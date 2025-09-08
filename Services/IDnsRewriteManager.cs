namespace AdGuardHomeHA.Services;

public interface IDnsRewriteManager
{
    Task InitializeAsync();
    Task UpdateSingleRewriteAsync(string domain, string? targetIpAddress);
}
