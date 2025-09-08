using AdGuardHomeHA.Models;
using AdGuardHomeHA.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdGuardHomeHA.Controllers;

[ApiController]
[Route("webhook")]
public class WebhookController : ControllerBase
{
    private readonly IWebhookHealthAggregator _healthAggregator;
    private readonly ILogger<WebhookController> _logger;
    private readonly WebhookConfiguration _webhookConfig;

    public WebhookController(
        IWebhookHealthAggregator healthAggregator,
        ILogger<WebhookController> logger,
        IOptions<AppConfiguration> appConfig)
    {
        _healthAggregator = healthAggregator;
        _logger = logger;
        _webhookConfig = appConfig.Value.Webhook;
    }

    [HttpPost]
    public async Task<IActionResult> ReceiveWebhook([FromBody] GatusWebhookPayload payload)
    {
        try
        {
            // Validate auth token if configured
            if (!string.IsNullOrEmpty(_webhookConfig.AuthToken))
            {
                var authHeader = Request.Headers.Authorization.FirstOrDefault();
                if (authHeader == null || !authHeader.StartsWith("Bearer "))
                {
                    _logger.LogWarning("Webhook received without proper authorization header");
                    return Unauthorized();
                }

                var token = authHeader.Substring("Bearer ".Length);
                if (token != _webhookConfig.AuthToken)
                {
                    _logger.LogWarning("Webhook received with invalid auth token");
                    return Unauthorized();
                }
            }

            if (payload?.EndpointName == null)
            {
                _logger.LogWarning("Webhook received with null or invalid payload");
                return BadRequest("Invalid payload");
            }

            _logger.LogInformation("Received webhook for endpoint {EndpointName} in group {EndpointGroup}: {Success}",
                payload.EndpointName, payload.EndpointGroup, payload.Success);

            // Process the webhook
            await _healthAggregator.ProcessWebhookAsync(payload);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
