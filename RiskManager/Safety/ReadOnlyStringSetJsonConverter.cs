using System.Text.Json;
using System.Text.Json.Serialization;

namespace RiskManager.Safety;

/// <summary>
/// System.Text.Json's reflection-based deserializer supports <see cref="ISet{T}"/> (mapping to
/// <see cref="HashSet{T}"/>) but not <see cref="IReadOnlySet{T}"/>, so any property declared with
/// that interface throws <see cref="NotSupportedException"/> on deserialize with no converter.
/// Applied directly to <see cref="EquityHighWatermarkSnapshot.ActivatedTierIds"/>.
/// </summary>
public sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array for a string set.");

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;
            string? value = reader.GetString();
            if (value is not null)
                set.Add(value);
        }
        return set;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
