using System.Text.Json.Serialization;

public sealed class GoogleTokenInfo
{
    [JsonPropertyName("aud")] public string? Aud { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("user_id")] public string? UserId { get; set; } // sometimes present
    [JsonPropertyName("sub")] public string? Sub { get; set; }        // sometimes present
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
}