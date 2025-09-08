namespace AdGuardHomeHA.Models;

public class ServiceConfiguration
{
    public string Name { get; set; } = string.Empty;
    public HealthSource MonitoringMode { get; set; } = HealthSource.Ping;
    public string IpAddress { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int TimeoutMs { get; set; } = 5000;
    public string[] DnsRewrites { get; set; } = Array.Empty<string>();
    public string[] GatusEndpointNames { get; set; } = Array.Empty<string>();
    public int RequiredGatusEndpoints { get; set; } = 1; // How many Gatus endpoints must report healthy
}
