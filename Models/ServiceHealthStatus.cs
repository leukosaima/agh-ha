namespace AdGuardHomeHA.Models;

public class ServiceHealthStatus
{
    public string ServiceName { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public DateTime LastUpdated { get; set; }
    public HealthSource Source { get; set; }
    public Dictionary<string, bool> GatusEndpoints { get; set; } = new();
    public Dictionary<string, DateTime> EndpointLastSeen { get; set; } = new(); // Track when each endpoint was last polled from Gatus API
}
