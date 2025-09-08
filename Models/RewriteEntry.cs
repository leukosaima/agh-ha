using System.Text.Json.Serialization;

namespace AdGuardHomeHA.Models;

public class RewriteEntry
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("answer")]
    public string Answer { get; set; } = string.Empty;
}
