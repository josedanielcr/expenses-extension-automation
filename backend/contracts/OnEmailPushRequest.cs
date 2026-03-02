using System.Text.Json.Serialization;

public sealed class OnEmailPushRequest
{
    [JsonPropertyName("emails")]
    [JsonConverter(typeof(EmailEntryListConverter))]
    public List<EmailEntry> Emails { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("exclusionRules")]
    public List<CategoryExclusionRule> ExclusionRules { get; set; } = [];
}
