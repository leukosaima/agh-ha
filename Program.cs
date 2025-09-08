using AdGuardHomeHA.Models;
using AdGuardHomeHA.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

// Check if webhook mode is enabled
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var webhookEnabled = configuration.GetSection($"{AppConfiguration.SectionName}:Webhook:Enabled").Get<bool>();

if (webhookEnabled)
{
    // Use WebApplicationBuilder for web API support
    var builder = WebApplication.CreateBuilder(args);
    
    // Configure logging
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
    
    // Configure options
    builder.Services.Configure<AppConfiguration>(
        builder.Configuration.GetSection(AppConfiguration.SectionName));
    
    // Add web API services
    builder.Services.AddControllers();
    
    // Configure Kestrel to listen on webhook port
    var webhookPort = builder.Configuration.GetSection($"{AppConfiguration.SectionName}:Webhook:Port").Get<int>();
    if (webhookPort <= 0) webhookPort = 8080;
    
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(webhookPort);
    });
    
    // Register HTTP client for AdGuard Home
    builder.Services.AddHttpClient<IAdGuardHomeClient, AdGuardHomeClient>();
    
    // Register webhook services
    builder.Services.AddSingleton<IWebhookHealthAggregator, WebhookHealthAggregator>();
    
    // Register services
    builder.Services.AddSingleton<IServiceHealthMonitor>(provider =>
    {
        var logger = provider.GetRequiredService<ILogger<ServiceHealthMonitor>>();
        var config = provider.GetRequiredService<IOptions<AppConfiguration>>();
        var webhookAggregator = provider.GetService<IWebhookHealthAggregator>();
        return new ServiceHealthMonitor(config, logger, webhookAggregator);
    });
    builder.Services.AddSingleton<IDnsRewriteManager, DnsRewriteManager>();
    
    // Register the main hosted service
    builder.Services.AddHostedService<AdGuardHomeHaService>();
    
    var app = builder.Build();
    
    // Configure the web application pipeline
    app.UseRouting();
    app.MapControllers();
    
    try
    {
        await app.RunAsync();
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogCritical(ex, "Application terminated unexpectedly");
        throw;
    }
}
else
{
    // Use traditional host builder for background service only
    var builder = Host.CreateApplicationBuilder(args);
    
    // Configure logging
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
    
    // Configure options
    builder.Services.Configure<AppConfiguration>(
        builder.Configuration.GetSection(AppConfiguration.SectionName));
    
    // Register HTTP client for AdGuard Home
    builder.Services.AddHttpClient<IAdGuardHomeClient, AdGuardHomeClient>();
    
    // Register services (no webhook aggregator)
    builder.Services.AddSingleton<IServiceHealthMonitor>(provider =>
    {
        var logger = provider.GetRequiredService<ILogger<ServiceHealthMonitor>>();
        var config = provider.GetRequiredService<IOptions<AppConfiguration>>();
        return new ServiceHealthMonitor(config, logger, null);
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
}
