using System.Text.Json.Serialization;

namespace AdGuardHomeHA.Models;

public class StatusResponse
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
