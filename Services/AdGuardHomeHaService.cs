using AdGuardHomeHA.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

public class AdGuardHomeHaService : BackgroundService
{
    private readonly IServiceHealthMonitor _healthMonitor;
    private readonly IDnsRewriteManager _dnsManager;
    private readonly ILogger<AdGuardHomeHaService> _logger;
    private readonly AppConfiguration _config;

    public AdGuardHomeHaService(
        IServiceHealthMonitor healthMonitor,
        IDnsRewriteManager dnsManager,
        IOptions<AppConfiguration> appConfig,
        ILogger<AdGuardHomeHaService> logger)
    {
        _healthMonitor = healthMonitor;
        _dnsManager = dnsManager;
        _logger = logger;
        _config = appConfig.Value;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting AdGuard Home High Availability service");

        try
        {
            // Validate configuration
            ValidateConfiguration();

            // Initialize DNS rewrite manager
            await _dnsManager.InitializeAsync();

            // Subscribe to service status changes
            _healthMonitor.ServiceStatusChanged += OnServiceStatusChanged;

            _logger.LogInformation("AdGuard Home HA service started successfully");
            var totalRewrites = _config.Services.Sum(s => s.DnsRewrites.Length);
            _logger.LogInformation("Monitoring {ServiceCount} services with {RewriteCount} total DNS rewrites",
                _config.Services.Length, totalRewrites);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to start AdGuard Home HA service");
            throw;
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping AdGuard Home High Availability service");
        
        // Unsubscribe from events
        _healthMonitor.ServiceStatusChanged -= OnServiceStatusChanged;

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("AdGuard Home HA service stopped");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingServices = _config.Services.Where(s => s.MonitoringMode == HealthSource.Ping).ToArray();
        
        if (pingServices.Length > 0)
        {
            _logger.LogInformation("Starting ping monitoring loop with {Interval}s interval for {ServiceCount} ping-based services", 
                _config.Monitoring.CheckIntervalSeconds, pingServices.Length);

            // Perform initial health check
            await PerformHealthCheckCycle();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Monitoring.CheckIntervalSeconds), stoppingToken);
                    
                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await PerformHealthCheckCycle();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error in ping monitoring loop: {ErrorMessage}", ex.Message);
                    
                    // Wait a shorter interval before retrying on error
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("Ping monitoring loop ended");
        }
        else
        {
            var gatusServices = _config.Services.Where(s => s.MonitoringMode == HealthSource.Gatus).ToArray();
            _logger.LogInformation("No ping-based services configured. Running in Gatus-only mode with {ServiceCount} Gatus services. DNS updates will be triggered by Gatus polling events.", gatusServices.Length);
            
            // Perform initial DNS setup for Gatus services
            await UpdateAllServiceDnsRewrites();
            
            // Just wait for cancellation - Gatus polling handles everything via events
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            
            _logger.LogInformation("Gatus-only monitoring ended");
        }
    }

    private async Task PerformHealthCheckCycle()
    {
        try
        {
            _logger.LogDebug("Performing health check cycle");
            
            var serviceStatuses = await _healthMonitor.CheckAllServicesAsync();
            
            var healthyServices = serviceStatuses.Where(kvp => kvp.Value).ToArray();
            var unhealthyServices = serviceStatuses.Where(kvp => !kvp.Value).ToArray();

            _logger.LogDebug("Health check completed: {HealthyCount} healthy, {UnhealthyCount} unhealthy",
                healthyServices.Length, unhealthyServices.Length);

            if (unhealthyServices.Length > 0)
            {
                _logger.LogWarning("Unhealthy services: {UnhealthyServices}",
                    string.Join(", ", unhealthyServices.Select(kvp => kvp.Key)));
            }

            // Update DNS rewrites for all services based on their current health
            await UpdateAllServiceDnsRewrites();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error during health check cycle: {ErrorMessage}", ex.Message);
        }
    }

    private async void OnServiceStatusChanged(string serviceName, bool isHealthy)
    {
        try
        {
            _logger.LogInformation("Service status change detected: {ServiceName} is now {Status}",
                serviceName, isHealthy ? "healthy" : "unhealthy");

            // Find the service configuration
            var service = _config.Services.FirstOrDefault(s => s.Name == serviceName);
            if (service != null && service.DnsRewrites.Length > 0)
            {
                // Update DNS rewrites for this specific service
                await UpdateDnsRewritesForService(service);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error handling service status change for {ServiceName}: {ErrorMessage}", serviceName, ex.Message);
        }
    }

    private async Task UpdateDnsRewritesForService(ServiceConfiguration service)
    {
        try
        {
            // Update DNS rewrites for each domain managed by this service
            foreach (var domain in service.DnsRewrites)
            {
                await UpdateDnsRewriteForDomain(domain);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error updating DNS rewrites for service {ServiceName}: {ErrorMessage}", service.Name, ex.Message);
        }
    }

    private async Task UpdateDnsRewriteForDomain(string domain)
    {
        try
        {
            // Find all services that manage this domain and their health status
            var allServiceHealth = await _healthMonitor.GetAllServiceHealthAsync();
            var servicesForDomain = _config.Services
                .Where(s => s.DnsRewrites.Contains(domain))
                .ToArray();

            // Find the highest priority healthy service for this domain
            var targetService = servicesForDomain
                .Where(s => allServiceHealth.GetValueOrDefault(s.Name, false))
                .OrderBy(s => s.Priority)
                .FirstOrDefault();

            if (targetService != null)
            {
                _logger.LogInformation("Updating DNS rewrite for {Domain} to {IpAddress} (service: {ServiceName}, priority: {Priority})",
                    domain, targetService.IpAddress, targetService.Name, targetService.Priority);
                await _dnsManager.UpdateSingleRewriteAsync(domain, targetService.IpAddress);
            }
            else
            {
                var servicesForDomainNames = string.Join(", ", servicesForDomain.Select(s => s.Name));
                _logger.LogWarning("No healthy services available for domain {Domain}. Services managing this domain: [{Services}]. DNS rewrite will remain unchanged.",
                    domain, servicesForDomainNames);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error updating DNS rewrite for domain {Domain}: {ErrorMessage}", domain, ex.Message);
        }
    }

    private async Task UpdateAllServiceDnsRewrites()
    {
        try
        {
            _logger.LogDebug("Updating DNS rewrites for all services");
            
            // Get all unique domains to avoid duplicate updates
            var allDomains = _config.Services
                .Where(s => s.DnsRewrites.Length > 0)
                .SelectMany(s => s.DnsRewrites)
                .Distinct()
                .ToArray();
            
            // Update each unique domain based on the highest priority healthy service
            var updateTasks = allDomains.Select(domain => UpdateDnsRewriteForDomain(domain));
            await Task.WhenAll(updateTasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error updating DNS rewrites for all services: {ErrorMessage}", ex.Message);
        }
    }

    private void ValidateConfiguration()
    {
        var errors = new List<string>();

        // Validate AdGuard Home configuration
        if (string.IsNullOrWhiteSpace(_config.AdGuardHome.BaseUrl))
            errors.Add("AdGuardHome:BaseUrl is required");
        
        if (string.IsNullOrWhiteSpace(_config.AdGuardHome.Username))
            errors.Add("AdGuardHome:Username is required");
        
        if (string.IsNullOrWhiteSpace(_config.AdGuardHome.Password))
            errors.Add("AdGuardHome:Password is required");

        // Validate services configuration
        if (_config.Services == null || _config.Services.Length == 0)
            errors.Add("At least one service must be configured");
        else
        {
            for (int i = 0; i < _config.Services.Length; i++)
            {
                var service = _config.Services[i];
                if (string.IsNullOrWhiteSpace(service.Name))
                    errors.Add($"Services[{i}]:Name is required");
                
                if (string.IsNullOrWhiteSpace(service.IpAddress))
                    errors.Add($"Services[{i}]:IpAddress is required");
                
                if (service.TimeoutMs <= 0)
                    errors.Add($"Services[{i}]:TimeoutMs must be greater than 0");
                    
                // Validate Gatus-specific settings
                if (service.MonitoringMode == HealthSource.Gatus)
                {
                    if (service.GatusEndpointNames == null || service.GatusEndpointNames.Length == 0)
                        errors.Add($"Services[{i}]:GatusEndpointNames is required for Gatus monitoring");
                        
                    if (service.GatusInstanceUrls == null || service.GatusInstanceUrls.Length == 0)
                        errors.Add($"Services[{i}]:GatusInstanceUrls is required for Gatus monitoring");
                        
                    if (service.RequiredGatusEndpoints <= 0)
                        errors.Add($"Services[{i}]:RequiredGatusEndpoints must be greater than 0");
                        
                    if (service.RequiredGatusEndpoints > service.GatusEndpointNames?.Length)
                        errors.Add($"Services[{i}]:RequiredGatusEndpoints cannot be greater than the number of GatusEndpointNames");
                }
            }
        }

        // Validate that at least one service has DNS rewrites configured
        var servicesWithRewrites = _config.Services?.Where(s => s.DnsRewrites?.Length > 0).ToArray() ?? Array.Empty<ServiceConfiguration>();
        if (servicesWithRewrites.Length == 0)
        {
            errors.Add("At least one service must have DNS rewrites configured");
        }
        
        // Validate individual service DNS rewrites
        for (int i = 0; i < (_config.Services?.Length ?? 0); i++)
        {
            var service = _config.Services![i];
            if (service.DnsRewrites != null)
            {
                for (int j = 0; j < service.DnsRewrites.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(service.DnsRewrites[j]))
                        errors.Add($"Services[{i}]:DnsRewrites[{j}] cannot be empty");
                }
            }
        }

        // Validate monitoring configuration (only required for ping services)
        var hasPingServices = _config.Services?.Any(s => s.MonitoringMode == HealthSource.Ping) ?? false;
        if (hasPingServices)
        {
            if (_config.Monitoring.CheckIntervalSeconds <= 0)
                errors.Add("Monitoring:CheckIntervalSeconds must be greater than 0 (required for ping services)");
            
            if (_config.Monitoring.RetryAttempts <= 0)
                errors.Add("Monitoring:RetryAttempts must be greater than 0 (required for ping services)");
            
            if (_config.Monitoring.RetryDelayMs < 0)
                errors.Add("Monitoring:RetryDelayMs cannot be negative (required for ping services)");
        }

        if (errors.Count > 0)
        {
            var errorMessage = "Configuration validation failed:\n" + string.Join("\n", errors.Select(e => $"  - {e}"));
            _logger.LogCritical(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        _logger.LogInformation("Configuration validation passed");
    }
}
