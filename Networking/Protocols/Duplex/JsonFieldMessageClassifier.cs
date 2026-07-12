using System.Text.Json;

namespace Networking.Duplex;

/// <summary>
/// A simple classifier for protocols whose correlation and stream keys are
/// top-level JSON string properties. More complex brokers should provide their own classifier.
/// </summary>
public sealed class JsonFieldMessageClassifier : IInboundMessageClassifier
{
    private readonly string _correlationPropertyName;
    private readonly string _streamPropertyName;

    public JsonFieldMessageClassifier(
        string correlationPropertyName,
        string streamPropertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationPropertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamPropertyName);

        _correlationPropertyName = correlationPropertyName;
        _streamPropertyName = streamPropertyName;
    }

    public bool TryGetCorrelationId(
        ReadOnlyMemory<byte> message,
        out string correlationId) =>
        TryGetStringProperty(message, _correlationPropertyName, out correlationId);

    public bool TryGetStreamKey(
        ReadOnlyMemory<byte> message,
        out string streamKey) =>
        TryGetStringProperty(message, _streamPropertyName, out streamKey);

    private static bool TryGetStringProperty(
        ReadOnlyMemory<byte> message,
        string propertyName,
        out string value)
    {
        value = string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(message);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? candidate = property.GetString();

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
