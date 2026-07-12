using System.Globalization;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Abstractions;
using Brokers.Exceptions;

namespace Brokers.Infrastructure;

internal static class BrokerJson
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static async ValueTask<T> ReadAsync<T>(
        BrokerKind broker,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await ReadBoundedBodyAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new BrokerApiException(broker, response.StatusCode, body);
        }

        try
        {
            T? result = JsonSerializer.Deserialize<T>(body, Options);
            return result ?? throw new JsonException("The broker returned an empty JSON document.");
        }
        catch (JsonException exception)
        {
            throw new BrokerApiException(
                broker,
                response.StatusCode,
                body,
                $"{broker} returned JSON that could not be decoded as {typeof(T).Name}.",
                exception);
        }
    }

    public static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("A required broker decimal value was missing.");
        }

        return decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static decimal? ParseNullableDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                $"The broker response exceeded the {MaximumResponseBytes}-byte limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var bufferOwner = MemoryPool<byte>.Shared.Rent(16 * 1024);
        using var output = new MemoryStream();

        while (true)
        {
            int read = await stream.ReadAsync(bufferOwner.Memory, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    $"The broker response exceeded the {MaximumResponseBytes}-byte limit.");
            }

            output.Write(bufferOwner.Memory.Span[..read]);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
