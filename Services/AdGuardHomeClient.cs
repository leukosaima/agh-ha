using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

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
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            _logger.LogDebug("Testing connection to AdGuard Home at {BaseUrl}", _config.BaseUrl);
            
            // First ensure we're authenticated since /control/status requires auth
            if (!await EnsureAuthenticatedAsync())
            {
                _logger.LogError("Failed to authenticate with AdGuard Home for status check");
                return false;
            }
            
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Error testing connection to AdGuard Home: {ErrorMessage}", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error testing connection to AdGuard Home: {ErrorMessage}", ex.Message);
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
                var rewriteEntries = JsonSerializer.Deserialize<RewriteEntry[]>(content);
                return rewriteEntries ?? Array.Empty<RewriteEntry>();
            }
            
            _logger.LogWarning("Failed to get rewrites: {StatusCode}", response.StatusCode);
            return Array.Empty<RewriteEntry>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Error getting rewrites from AdGuard Home: {ErrorMessage}", ex.Message);
            return Array.Empty<RewriteEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error getting rewrites from AdGuard Home: {ErrorMessage}", ex.Message);
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

            var rewrite = new RewriteEntry { Domain = domain, Answer = ipAddress };

            var json = JsonSerializer.Serialize(rewrite);
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Error adding rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, ipAddress, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error adding rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, ipAddress, ex.Message);
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

            var rewrite = new RewriteEntry { Domain = domain, Answer = ipAddress };

            var json = JsonSerializer.Serialize(rewrite);
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Error deleting rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, ipAddress, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error deleting rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, ipAddress, ex.Message);
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
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Error updating rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, newIpAddress, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error updating rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, newIpAddress, ex.Message);
            return false;
        }
    }

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        await _authSemaphore.WaitAsync();
        try
        {
            // If we already have a session cookie, assume we're authenticated
            if (!string.IsNullOrEmpty(_sessionCookie) && _sessionCookie != "retry")
            {
                return true;
            }

            _logger.LogDebug("Authenticating with AdGuard Home using username: {Username}", _config.Username);

            var loginRequest = new LoginRequest
            {
                Name = _config.Username,
                Password = _config.Password
            };

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(loginRequest, jsonOptions);
            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            
            _logger.LogDebug("Login request JSON: {Json}", json);
            _logger.LogDebug("JSON length: {Length} bytes", json.Length);
            _logger.LogDebug("Content-Type: {ContentType}", content.Headers.ContentType?.ToString());
            
            _logger.LogDebug("Sending login request to /control/login");
            
            // Log all request headers
            _logger.LogDebug("Request headers:");
            foreach (var header in _httpClient.DefaultRequestHeaders)
            {
                _logger.LogDebug("  {HeaderName}: {HeaderValue}", header.Key, string.Join(", ", header.Value));
            }
            
            var response = await _httpClient.PostAsync("/control/login", content);
            
            _logger.LogDebug("Login response status: {StatusCode}", response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                // Log all response headers for debugging
                _logger.LogDebug("All response headers:");
                foreach (var header in response.Headers)
                {
                    _logger.LogDebug("  {HeaderName}: {HeaderValue}", header.Key, string.Join(", ", header.Value));
                }
                
                // Check response content headers too
                if (response.Content.Headers.Any())
                {
                    _logger.LogDebug("Content headers:");
                    foreach (var header in response.Content.Headers)
                    {
                        _logger.LogDebug("  {HeaderName}: {HeaderValue}", header.Key, string.Join(", ", header.Value));
                    }
                }
                
                // Read response body
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogDebug("Login response body: {ResponseBody}", responseBody);
                
                // Extract session cookies from response headers
                var allCookies = new List<string>();
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    allCookies.AddRange(cookies);
                }
                
                _logger.LogDebug("Found {CookieCount} cookies: {Cookies}", allCookies.Count, string.Join("; ", allCookies));
                
                // Look for session cookie (try different patterns AdGuard Home might use)
                var sessionCookie = allCookies.FirstOrDefault(c => 
                    c.StartsWith("agh_session=") || 
                    c.StartsWith("session=") ||
                    c.StartsWith("adguard_session=") ||
                    c.Contains("session"));
                    
                if (!string.IsNullOrEmpty(sessionCookie))
                {
                    // Extract just the cookie name=value part (before any semicolon)
                    _sessionCookie = sessionCookie.Split(';')[0];
                    
                    // Clear any existing Cookie headers and add the new one
                    _httpClient.DefaultRequestHeaders.Remove("Cookie");
                    _httpClient.DefaultRequestHeaders.Add("Cookie", _sessionCookie);
                    
                    _logger.LogInformation("Successfully authenticated with AdGuard Home using cookie: {Cookie}", 
                        _sessionCookie);
                    return true;
                }
                
                // If no session cookie found but login was successful, 
                // maybe AdGuard Home uses Basic Auth or doesn't need cookies
                _logger.LogWarning("Login successful but no session cookie found. Trying without cookie-based auth.");
                _sessionCookie = "no-cookie-auth";
                return true;
            }
            
            // Read error response body
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to authenticate with AdGuard Home: {StatusCode}, Response: {ErrorBody}", 
                response.StatusCode, errorBody);
                
            // Mark for retry next time
            _sessionCookie = "retry";
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Error authenticating with AdGuard Home: {ErrorMessage}", ex.Message);
            _sessionCookie = "retry";
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error authenticating with AdGuard Home: {ErrorMessage}", ex.Message);
            _sessionCookie = "retry";
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
