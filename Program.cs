using AdGuardHomeHA.Models;
using AdGuardHomeHA.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

// Use traditional host builder for background service
var builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Configure options
builder.Services.Configure<AppConfiguration>(
    builder.Configuration.GetSection(AppConfiguration.SectionName));

// Register HTTP client for AdGuard Home
builder.Services.AddHttpClient<IAdGuardHomeClient, AdGuardHomeClient>();

// Register HTTP client for Gatus polling
builder.Services.AddHttpClient<GatusPollingHealthMonitor>();

// Register Gatus polling health monitor
builder.Services.AddSingleton<IGatusPollingHealthMonitor, GatusPollingHealthMonitor>();

// Register services
builder.Services.AddSingleton<IServiceHealthMonitor>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ServiceHealthMonitor>>();
    var config = provider.GetRequiredService<IOptions<AppConfiguration>>();
    var gatusPollingMonitor = provider.GetService<IGatusPollingHealthMonitor>();
    return new ServiceHealthMonitor(config, logger, gatusPollingMonitor);
});
builder.Services.AddSingleton<IDnsRewriteManager, DnsRewriteManager>();

// Register the main hosted service
builder.Services.AddHostedService<AdGuardHomeHaService>();

var host = builder.Build();

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogCritical(ex, "Application terminated unexpectedly");
    throw;
}
