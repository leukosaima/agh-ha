namespace AdGuardHomeHA.Models;

public class WebhookConfiguration
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 8080;
    public string Path { get; set; } = "/webhook";
    public int HealthStatusTimeoutSeconds { get; set; } = 300; // 5 minutes default timeout for webhook health status
    public string? AuthToken { get; set; } // Optional token for webhook authentication
}
