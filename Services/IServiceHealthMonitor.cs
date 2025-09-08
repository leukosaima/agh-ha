namespace AdGuardHomeHA.Services;

public interface IServiceHealthMonitor
{
    Task<Dictionary<string, bool>> CheckAllServicesAsync();
    Task<Dictionary<string, bool>> GetAllServiceHealthAsync();
    string? GetBestAvailableService();
    event Action<string, bool> ServiceStatusChanged;
}
