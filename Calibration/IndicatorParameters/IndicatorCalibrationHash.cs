using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Simulator.Calibration;

/// <summary>
/// Canonical, order-independent JSON hashing for indicator-calibration compatibility identities
/// and default-configuration fingerprints. Deliberately self-contained (no dependency on
/// <c>Simulator.Experiments.Persistence.CanonicalJsonHash</c>) - the Calibration project must stay
/// referenceable by live hosts without pulling in the whole Simulator project, so this small,
/// self-contained duplicate is preferred over a cross-project dependency for one 40-line helper.
/// Object keys are sorted and numbers are reformatted to a canonical form so two structurally
/// identical values always hash identically regardless of serializer key ordering.
/// </summary>
public static class IndicatorCalibrationHash
{
    public static string ComputeOfObject<T>(T value)
    {
        string json = JsonSerializer.Serialize(value);
        return ComputeOfJson(json);
    }

    public static string ComputeOfJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(document.RootElement, writer);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    Write(item, writer);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                WriteNumber(element, writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON token {element.ValueKind}.");
        }
    }

    private static void WriteNumber(JsonElement element, Utf8JsonWriter writer)
    {
        string raw = element.GetRawText();
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal decimalValue))
        {
            writer.WriteRawValue(decimalValue.ToString("G29", CultureInfo.InvariantCulture));
            return;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue) &&
            double.IsFinite(doubleValue))
        {
            writer.WriteRawValue(doubleValue.ToString("R", CultureInfo.InvariantCulture));
            return;
        }

        throw new JsonException($"JSON number '{raw}' cannot be canonicalized.");
    }
}
