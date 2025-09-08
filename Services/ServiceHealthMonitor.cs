using System.Diagnostics;
using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AdGuardHomeHA.Services;

public class ServiceHealthMonitor : IServiceHealthMonitor
{
    private readonly ILogger<ServiceHealthMonitor> _logger;
    private readonly AppConfiguration _config;
    private readonly ConcurrentDictionary<string, bool> _serviceStatus = new();
    private readonly ConcurrentDictionary<string, bool> _ipHealthCache = new();
    private readonly SemaphoreSlim _statusSemaphore = new(1, 1);
    private readonly IGatusPollingHealthMonitor? _gatusPollingMonitor;

    public event Action<string, bool>? ServiceStatusChanged;

    public ServiceHealthMonitor(
        IOptions<AppConfiguration> appConfig,
        ILogger<ServiceHealthMonitor> logger,
        IGatusPollingHealthMonitor? gatusPollingMonitor = null)
    {
        _logger = logger;
        _config = appConfig.Value;
        _gatusPollingMonitor = gatusPollingMonitor;

        // Initialize all services as unknown status
        foreach (var service in _config.Services)
        {
            _serviceStatus[service.Name] = false;
        }

        // Subscribe to Gatus polling health changes if available
        if (_gatusPollingMonitor != null)
        {
            _gatusPollingMonitor.ServiceHealthChanged += OnServiceHealthChanged;
        }
    }

    public async Task<Dictionary<string, bool>> GetAllServiceHealthAsync()
    {
        var allStatuses = new Dictionary<string, bool>();

        // Get Gatus-based service statuses
        if (_gatusPollingMonitor != null)
        {
            var gatusStatuses = await _gatusPollingMonitor.GetServiceHealthStatusAsync();
            foreach (var kvp in gatusStatuses)
            {
                allStatuses[kvp.Key] = kvp.Value;
            }
        }

        // Get ping-based service statuses
        foreach (var service in _config.Services.Where(s => s.MonitoringMode == HealthSource.Ping))
        {
            allStatuses[service.Name] = _serviceStatus.GetValueOrDefault(service.Name, false);
        }

        return allStatuses;
    }

    public async Task<Dictionary<string, bool>> CheckAllServicesAsync()
    {
        var pingServices = _config.Services.Where(s => s.MonitoringMode == HealthSource.Ping).ToArray();
        
        if (pingServices.Length == 0)
        {
            _logger.LogDebug("No ping-based services to check");
            return new Dictionary<string, bool>();
        }

        // Group services by IP address for deduplication
        var servicesByIp = pingServices.GroupBy(s => s.IpAddress).ToArray();
        
        _logger.LogDebug("Checking {ServiceCount} services across {IpCount} unique IP addresses", 
            pingServices.Length, servicesByIp.Length);

        // Ping each unique IP address
        var ipHealthTasks = servicesByIp.Select(async ipGroup =>
        {
            var ipAddress = ipGroup.Key;
            var servicesForIp = ipGroup.ToArray();
            
            // Use the most restrictive timeout from all services sharing this IP
            var maxTimeout = servicesForIp.Max(s => s.TimeoutMs);
            
            var isHealthy = await PingIpAddressAsync(ipAddress, maxTimeout);
            _ipHealthCache[ipAddress] = isHealthy;
            
            _logger.LogDebug("IP {IpAddress} health check result: {IsHealthy} (affects {ServiceCount} services)",
                ipAddress, isHealthy, servicesForIp.Length);
                
            return new { IpAddress = ipAddress, IsHealthy = isHealthy, Services = servicesForIp };
        });

        var ipResults = await Task.WhenAll(ipHealthTasks);
        var serviceResults = new Dictionary<string, bool>();

        // Apply IP health results to all services sharing that IP
        foreach (var ipResult in ipResults)
        {
            foreach (var service in ipResult.Services)
            {
                serviceResults[service.Name] = ipResult.IsHealthy;
                await UpdateServiceStatusAsync(service.Name, ipResult.IsHealthy);
            }
        }

        return serviceResults;
    }

