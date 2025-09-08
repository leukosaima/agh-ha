using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace AdGuardHomeHA.Services;

public class WebhookHealthAggregator : IWebhookHealthAggregator
{
    private readonly ILogger<WebhookHealthAggregator> _logger;
    private readonly AppConfiguration _config;
    private readonly ConcurrentDictionary<string, ServiceHealthStatus> _serviceStatuses = new();
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
    private readonly Timer _staleCheckTimer;

    public event Action<string, bool>? ServiceHealthChanged;

    public WebhookHealthAggregator(
        ILogger<WebhookHealthAggregator> logger,
        IOptions<AppConfiguration> appConfig)
    {
        _logger = logger;
        _config = appConfig.Value;

        // Initialize service statuses for webhook-monitored services
        foreach (var service in _config.Services.Where(s => s.MonitoringMode == HealthSource.Webhook))
        {
            _serviceStatuses[service.Name] = new ServiceHealthStatus
            {
                ServiceName = service.Name,
                IsHealthy = false,
                LastUpdated = DateTime.UtcNow,
                Source = HealthSource.Webhook,
                GatusEndpoints = new Dictionary<string, bool>()
            };

            // Initialize each expected Gatus endpoint as unhealthy
            foreach (var endpointName in service.GatusEndpointNames)
            {
                _serviceStatuses[service.Name].GatusEndpoints[endpointName] = false;
                _serviceStatuses[service.Name].EndpointLastSeen[endpointName] = DateTime.MinValue; // Never seen initially
            }
        }

        // Start background timer to check for stale endpoints every 60 seconds
        var checkInterval = TimeSpan.FromSeconds(Math.Max(60, _config.Webhook.HealthStatusTimeoutSeconds / 5));
        _staleCheckTimer = new Timer(CheckStaleEndpointsCallback, null, checkInterval, checkInterval);
        
        _logger.LogDebug("Started stale endpoint checker with interval: {Interval} seconds", checkInterval.TotalSeconds);
    }

