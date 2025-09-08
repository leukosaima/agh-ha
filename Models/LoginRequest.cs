using System.Text.Json.Serialization;

namespace AdGuardHomeHA.Models;

public class LoginRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}
