using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AdGuardHomeHA.Services;

public interface IGatusPollingHealthMonitor : IDisposable
{
    Task<Dictionary<string, bool>> GetServiceHealthStatusAsync();
    Task<bool> GetServiceHealthAsync(string serviceName);
    event Action<string, bool>? ServiceHealthChanged;
}

public class GatusPollingHealthMonitor : IGatusPollingHealthMonitor
{
    private readonly ILogger<GatusPollingHealthMonitor> _logger;
    private readonly AppConfiguration _config;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ServiceHealthStatus> _serviceStatuses = new();
    private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
    private readonly Timer _pollingTimer;
    
    public event Action<string, bool>? ServiceHealthChanged;
    
    public GatusPollingHealthMonitor(
        ILogger<GatusPollingHealthMonitor> logger,
        IOptions<AppConfiguration> appConfig,
        HttpClient httpClient)
    {
        _logger = logger;
        _config = appConfig.Value;
        _httpClient = httpClient;
        
        // Configure HttpClient timeout
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        
        // Initialize service statuses for Gatus-monitored services
        foreach (var service in _config.Services.Where(s => s.MonitoringMode == HealthSource.Gatus))
        {
            _serviceStatuses[service.Name] = new ServiceHealthStatus
            {
                ServiceName = service.Name,
                IsHealthy = false,
                LastUpdated = DateTime.UtcNow,
                Source = HealthSource.Gatus,
                GatusEndpoints = new Dictionary<string, bool>()
            };
            
            // Initialize each expected Gatus endpoint as unhealthy
            foreach (var endpointName in service.GatusEndpointNames)
            {
                _serviceStatuses[service.Name].GatusEndpoints[endpointName] = false;
                _serviceStatuses[service.Name].EndpointLastSeen[endpointName] = DateTime.MinValue;
            }
        }
        
        // Start polling timer - default to 30 seconds if not configured
        var pollingInterval = TimeSpan.FromSeconds(_config.GatusPolling?.IntervalSeconds ?? 30);
        _pollingTimer = new Timer(PollGatusInstancesCallback, null, TimeSpan.Zero, pollingInterval);
        
        _logger.LogInformation("Started Gatus polling monitor with interval: {Interval} seconds", pollingInterval.TotalSeconds);
    }
    
    public async Task<Dictionary<string, bool>> GetServiceHealthStatusAsync()
    {
        await _updateSemaphore.WaitAsync();
        try
        {
            return _serviceStatuses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.IsHealthy);
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
                _logger.LogDebug("Service {ServiceName} not found in Gatus health status", serviceName);
                return false;
            }
            
