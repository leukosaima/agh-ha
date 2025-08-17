using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

public interface IAdGuardHomeClient
{
    Task<bool> TestConnectionAsync();
    Task<RewriteEntry[]> GetRewritesAsync();
    Task<bool> AddRewriteAsync(string domain, string ipAddress);
    Task<bool> DeleteRewriteAsync(string domain, string ipAddress);
    Task<bool> UpdateRewriteAsync(string domain, string newIpAddress);
}

public class AdGuardHomeClient : IAdGuardHomeClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AdGuardHomeClient> _logger;
    private readonly AdGuardHomeConfiguration _config;
    private readonly SemaphoreSlim _authSemaphore = new(1, 1);
    private string? _sessionCookie;

    public AdGuardHomeClient(
        HttpClient httpClient,
        IOptions<AppConfiguration> appConfig,
        ILogger<AdGuardHomeClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = appConfig.Value.AdGuardHome;
        
        _httpClient.BaseAddress = new Uri(_config.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            _logger.LogDebug("Testing connection to AdGuard Home at {BaseUrl}", _config.BaseUrl);
            
            var response = await _httpClient.GetAsync("/control/status");
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var status = JsonSerializer.Deserialize<StatusResponse>(content);
                
                _logger.LogInformation("Connected to AdGuard Home v{Version}, Running: {Running}",
                    status?.Version ?? "Unknown", status?.Running ?? false);
                
                return status?.Running == true;
            }
            
            _logger.LogWarning("Failed to connect to AdGuard Home: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing connection to AdGuard Home");
            return false;
        }
    }

    public async Task<RewriteEntry[]> GetRewritesAsync()
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogError("Failed to authenticate with AdGuard Home");
                return Array.Empty<RewriteEntry>();
            }

            var response = await _httpClient.GetAsync("/control/rewrite/list");
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var rewritesResponse = JsonSerializer.Deserialize<RewritesResponse>(content);
                return rewritesResponse?.DomainRewrites ?? Array.Empty<RewriteEntry>();
            }
            
            _logger.LogWarning("Failed to get rewrites: {StatusCode}", response.StatusCode);
            return Array.Empty<RewriteEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting rewrites from AdGuard Home");
            return Array.Empty<RewriteEntry>();
        }
    }

    public async Task<bool> AddRewriteAsync(string domain, string ipAddress)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogError("Failed to authenticate with AdGuard Home");
                return false;
            }

            var rewrite = new RewriteRequest
            {
                Target = new RewriteEntry { Domain = domain, Answer = ipAddress }
            };

            var json = JsonSerializer.Serialize(rewrite);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/control/rewrite/add", content);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully added rewrite: {Domain} -> {IpAddress}", domain, ipAddress);
                return true;
            }
            
            _logger.LogWarning("Failed to add rewrite {Domain} -> {IpAddress}: {StatusCode}",
                domain, ipAddress, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding rewrite {Domain} -> {IpAddress}", domain, ipAddress);
            return false;
        }
    }

    public async Task<bool> DeleteRewriteAsync(string domain, string ipAddress)
    {
        try
        {
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogError("Failed to authenticate with AdGuard Home");
                return false;
            }

            var rewrite = new RewriteRequest
            {
                Target = new RewriteEntry { Domain = domain, Answer = ipAddress }
            };

            var json = JsonSerializer.Serialize(rewrite);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/control/rewrite/delete", content);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully deleted rewrite: {Domain} -> {IpAddress}", domain, ipAddress);
                return true;
            }
            
            _logger.LogWarning("Failed to delete rewrite {Domain} -> {IpAddress}: {StatusCode}",
                domain, ipAddress, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting rewrite {Domain} -> {IpAddress}", domain, ipAddress);
            return false;
        }
    }

    public async Task<bool> UpdateRewriteAsync(string domain, string newIpAddress)
    {
        try
        {
            // Get current rewrites to find the old entry
            var currentRewrites = await GetRewritesAsync();
            var existingRewrite = currentRewrites.FirstOrDefault(r => r.Domain == domain);
            
            if (existingRewrite == null)
            {
                _logger.LogInformation("Rewrite for {Domain} doesn't exist, creating new one", domain);
                return await AddRewriteAsync(domain, newIpAddress);
            }
            
            if (existingRewrite.Answer == newIpAddress)
            {
                _logger.LogDebug("Rewrite for {Domain} already points to {IpAddress}", domain, newIpAddress);
                return true;
            }
            
            // Delete old rewrite and add new one
            var deleteSuccess = await DeleteRewriteAsync(domain, existingRewrite.Answer);
            if (!deleteSuccess)
            {
                _logger.LogError("Failed to delete existing rewrite for {Domain}", domain);
                return false;
            }
            
            var addSuccess = await AddRewriteAsync(domain, newIpAddress);
            if (addSuccess)
            {
                _logger.LogInformation("Successfully updated rewrite: {Domain} {OldIp} -> {NewIp}",
                    domain, existingRewrite.Answer, newIpAddress);
            }
            
            return addSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating rewrite {Domain} -> {IpAddress}", domain, newIpAddress);
            return false;
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        await _authSemaphore.WaitAsync();
        try
        {
            // If we already have a session cookie, assume we're authenticated
            if (!string.IsNullOrEmpty(_sessionCookie))
            {
                return true;
            }

            _logger.LogDebug("Authenticating with AdGuard Home");

            var loginRequest = new LoginRequest
            {
                Name = _config.Username,
                Password = _config.Password
            };

            var json = JsonSerializer.Serialize(loginRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/control/login", content);
            
            if (response.IsSuccessStatusCode)
            {
                // Extract session cookie from response headers
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    var sessionCookie = cookies.FirstOrDefault(c => c.StartsWith("agh_session"));
                    if (!string.IsNullOrEmpty(sessionCookie))
                    {
                        _sessionCookie = sessionCookie.Split(';')[0];
                        _httpClient.DefaultRequestHeaders.Add("Cookie", _sessionCookie);
                        _logger.LogInformation("Successfully authenticated with AdGuard Home");
                        return true;
                    }
                }
                
                _logger.LogWarning("Authentication succeeded but no session cookie received");
                return false;
            }
            
            _logger.LogError("Failed to authenticate with AdGuard Home: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error authenticating with AdGuard Home");
            return false;
        }
        finally
        {
            _authSemaphore.Release();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _authSemaphore?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
