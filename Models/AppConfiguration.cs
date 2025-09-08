namespace AdGuardHomeHA.Models;

public class AppConfiguration
{
    public const string SectionName = "AdGuardHomeHA";

    public AdGuardHomeConfiguration AdGuardHome { get; set; } = new();
    public MonitoringConfiguration Monitoring { get; set; } = new();
    public GatusPollingConfiguration? GatusPolling { get; set; }
    public ServiceConfiguration[] Services { get; set; } = Array.Empty<ServiceConfiguration>();
}