            return status.IsHealthy;
        }
        finally
        {
            _updateSemaphore.Release();
        }
    }
    
    private async void PollGatusInstancesCallback(object? state)
    {
        try
        {
            var healthChanges = new List<(string serviceName, bool newHealth)>();
            
            await _updateSemaphore.WaitAsync();
            
            foreach (var service in _config.Services.Where(s => s.MonitoringMode == HealthSource.Gatus))
            {
                var previousHealth = _serviceStatuses[service.Name].IsHealthy;
                var newHealth = await PollServiceEndpointsAsync(service);
                
                _serviceStatuses[service.Name].IsHealthy = newHealth;
                _serviceStatuses[service.Name].LastUpdated = DateTime.UtcNow;
                
                if (previousHealth != newHealth)
                {
                    healthChanges.Add((service.Name, newHealth));
                }
            }
            
            _updateSemaphore.Release();
            
            // Fire events for services that changed health status (outside semaphore to avoid deadlock)
            foreach (var (serviceName, newHealth) in healthChanges)
            {
                _logger.LogInformation("Service {ServiceName} health changed to {NewHealth}", serviceName, newHealth);
                ServiceHealthChanged?.Invoke(serviceName, newHealth);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Gatus polling");
            if (_updateSemaphore.CurrentCount == 0)
            {
                _updateSemaphore.Release();
            }
        }
    }
    
    private async Task<bool> PollServiceEndpointsAsync(ServiceConfiguration service)
    {
        var endpointResults = new Dictionary<string, bool>();
        var now = DateTime.UtcNow;
        
        // Poll each Gatus instance for the service's endpoints
        foreach (var gatusUrl in service.GatusInstanceUrls)
        {
            foreach (var endpointName in service.GatusEndpointNames)
            {
                try
                {
                    var endpointStatus = await GetGatusEndpointStatusAsync(gatusUrl, endpointName);
                    if (endpointStatus.HasValue)
                    {
                        endpointResults[endpointName] = endpointStatus.Value;
                        _serviceStatuses[service.Name].EndpointLastSeen[endpointName] = now;
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get status for endpoint {EndpointName} from Gatus instance {GatusUrl}",
                            endpointName, gatusUrl);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error polling endpoint {EndpointName} from Gatus instance {GatusUrl}",
                        endpointName, gatusUrl);
                }
            }
        }
        
        // Update the service status with polled results
        foreach (var kvp in endpointResults)
        {
            _serviceStatuses[service.Name].GatusEndpoints[kvp.Key] = kvp.Value;
        }
        
        // Calculate overall service health
        var healthyCount = endpointResults.Values.Count(healthy => healthy);
        var isServiceHealthy = healthyCount >= service.RequiredGatusEndpoints;
        
        _logger.LogDebug("Service {ServiceName} health calculation: {HealthyCount}/{TotalCount} endpoints healthy, required: {RequiredCount}, result: {IsHealthy}",
            service.Name, healthyCount, endpointResults.Count, service.RequiredGatusEndpoints, isServiceHealthy);
        
        return isServiceHealthy;
    }
    
    private async Task<bool?> GetGatusEndpointStatusAsync(string gatusUrl, string endpointName)
    {
        try
        {
            // Use individual endpoint query for better efficiency: GET /api/v1/endpoints/{key}/statuses
            // This reduces network traffic compared to fetching all endpoints
            var apiUrl = $"{gatusUrl.TrimEnd('/')}/api/v1/endpoints/{endpointName}/statuses";
            
            var response = await _httpClient.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gatus API returned {StatusCode} for endpoint {EndpointName} at {GatusUrl}", 
                    response.StatusCode, endpointName, apiUrl);
                return null;
            }
            
            var jsonContent = await response.Content.ReadAsStringAsync();
            var endpointStatus = JsonSerializer.Deserialize<GatusEndpointStatus>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (endpointStatus != null)
            {
                // Only check the most recent result (first in array) for efficiency
                var latestResult = endpointStatus.Results?.FirstOrDefault();
                var isHealthy = latestResult?.Success == true;
                
                _logger.LogDebug("Polled Gatus endpoint {EndpointName} from {GatusUrl}: {Status} (timestamp: {Timestamp})",
                    endpointName, gatusUrl, isHealthy, latestResult?.Timestamp);
                    
                return isHealthy;
            }
            
            _logger.LogDebug("Endpoint {EndpointName} not found in Gatus instance {GatusUrl}", endpointName, gatusUrl);
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout polling Gatus instance {GatusUrl} for endpoint {EndpointName}", gatusUrl, endpointName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting endpoint status from Gatus {GatusUrl}", gatusUrl);
            return null;
        }
    }
    
    public void Dispose()
    {
        _pollingTimer?.Dispose();
        _updateSemaphore?.Dispose();
    }
}

// DTO for Gatus API response
public class GatusEndpointStatus
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Key { get; set; } = "";
    public List<GatusResult>? Results { get; set; }
}

public class GatusResult
{
    public string Hostname { get; set; } = "";
    public long Duration { get; set; }
    public bool Success { get; set; }
    public DateTime Timestamp { get; set; }
    public List<GatusConditionResult>? ConditionResults { get; set; }
}

public class GatusConditionResult
{
    public string Condition { get; set; } = "";
    public bool Success { get; set; }
}
