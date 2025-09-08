namespace AdGuardHomeHA.Models;

public class MonitoringConfiguration
{
    public int CheckIntervalSeconds { get; set; } = 30;
    public int RetryAttempts { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
}
