using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;

namespace Dashboard.Live;

internal enum BinanceStreamMessageKind
{
    Ignore,
    Kline,
    ServerShutdown
}

internal readonly record struct BinanceStreamMessage(
    BinanceStreamMessageKind Kind,
    Candle? Candle);

internal static class BinanceKlineParser
{
    public static BinanceStreamMessage ParseStream(
        ReadOnlySpan<byte> json,
        string expectedSymbol,
        string expectedInterval,
        InstrumentKey instrument,
        BarInterval interval)
    {
        BinanceStreamEnvelope? message = JsonSerializer.Deserialize(
            json,
            LiveFeedJsonSerializerContext.Default.BinanceStreamEnvelope);
        if (message is null)
        {
            throw new JsonException("The Binance stream message was empty.");
        }

        if (string.Equals(message.EventType, "serverShutdown", StringComparison.Ordinal))
        {
            return new BinanceStreamMessage(BinanceStreamMessageKind.ServerShutdown, null);
        }

        if (!string.Equals(message.EventType, "kline", StringComparison.Ordinal))
        {
            return new BinanceStreamMessage(BinanceStreamMessageKind.Ignore, null);
        }

        BinanceStreamKline kline = message.Kline
            ?? throw new JsonException("The Binance kline event did not include a kline payload.");
        if (!string.Equals(kline.Symbol, expectedSymbol, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(
                $"The Binance stream returned symbol '{kline.Symbol}' instead of '{expectedSymbol}'.");
        }

        if (!string.Equals(kline.Interval, expectedInterval, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"The Binance stream returned interval '{kline.Interval}' instead of '{expectedInterval}'.");
        }

        return new BinanceStreamMessage(
            BinanceStreamMessageKind.Kline,
            CreateCandle(
                instrument,
                interval,
                kline.OpenTime,
                kline.CloseTime,
                kline.Open,
                kline.High,
                kline.Low,
                kline.Close,
                kline.Volume,
                kline.IsClosed));
    }

    public static IReadOnlyList<Candle> ParseRest(
        ReadOnlyMemory<byte> json,
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset now)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The Binance kline response must be an array.");
        }

        var candles = new List<Candle>(document.RootElement.GetArrayLength());
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7)
            {
                throw new JsonException("A Binance REST kline row contained fewer than seven fields.");
            }

            long closeTime = GetInt64(row[6], "close time");
            candles.Add(CreateCandle(
                instrument,
                interval,
                GetInt64(row[0], "open time"),
                closeTime,
                GetString(row[1], "open"),
                GetString(row[2], "high"),
                GetString(row[3], "low"),
                GetString(row[4], "close"),
                GetString(row[5], "volume"),
                closeTime < now.ToUnixTimeMilliseconds()));
        }

        return candles.OrderBy(candle => candle.OpenTime).ToArray();
    }

    private static Candle CreateCandle(
        InstrumentKey instrument,
        BarInterval interval,
        long openTime,
        long closeTime,
        string? openValue,
        string? highValue,
        string? lowValue,
        string? closeValue,
        string? volumeValue,
        bool isComplete)
    {
        decimal open = ParseDecimal(openValue, "open");
        decimal high = ParseDecimal(highValue, "high");
        decimal low = ParseDecimal(lowValue, "low");
        decimal close = ParseDecimal(closeValue, "close");
        decimal volume = ParseDecimal(volumeValue, "volume");
        if (open <= 0m ||
            high <= 0m ||
            low <= 0m ||
            close <= 0m ||
            volume < 0m ||
            high < Math.Max(open, close) ||
            low > Math.Min(open, close) ||
            high < low ||
            closeTime <= openTime)
        {
            throw new JsonException("The Binance kline contained inconsistent OHLCV values.");
        }

        return new Candle
        {
            Instrument = instrument,
            Interval = interval,
            OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(openTime),
            CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(closeTime),
            Prices = new Ohlc(open, high, low, close),
            Volume = new MarketVolume(volume, VolumeKind.BaseAssetQuantity),
            IsComplete = isComplete
        };
    }

    private static decimal ParseDecimal(string? value, string field)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal result))
        {
            throw new JsonException($"The Binance kline {field} value was invalid.");
        }

        return result;
    }

    private static string GetString(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? throw new JsonException($"The Binance kline {field} was null.")
            : throw new JsonException($"The Binance kline {field} must be a string.");

    private static long GetInt64(JsonElement element, string field)
    {
        if (element.TryGetInt64(out long result))
        {
            return result;
        }

        throw new JsonException($"The Binance kline {field} must be an integer.");
    }
}

internal sealed record BinanceStreamEnvelope
{
    [JsonPropertyName("e")]
    public string? EventType { get; init; }

    [JsonPropertyName("E")]
    public long EventTime { get; init; }

    [JsonPropertyName("s")]
    public string? Symbol { get; init; }

    [JsonPropertyName("k")]
    public BinanceStreamKline? Kline { get; init; }
}

internal sealed record BinanceStreamKline
{
    [JsonPropertyName("t")]
    public long OpenTime { get; init; }

    [JsonPropertyName("T")]
    public long CloseTime { get; init; }

    [JsonPropertyName("s")]
    public string? Symbol { get; init; }

    [JsonPropertyName("i")]
    public string? Interval { get; init; }

    [JsonPropertyName("o")]
    public string? Open { get; init; }

    [JsonPropertyName("c")]
    public string? Close { get; init; }

    [JsonPropertyName("h")]
    public string? High { get; init; }

    [JsonPropertyName("l")]
    public string? Low { get; init; }

    [JsonPropertyName("v")]
    public string? Volume { get; init; }

    [JsonPropertyName("x")]
    public bool IsClosed { get; init; }
}

[JsonSerializable(typeof(BinanceStreamEnvelope))]
internal partial class LiveFeedJsonSerializerContext : JsonSerializerContext;
