using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class EmailEntryListConverter : JsonConverter<List<EmailEntry>>
{
    public override List<EmailEntry> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("emails must be an array.");

        var items = new List<EmailEntry>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return items;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var entry = JsonSerializer.Deserialize<EmailEntry>(ref reader, options)
                    ?? new EmailEntry();
                items.Add(entry);
                continue;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    items.Add(new EmailEntry());
                    continue;
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<EmailEntry>(raw, options);
                    if (parsed is not null)
                    {
                        items.Add(parsed);
                        continue;
                    }
                }
                catch (JsonException)
                {
                }

                items.Add(new EmailEntry { Message = raw });
                continue;
            }

            throw new JsonException("emails entries must be objects or strings.");
        }

        throw new JsonException("Unexpected end of emails array.");
    }

    public override void Write(Utf8JsonWriter writer, List<EmailEntry> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}
