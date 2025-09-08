using System.Text.Json.Serialization;

namespace AdGuardHomeHA.Models;

public class ConditionResult
{
    [JsonPropertyName("condition")]
    public string? Condition { get; set; }
    
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
