using AdGuardHomeHA.Models;

namespace AdGuardHomeHA.Services;

public interface IWebhookHealthAggregator
{
    Task ProcessWebhookAsync(GatusWebhookPayload payload);
    Task<Dictionary<string, bool>> GetServiceHealthStatusAsync();
    Task<bool> GetServiceHealthAsync(string serviceName);
    event Action<string, bool> ServiceHealthChanged;
}
