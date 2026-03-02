using System.Text.Json.Serialization;

public sealed class CategoryExclusionRule
{
    [JsonPropertyName("word")]
    public string Word { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}
