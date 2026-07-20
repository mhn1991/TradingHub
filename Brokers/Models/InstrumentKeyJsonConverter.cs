using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brokers.Models;

/// <summary>
/// Accepts either a plain string (<c>"FX:EUR/USD"</c>) or an object
/// (<c>{ "value": "FX:EUR/USD" }</c>) for <see cref="InstrumentKey"/>.
/// </summary>
public sealed class InstrumentKeyJsonConverter : JsonConverter<InstrumentKey>
{
    public override InstrumentKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text))
                throw new JsonException("InstrumentKey string was empty.");
            return new InstrumentKey(text);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a string or object for InstrumentKey.");

        string? value = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected an InstrumentKey property name.");

            string propertyName = reader.GetString() ?? string.Empty;
            if (!reader.Read())
                throw new JsonException("Unexpected end of JSON while reading InstrumentKey.");

            if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
                value = reader.GetString();
            else
                reader.Skip();
        }

        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("InstrumentKey object must contain a non-empty 'value'.");

        return new InstrumentKey(value);
    }

    public override void Write(Utf8JsonWriter writer, InstrumentKey value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        writer.WriteBoolean("isEmpty", value.IsEmpty);
        writer.WriteEndObject();
    }
}
