using System.Text.Json;
using System.Text.Json.Serialization;

namespace Brokers.Models;

/// <summary>
/// Reads/writes <see cref="BarInterval"/> as <c>{ "value": int, "unit": "..." }</c>, always going
/// through the validating <see cref="BarInterval(int, BarUnit)"/> constructor so malformed JSON
/// (missing/non-positive value, undefined unit) fails loudly at deserialization time instead of
/// silently producing an invalid interval. See the remarks on <see cref="BarInterval"/> for why a
/// converter is required at all.
/// </summary>
public sealed class BarIntervalJsonConverter : JsonConverter<BarInterval>
{
    public override BarInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a JSON object for a BarInterval.");

        int? value = null;
        BarUnit? unit = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected a BarInterval property name.");

            string propertyName = reader.GetString() ?? string.Empty;
            if (!reader.Read())
                throw new JsonException("Unexpected end of JSON while reading a BarInterval.");

            if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
            {
                value = reader.GetInt32();
            }
            else if (string.Equals(propertyName, "unit", StringComparison.OrdinalIgnoreCase))
            {
                unit = reader.TokenType == JsonTokenType.String
                    ? Enum.Parse<BarUnit>(reader.GetString()!, ignoreCase: true)
                    : (BarUnit)reader.GetInt32();
            }
            else
            {
                reader.Skip();
            }
        }

        if (value is null || unit is null)
            throw new JsonException("A BarInterval JSON object must contain both 'value' and 'unit'.");

        try
        {
            return new BarInterval(value.Value, unit.Value);
        }
        catch (ArgumentException exception)
        {
            // Domain validation failures during JSON load must surface as JsonException so
            // persistence recovery paths (job/artifact repositories) can quarantine the file
            // instead of treating it as an invalid API request parameter.
            throw new JsonException(
                $"Invalid BarInterval value/unit combination ({value.Value}, {unit.Value}).",
                exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, BarInterval value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("value", value.Value);
        writer.WriteString("unit", value.Unit.ToString());
        writer.WriteEndObject();
    }
}
