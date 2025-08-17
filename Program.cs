using AdGuardHomeHA.Models;
using AdGuardHomeHA.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Configure options
builder.Services.Configure<AppConfiguration>(
    builder.Configuration.GetSection(AppConfiguration.SectionName));

// Register HTTP client for AdGuard Home
builder.Services.AddHttpClient<IAdGuardHomeClient, AdGuardHomeClient>();

// Register services
builder.Services.AddSingleton<IMachineHealthMonitor, MachineHealthMonitor>();
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
