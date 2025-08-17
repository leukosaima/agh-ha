using AdGuardHomeHA.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

public class AdGuardHomeHaService : BackgroundService
{
    private readonly IMachineHealthMonitor _healthMonitor;
    private readonly IDnsRewriteManager _dnsManager;
    private readonly ILogger<AdGuardHomeHaService> _logger;
    private readonly AppConfiguration _config;

    public AdGuardHomeHaService(
        IMachineHealthMonitor healthMonitor,
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

            // Subscribe to machine status changes
            _healthMonitor.MachineStatusChanged += OnMachineStatusChanged;

            _logger.LogInformation("AdGuard Home HA service started successfully");
            _logger.LogInformation("Monitoring {MachineCount} machines with {RewriteCount} DNS rewrites",
                _config.Machines.Length, _config.DnsRewrites.Length);
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
        _healthMonitor.MachineStatusChanged -= OnMachineStatusChanged;

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("AdGuard Home HA service stopped");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting monitoring loop with {Interval}s interval", 
            _config.Monitoring.CheckIntervalSeconds);

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
                _logger.LogError(ex, "Error in monitoring loop");
                
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

        _logger.LogInformation("Monitoring loop ended");
    }

    private async Task PerformHealthCheckCycle()
    {
        try
        {
            _logger.LogDebug("Performing health check cycle");
            
            var machineStatuses = await _healthMonitor.CheckAllMachinesAsync();
            
            var healthyMachines = machineStatuses.Where(kvp => kvp.Value).ToArray();
            var unhealthyMachines = machineStatuses.Where(kvp => !kvp.Value).ToArray();

            _logger.LogDebug("Health check completed: {HealthyCount} healthy, {UnhealthyCount} unhealthy",
                healthyMachines.Length, unhealthyMachines.Length);

            if (unhealthyMachines.Length > 0)
            {
                _logger.LogWarning("Unhealthy machines: {UnhealthyMachines}",
                    string.Join(", ", unhealthyMachines.Select(kvp => kvp.Key)));
            }

            // Update DNS rewrites to point to the best available machine
            await UpdateDnsTobestAvailableMachine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during health check cycle");
        }
    }

    private async void OnMachineStatusChanged(string machineName, bool isHealthy)
    {
        try
        {
            _logger.LogInformation("Machine status change detected: {MachineName} is now {Status}",
                machineName, isHealthy ? "healthy" : "unhealthy");

            // When a machine status changes, immediately update DNS to the best available machine
            await UpdateDnsTobestAvailableMachine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling machine status change for {MachineName}", machineName);
        }
    }

    private async Task UpdateDnsTobestAvailableMachine()
    {
        try
        {
            var bestAvailableIp = _healthMonitor.GetBestAvailableMachine();
            var currentTarget = await _dnsManager.GetCurrentTargetAsync();

            if (bestAvailableIp == null)
            {
                if (!string.IsNullOrEmpty(currentTarget))
                {
                    _logger.LogCritical("No healthy machines available, but DNS rewrites are still pointing to {CurrentTarget}. " +
                                       "Consider manual intervention.", currentTarget);
                }
                else
                {
                    _logger.LogCritical("No healthy machines available and no current DNS target set");
                }
                return;
            }

            // Update DNS rewrites to point to the best available machine
            await _dnsManager.UpdateRewritesAsync(bestAvailableIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating DNS to best available machine");
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

        // Validate machines configuration
        if (_config.Machines == null || _config.Machines.Length == 0)
            errors.Add("At least one machine must be configured");
        else
        {
            for (int i = 0; i < _config.Machines.Length; i++)
            {
                var machine = _config.Machines[i];
                if (string.IsNullOrWhiteSpace(machine.Name))
                    errors.Add($"Machine[{i}]:Name is required");
                
                if (string.IsNullOrWhiteSpace(machine.IpAddress))
                    errors.Add($"Machine[{i}]:IpAddress is required");
                
                if (machine.TimeoutMs <= 0)
                    errors.Add($"Machine[{i}]:TimeoutMs must be greater than 0");
            }
        }

        // Validate DNS rewrites
        if (_config.DnsRewrites == null || _config.DnsRewrites.Length == 0)
            errors.Add("At least one DNS rewrite domain must be configured");
        else
        {
            for (int i = 0; i < _config.DnsRewrites.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(_config.DnsRewrites[i]))
                    errors.Add($"DnsRewrites[{i}] cannot be empty");
            }
        }

        // Validate monitoring configuration
        if (_config.Monitoring.CheckIntervalSeconds <= 0)
            errors.Add("Monitoring:CheckIntervalSeconds must be greater than 0");
        
        if (_config.Monitoring.RetryAttempts <= 0)
            errors.Add("Monitoring:RetryAttempts must be greater than 0");
        
        if (_config.Monitoring.RetryDelayMs < 0)
            errors.Add("Monitoring:RetryDelayMs cannot be negative");

        if (errors.Count > 0)
        {
            var errorMessage = "Configuration validation failed:\n" + string.Join("\n", errors.Select(e => $"  - {e}"));
            _logger.LogCritical(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        _logger.LogInformation("Configuration validation passed");
    }
}
