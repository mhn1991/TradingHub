using System.Globalization;
using System.Text.Json;
using Networking;

namespace Brokers;

internal static class BrokerHttpErrorMapper
{
    public static BrokerError Map(BrokerProvider provider, HttpNetworkResponse response)
    {
        (string? code, string? providerMessage) = ParseProviderError(provider, response.Body);
        int statusCode = response.StatusCodeValue;
        BrokerErrorKind kind = statusCode switch
        {
            >= 300 and <= 399 => BrokerErrorKind.Redirect,
            400 => BrokerErrorKind.Validation,
            401 => BrokerErrorKind.Authentication,
            403 => BrokerErrorKind.Permission,
            404 => BrokerErrorKind.NotFound,
            408 => BrokerErrorKind.Timeout,
            418 or 429 => BrokerErrorKind.RateLimited,
            >= 500 and <= 599 => BrokerErrorKind.Server,
            _ => BrokerErrorKind.Provider
        };

        bool retryable = kind is BrokerErrorKind.Timeout or BrokerErrorKind.RateLimited or BrokerErrorKind.Server;
        string message = providerMessage ??
                         $"{provider} returned HTTP {statusCode} ({response.ReasonPhrase ?? "unknown"}).";

        return new BrokerError(
            kind,
            message,
            code,
            statusCode,
            retryable,
            GetRetryAfter(response),
            GetRequestId(response));
    }

    public static string? GetRequestId(HttpNetworkResponse response)
    {
        string[] candidates = ["RequestID", "Request-Id", "X-Request-ID", "X-MBX-UUID"];
        foreach (string candidate in candidates)
        {
            if (response.TryGetHeader(candidate, out IReadOnlyList<string>? values) && values.Count > 0)
            {
                return values[0];
            }
        }

        return null;
    }

    private static (string? Code, string? Message) ParseProviderError(
        BrokerProvider provider,
        ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            return provider switch
            {
                BrokerProvider.BinanceSpot => (
                    ReadAsString(root, "code"),
                    ReadAsString(root, "msg")),
                BrokerProvider.OandaV20 => (
                    ReadAsString(root, "errorCode") ?? ReadAsString(root, "rejectReason"),
                    ReadAsString(root, "errorMessage")),
                _ => (null, null)
            };
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadAsString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static TimeSpan? GetRetryAfter(HttpNetworkResponse response)
    {
        if (!response.TryGetHeader("Retry-After", out IReadOnlyList<string>? values) || values.Count == 0)
        {
            return null;
        }

        string value = values[0];
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
            seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset retryAt))
        {
            TimeSpan delay = retryAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
