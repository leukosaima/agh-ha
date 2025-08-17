namespace AdGuardHomeHA.Models;

public class AppConfiguration
{
    public const string SectionName = "AdGuardHomeHA";

    public AdGuardHomeConfiguration AdGuardHome { get; set; } = new();
    public MachineConfiguration[] Machines { get; set; } = Array.Empty<MachineConfiguration>();
    public string[] DnsRewrites { get; set; } = Array.Empty<string>();
    public MonitoringConfiguration Monitoring { get; set; } = new();
}

public class AdGuardHomeConfiguration
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class MachineConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Priority { get; set; } // Lower number = higher priority
    public int TimeoutMs { get; set; } = 5000;
}

public class MonitoringConfiguration
{
    public int CheckIntervalSeconds { get; set; } = 30;
    public int RetryAttempts { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
}
