using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

public interface IDnsRewriteManager
{
    Task InitializeAsync();
    Task UpdateRewritesAsync(string? targetIpAddress);
    Task<string?> GetCurrentTargetAsync();
}

public class DnsRewriteManager : IDnsRewriteManager
{
    private readonly IAdGuardHomeClient _adGuardClient;
    private readonly ILogger<DnsRewriteManager> _logger;
    private readonly AppConfiguration _config;
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
    private string? _currentTargetIp;

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

            // Get current rewrites to determine the current target
            await DetermineCurrentTargetAsync();

            _logger.LogInformation("DNS rewrite manager initialized successfully. Current target: {CurrentTarget}",
                _currentTargetIp ?? "None");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DNS rewrite manager");
            throw;
        }
    }

    public async Task UpdateRewritesAsync(string? targetIpAddress)
    {
        await _updateSemaphore.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(targetIpAddress))
            {
                _logger.LogWarning("No target IP address provided - cannot update DNS rewrites");
                return;
            }

            // Skip update if already pointing to the same IP
            if (_currentTargetIp == targetIpAddress)
            {
                _logger.LogDebug("DNS rewrites already point to {TargetIp}, skipping update", targetIpAddress);
                return;
            }

            _logger.LogInformation("Updating DNS rewrites to point to {TargetIp} (was {PreviousIp})",
                targetIpAddress, _currentTargetIp ?? "None");

            var updateTasks = _config.DnsRewrites.Select(domain =>
                UpdateSingleRewriteAsync(domain, targetIpAddress));

            var results = await Task.WhenAll(updateTasks);
            var successCount = results.Count(r => r);

            if (successCount == _config.DnsRewrites.Length)
            {
                _currentTargetIp = targetIpAddress;
                _logger.LogInformation("Successfully updated all {Count} DNS rewrites to {TargetIp}",
                    successCount, targetIpAddress);
            }
            else
            {
                _logger.LogError("Only {SuccessCount} out of {TotalCount} DNS rewrites were updated successfully",
                    successCount, _config.DnsRewrites.Length);
            }
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public async Task<string?> GetCurrentTargetAsync()
    {
        // If we don't know the current target, try to determine it
        if (string.IsNullOrEmpty(_currentTargetIp))
        {
            await DetermineCurrentTargetAsync();
        }

        return _currentTargetIp;
    }

    private async Task<bool> UpdateSingleRewriteAsync(string domain, string targetIpAddress)
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
            _logger.LogError(ex, "Error updating rewrite {Domain} -> {IpAddress}", domain, targetIpAddress);
            return false;
        }
    }

    private async Task DetermineCurrentTargetAsync()
    {
        try
        {
            _logger.LogDebug("Determining current target IP from existing rewrites");
            
            var existingRewrites = await _adGuardClient.GetRewritesAsync();
            
            // Look for our configured domains in the existing rewrites
            var ourRewrites = existingRewrites
                .Where(r => _config.DnsRewrites.Contains(r.Domain))
                .ToArray();

            if (ourRewrites.Length == 0)
            {
                _logger.LogInformation("No existing rewrites found for configured domains");
                _currentTargetIp = null;
                return;
            }

            // Group by target IP to see what they're currently pointing to
            var targetGroups = ourRewrites.GroupBy(r => r.Answer).ToArray();
            
            if (targetGroups.Length == 1)
            {
                // All domains point to the same IP
                _currentTargetIp = targetGroups[0].Key;
                _logger.LogInformation("All configured domains currently point to {CurrentTarget}", _currentTargetIp);
            }
            else
            {
                // Mixed targets - this shouldn't normally happen
                _logger.LogWarning("Configured domains point to multiple targets: {Targets}",
                    string.Join(", ", targetGroups.Select(g => $"{g.Key} ({g.Count()} domains)")));
                
                // Use the most common target as current
                var mostCommonTarget = targetGroups.OrderByDescending(g => g.Count()).First();
                _currentTargetIp = mostCommonTarget.Key;
                
                _logger.LogInformation("Using most common target as current: {CurrentTarget} ({Count} domains)",
                    _currentTargetIp, mostCommonTarget.Count());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error determining current target IP");
            _currentTargetIp = null;
        }
    }

    public void Dispose()
    {
        _updateSemaphore?.Dispose();
    }
}