    public async Task ProcessWebhookAsync(GatusWebhookPayload payload)
    {
        if (payload?.EndpointName == null)
        {
            _logger.LogWarning("Received webhook with null endpoint name");
            return;
        }

        await _updateSemaphore.WaitAsync();
        try
        {
            // Find the service configuration that matches this endpoint
            var matchingService = _config.Services.FirstOrDefault(s => 
                s.MonitoringMode == HealthSource.Webhook && 
                s.GatusEndpointNames.Contains(payload.EndpointName));

            if (matchingService == null)
            {
                _logger.LogDebug("Received webhook for endpoint {EndpointName} but no matching service configuration found",
                    payload.EndpointName);
                return;
            }

            // Get or create service status
            if (!_serviceStatuses.TryGetValue(matchingService.Name, out var serviceStatus))
            {
                serviceStatus = new ServiceHealthStatus
                {
                    ServiceName = matchingService.Name,
                    IsHealthy = false,
                    LastUpdated = DateTime.UtcNow,
                    Source = HealthSource.Webhook,
                    GatusEndpoints = new Dictionary<string, bool>()
                };
                _serviceStatuses[matchingService.Name] = serviceStatus;
            }

            // Update the specific Gatus endpoint status and timestamp
            var previousEndpointStatus = serviceStatus.GatusEndpoints.GetValueOrDefault(payload.EndpointName, false);
            var now = DateTime.UtcNow;
            serviceStatus.GatusEndpoints[payload.EndpointName] = payload.Success;
            serviceStatus.EndpointLastSeen[payload.EndpointName] = now; // Track when this specific endpoint was last seen
            serviceStatus.LastUpdated = now;

            _logger.LogDebug("Updated Gatus endpoint {EndpointName} for service {ServiceName}: {Status}",
                payload.EndpointName, matchingService.Name, payload.Success);

            // Calculate overall service health based on aggregation rules
            var previousOverallHealth = serviceStatus.IsHealthy;
            serviceStatus.IsHealthy = CalculateServiceHealth(matchingService, serviceStatus);

            // Fire event if overall service health changed
            if (previousOverallHealth != serviceStatus.IsHealthy)
            {
                _logger.LogInformation("Service {ServiceName} health changed from {PreviousHealth} to {NewHealth}",
                    matchingService.Name, previousOverallHealth, serviceStatus.IsHealthy);

                ServiceHealthChanged?.Invoke(matchingService.Name, serviceStatus.IsHealthy);
            }
            else if (previousEndpointStatus != payload.Success)
            {
                _logger.LogInformation("Gatus endpoint {EndpointName} for service {ServiceName} changed from {PreviousStatus} to {NewStatus}, but overall service health remains {OverallHealth}",
                    payload.EndpointName, matchingService.Name, previousEndpointStatus, payload.Success, serviceStatus.IsHealthy);
            }
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public async Task<Dictionary<string, bool>> GetServiceHealthStatusAsync()
    {
        await _updateSemaphore.WaitAsync();
        try
        {
            var result = new Dictionary<string, bool>();
            foreach (var kvp in _serviceStatuses)
            {
                // Check if status is stale
                var service = _config.Services.FirstOrDefault(s => s.Name == kvp.Key);
                if (service != null && IsStatusStale(kvp.Value, service))
                {
                    _logger.LogWarning("Service {ServiceName} status is stale (last updated: {LastUpdated})",
                        kvp.Key, kvp.Value.LastUpdated);
                    result[kvp.Key] = false; // Consider stale status as unhealthy
                }
                else
                {
                    result[kvp.Key] = kvp.Value.IsHealthy;
                }
            }
            return result;
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    public async Task<bool> GetServiceHealthAsync(string serviceName)
    {
        await _updateSemaphore.WaitAsync();
        try
        {
            if (!_serviceStatuses.TryGetValue(serviceName, out var status))
            {
                _logger.LogDebug("Service {ServiceName} not found in webhook health status", serviceName);
                return false;
            }

            var service = _config.Services.FirstOrDefault(s => s.Name == serviceName);
            if (service != null && IsStatusStale(status, service))
            {
                _logger.LogWarning("Service {ServiceName} status is stale (last updated: {LastUpdated})",
                    serviceName, status.LastUpdated);
                return false;
            }

            return status.IsHealthy;
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }

    private bool CalculateServiceHealth(ServiceConfiguration service, ServiceHealthStatus status)
    {
        if (status.GatusEndpoints.Count == 0)
        {
            _logger.LogDebug("No Gatus endpoints configured for service {ServiceName}", service.Name);
            return false;
        }

        // Check for stale endpoints and mark them as unhealthy
        CheckAndUpdateStaleEndpoints(service, status);

        var healthyCount = status.GatusEndpoints.Values.Count(healthy => healthy);
        var isHealthy = healthyCount >= service.RequiredGatusEndpoints;

        _logger.LogDebug("Service {ServiceName} health calculation: {HealthyCount}/{TotalCount} endpoints healthy, required: {RequiredCount}, result: {IsHealthy}",
            service.Name, healthyCount, status.GatusEndpoints.Count, service.RequiredGatusEndpoints, isHealthy);

        return isHealthy;
    }

    private bool IsStatusStale(ServiceHealthStatus status, ServiceConfiguration service)
    {
        var timeoutThreshold = DateTime.UtcNow.AddSeconds(-_config.Webhook.HealthStatusTimeoutSeconds);
        return status.LastUpdated < timeoutThreshold;
    }

    private void CheckAndUpdateStaleEndpoints(ServiceConfiguration service, ServiceHealthStatus status)
    {
        var timeoutThreshold = DateTime.UtcNow.AddSeconds(-_config.Webhook.HealthStatusTimeoutSeconds);
        var staleEndpoints = new List<string>();
        var recoveredEndpoints = new List<string>();

        foreach (var endpointName in service.GatusEndpointNames)
        {
            var lastSeen = status.EndpointLastSeen.GetValueOrDefault(endpointName, DateTime.MinValue);
            var wasHealthy = status.GatusEndpoints.GetValueOrDefault(endpointName, false);
            
            if (lastSeen < timeoutThreshold)
            {
                // Endpoint is stale - mark as unhealthy
                if (wasHealthy)
                {
                    status.GatusEndpoints[endpointName] = false;
                    staleEndpoints.Add(endpointName);
                    
                    _logger.LogWarning("Gatus endpoint {EndpointName} for service {ServiceName} marked as stale (last seen: {LastSeen})",
                        endpointName, service.Name, lastSeen == DateTime.MinValue ? "never" : lastSeen.ToString());
                }
            }
            else if (!wasHealthy && lastSeen >= timeoutThreshold)
            {
                // Endpoint has recovered from being stale (but we don't change its health status here - that comes from actual webhook)
                // This is just for logging purposes
                var timeSinceLastWebhook = DateTime.UtcNow - lastSeen;
                if (timeSinceLastWebhook.TotalSeconds < 60) // Only log if it was recent
                {
                    recoveredEndpoints.Add(endpointName);
                }
            }
        }

        // Log summary of stale endpoint changes
        if (staleEndpoints.Count > 0)
        {
            _logger.LogWarning("Service {ServiceName}: {Count} endpoint(s) became stale: {StaleEndpoints}",
                service.Name, staleEndpoints.Count, string.Join(", ", staleEndpoints));
        }
        
        if (recoveredEndpoints.Count > 0)
        {
            _logger.LogInformation("Service {ServiceName}: {Count} endpoint(s) recovered from staleness: {RecoveredEndpoints}",
                service.Name, recoveredEndpoints.Count, string.Join(", ", recoveredEndpoints));
        }
    }

    private async void CheckStaleEndpointsCallback(object? state)
    {
        try
        {
            await _updateSemaphore.WaitAsync();
            
            var servicesWithHealthChanges = new List<string>();
            
            foreach (var kvp in _serviceStatuses)
            {
                var serviceName = kvp.Key;
                var serviceStatus = kvp.Value;
                var service = _config.Services.FirstOrDefault(s => s.Name == serviceName && s.MonitoringMode == HealthSource.Webhook);
                
                if (service != null)
                {
                    var previousHealth = serviceStatus.IsHealthy;
                    
                    // This will check for stale endpoints and update their status
                    var newHealth = CalculateServiceHealth(service, serviceStatus);
                    serviceStatus.IsHealthy = newHealth;
                    
                    if (previousHealth != newHealth)
                    {
                        servicesWithHealthChanges.Add(serviceName);
                        _logger.LogInformation("Background stale check: Service {ServiceName} health changed from {PreviousHealth} to {NewHealth}",
                            serviceName, previousHealth, newHealth);
                    }
                }
            }
            
            _updateSemaphore.Release();
            
            // Fire events for services that changed health status (outside the semaphore to avoid deadlock)
            foreach (var serviceName in servicesWithHealthChanges)
            {
                var currentHealth = _serviceStatuses[serviceName].IsHealthy;
                ServiceHealthChanged?.Invoke(serviceName, currentHealth);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during background stale endpoint check");
            _updateSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _staleCheckTimer?.Dispose();
        _updateSemaphore?.Dispose();
    }
}
