using AdGuardHomeHA.Models;

namespace AdGuardHomeHA.Services;

public interface IAdGuardHomeClient
{
    Task<bool> TestConnectionAsync();
    Task<RewriteEntry[]> GetRewritesAsync();
    Task<bool> AddRewriteAsync(string domain, string ipAddress);
    Task<bool> DeleteRewriteAsync(string domain, string ipAddress);
    Task<bool> UpdateRewriteAsync(string domain, string newIpAddress);
}
