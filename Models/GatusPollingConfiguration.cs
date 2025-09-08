namespace AdGuardHomeHA.Models;

public class GatusPollingConfiguration
{
    public int IntervalSeconds { get; set; } = 30; // How often to poll Gatus instances
    public int TimeoutSeconds { get; set; } = 10;  // HTTP timeout for API calls
}