    private async Task<bool> PingIpAddressAsync(string ipAddress, int timeoutMs)
    {
        try
        {
            _logger.LogDebug("Pinging IP address {IpAddress} with timeout {TimeoutMs}ms", 
                ipAddress, timeoutMs);

            for (int attempt = 1; attempt <= _config.Monitoring.RetryAttempts; attempt++)
            {
                try
                {
                    var isReachable = await PingHostAsync(ipAddress, timeoutMs);
                    
                    if (isReachable)
                    {
                        _logger.LogDebug("IP {IpAddress} is reachable (ping successful)", ipAddress);
                        return true;
                    }

                    _logger.LogDebug("Ping attempt {Attempt} failed for IP {IpAddress}",
                        attempt, ipAddress);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Ping attempt {Attempt} failed for IP {IpAddress}: {Error}",
                        attempt, ipAddress, ex.Message);
                }

                if (attempt < _config.Monitoring.RetryAttempts)
                {
                    await Task.Delay(_config.Monitoring.RetryDelayMs);
                }
            }

            _logger.LogWarning("IP {IpAddress} is unreachable after {Attempts} attempts",
                ipAddress, _config.Monitoring.RetryAttempts);
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pinging IP {IpAddress}", ipAddress);
            return false;
        }
    }
    
    private async Task<bool> PingHostAsync(string hostAddress, int timeoutMs)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "ping";
            process.StartInfo.Arguments = $"-c 1 -W {timeoutMs / 1000} {hostAddress}"; // -c 1: send 1 packet, -W: timeout in seconds
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.Start();
            
            // Set a timeout slightly longer than the ping timeout
            var timeoutTask = Task.Delay(timeoutMs + 1000);
            var processTask = process.WaitForExitAsync();
            
            var completedTask = await Task.WhenAny(processTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                // Process timed out
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore kill errors
                }
                return false;
            }
            
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error executing ping command: {Error}", ex.Message);
            return false;
        }
    }

    public string? GetBestAvailableService()
    {
        var availableTargets = new List<(string Name, string IpAddress, int Priority)>();

        // Add ping-based services that are healthy
        foreach (var service in _config.Services.Where(s => s.MonitoringMode == HealthSource.Ping && _serviceStatus.GetValueOrDefault(s.Name, false)))
        {
            availableTargets.Add((service.Name, service.IpAddress, service.Priority));
        }

        // Add Gatus-based services that are healthy
        foreach (var service in _config.Services.Where(s => s.MonitoringMode == HealthSource.Gatus))
        {
            var isHealthy = _gatusPollingMonitor?.GetServiceHealthAsync(service.Name).Result ?? false;
            if (isHealthy)
            {
                availableTargets.Add((service.Name, service.IpAddress, service.Priority));
            }
        }

        if (availableTargets.Count == 0)
        {
            _logger.LogWarning("No healthy services available");
            return null;
        }

        var bestTarget = availableTargets.OrderBy(t => t.Priority).First();
        _logger.LogDebug("Best available service: {ServiceName} at {IpAddress} (priority {Priority})",
            bestTarget.Name, bestTarget.IpAddress, bestTarget.Priority);
        
        return bestTarget.IpAddress;
    }

    private async Task UpdateServiceStatusAsync(string serviceName, bool isHealthy)
    {
        await _statusSemaphore.WaitAsync();
        try
        {
            var previousStatus = _serviceStatus.GetValueOrDefault(serviceName, false);
            _serviceStatus[serviceName] = isHealthy;

            // Fire event if status changed
            if (previousStatus != isHealthy)
            {
                _logger.LogInformation("Service {ServiceName} status changed: {PreviousStatus} -> {NewStatus}",
                    serviceName, previousStatus ? "Healthy" : "Unhealthy", isHealthy ? "Healthy" : "Unhealthy");
                
                ServiceStatusChanged?.Invoke(serviceName, isHealthy);
            }
        }
        finally
        {
            _statusSemaphore.Release();
        }
    }


    private async void OnServiceHealthChanged(string serviceName, bool isHealthy)
    {
        await _statusSemaphore.WaitAsync();
        try
        {
            var previousStatus = _serviceStatus.GetValueOrDefault(serviceName, false);
            _serviceStatus[serviceName] = isHealthy;

            _logger.LogInformation("Gatus service {ServiceName} health changed to {Status}",
                serviceName, isHealthy ? "Healthy" : "Unhealthy");

            // Always fire the service status changed event for Gatus services
            ServiceStatusChanged?.Invoke(serviceName, isHealthy);
        }
        finally
        {
            _statusSemaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_gatusPollingMonitor != null)
        {
            _gatusPollingMonitor.ServiceHealthChanged -= OnServiceHealthChanged;
        }
        _statusSemaphore?.Dispose();
    }
}
