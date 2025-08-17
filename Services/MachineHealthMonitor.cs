using System.Diagnostics;
using AdGuardHomeHA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Services;

public interface IMachineHealthMonitor
{
    Task<Dictionary<string, bool>> CheckAllMachinesAsync();
    Task<bool> CheckMachineHealthAsync(MachineConfiguration machine);
    string? GetBestAvailableMachine();
    event Action<string, bool> MachineStatusChanged;
}

public class MachineHealthMonitor : IMachineHealthMonitor
{
    private readonly ILogger<MachineHealthMonitor> _logger;
    private readonly AppConfiguration _config;
    private readonly Dictionary<string, bool> _machineStatus = new();
    private readonly SemaphoreSlim _statusSemaphore = new(1, 1);

    public event Action<string, bool>? MachineStatusChanged;

    public MachineHealthMonitor(
        IOptions<AppConfiguration> appConfig,
        ILogger<MachineHealthMonitor> logger)
    {
        _logger = logger;
        _config = appConfig.Value;

        // Initialize all machines as unknown status
        foreach (var machine in _config.Machines)
        {
            _machineStatus[machine.Name] = false;
        }
    }

    public async Task<Dictionary<string, bool>> CheckAllMachinesAsync()
    {
        var tasks = _config.Machines.Select(async machine =>
        {
            var isHealthy = await CheckMachineHealthAsync(machine);
            await UpdateMachineStatusAsync(machine.Name, isHealthy);
            return new KeyValuePair<string, bool>(machine.Name, isHealthy);
        });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public async Task<bool> CheckMachineHealthAsync(MachineConfiguration machine)
    {
        try
        {
            _logger.LogDebug("Checking health of machine {MachineName} at {IpAddress}", 
                machine.Name, machine.IpAddress);

            for (int attempt = 1; attempt <= _config.Monitoring.RetryAttempts; attempt++)
            {
                try
                {
                    var isReachable = await PingHostAsync(machine.IpAddress, machine.TimeoutMs);
                    
                    if (isReachable)
                    {
                        _logger.LogDebug("Machine {MachineName} is healthy (ping successful)",
                            machine.Name);
                        return true;
                    }

                    _logger.LogDebug("Ping attempt {Attempt} failed for {MachineName}",
                        attempt, machine.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Ping attempt {Attempt} failed for {MachineName}: {Error}",
                        attempt, machine.Name, ex.Message);
                }

                if (attempt < _config.Monitoring.RetryAttempts)
                {
                    await Task.Delay(_config.Monitoring.RetryDelayMs);
                }
            }

            _logger.LogWarning("Machine {MachineName} at {IpAddress} is unhealthy after {Attempts} attempts",
                machine.Name, machine.IpAddress, _config.Monitoring.RetryAttempts);
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking health of machine {MachineName} at {IpAddress}",
                machine.Name, machine.IpAddress);
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

    public string? GetBestAvailableMachine()
    {
        // Return the machine with the lowest priority (highest preference) that is healthy
        var availableMachines = _config.Machines
            .Where(m => _machineStatus.GetValueOrDefault(m.Name, false))
            .OrderBy(m => m.Priority)
            .ToArray();

        if (availableMachines.Length == 0)
        {
            _logger.LogWarning("No healthy machines available");
            return null;
        }

        var bestMachine = availableMachines.First();
        _logger.LogDebug("Best available machine: {MachineName} (priority {Priority})",
            bestMachine.Name, bestMachine.Priority);
        
        return bestMachine.IpAddress;
    }

    private async Task UpdateMachineStatusAsync(string machineName, bool isHealthy)
    {
        await _statusSemaphore.WaitAsync();
        try
        {
            var previousStatus = _machineStatus.GetValueOrDefault(machineName, false);
            _machineStatus[machineName] = isHealthy;

            // Fire event if status changed
            if (previousStatus != isHealthy)
            {
                _logger.LogInformation("Machine {MachineName} status changed: {PreviousStatus} -> {NewStatus}",
                    machineName, previousStatus ? "Healthy" : "Unhealthy", isHealthy ? "Healthy" : "Unhealthy");
                
                MachineStatusChanged?.Invoke(machineName, isHealthy);
            }
        }
        finally
        {
            _statusSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _statusSemaphore?.Dispose();
    }
}
