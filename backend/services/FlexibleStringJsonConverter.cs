using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class FlexibleStringJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            JsonTokenType.True => bool.TrueString.ToLowerInvariant(),
            JsonTokenType.False => bool.FalseString.ToLowerInvariant(),
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException($"Cannot convert token type {reader.TokenType} to string."),
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
