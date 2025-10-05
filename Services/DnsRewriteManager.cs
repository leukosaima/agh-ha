using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

public class DnsRewriteManager : IDnsRewriteManager
{
    private readonly IAdGuardHomeClient _adGuardClient;
    private readonly ILogger<DnsRewriteManager> _logger;
    private readonly AppConfiguration _config;
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);

    public DnsRewriteManager(
        IAdGuardHomeClient adGuardClient,
        IOptions<AppConfiguration> appConfig,
        ILogger<DnsRewriteManager> logger)
    {
        _adGuardClient = adGuardClient;
        _logger = logger;
        _config = appConfig.Value;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Initializing DNS rewrite manager");

            // Test AdGuard Home connection first
            if (!await _adGuardClient.TestConnectionAsync())
            {
                throw new InvalidOperationException("Cannot connect to AdGuard Home");
            }

            _logger.LogInformation("DNS rewrite manager initialized successfully");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Failed to initialize DNS rewrite manager: {ErrorMessage}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to initialize DNS rewrite manager: {ErrorMessage}", ex.Message);
            throw;
        }
    }



    public async Task UpdateSingleRewriteAsync(string domain, string? targetIpAddress)
    {
        if (string.IsNullOrEmpty(targetIpAddress))
        {
            _logger.LogWarning("No target IP address provided for domain {Domain} - cannot update DNS rewrite", domain);
            return;
        }

        await _updateSemaphore.WaitAsync();
        try
        {
            _logger.LogInformation("Updating DNS rewrite for domain {Domain} to {TargetIp}", domain, targetIpAddress);
            
            var success = await UpdateSingleRewriteInternalAsync(domain, targetIpAddress);
            
            if (success)
            {
                _logger.LogInformation("Successfully updated DNS rewrite: {Domain} -> {TargetIp}", domain, targetIpAddress);
            }
            else
            {
                _logger.LogError("Failed to update DNS rewrite: {Domain} -> {TargetIp}", domain, targetIpAddress);
            }
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    private async Task<bool> UpdateSingleRewriteInternalAsync(string domain, string targetIpAddress)
    {
        try
        {
            var success = await _adGuardClient.UpdateRewriteAsync(domain, targetIpAddress);
            
            if (success)
            {
                _logger.LogDebug("Successfully updated rewrite: {Domain} -> {IpAddress}", domain, targetIpAddress);
            }
            else
            {
                _logger.LogError("Failed to update rewrite: {Domain} -> {IpAddress}", domain, targetIpAddress);
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error updating rewrite {Domain} -> {IpAddress}: {ErrorMessage}", domain, targetIpAddress, ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _updateSemaphore?.Dispose();
    }
}
