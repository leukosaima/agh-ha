using System.Text.Json.Serialization;

namespace AdGuardHomeHA.Models;

public class GatusWebhookPayload
{
    [JsonPropertyName("endpointName")]
    public string? EndpointName { get; set; }
    
    [JsonPropertyName("endpointGroup")]
    public string? EndpointGroup { get; set; }
    
    [JsonPropertyName("endpointURL")]
    public string? EndpointUrl { get; set; }
    
    [JsonPropertyName("conditionResults")]
    public ConditionResult[]? ConditionResults { get; set; }
    
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
    
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
