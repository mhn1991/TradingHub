using Brokers.Models;

namespace Dashboard.Live;

public sealed record LiveFeedOptions
{
    public const string SectionName = "LiveFeed";

    public string Symbol { get; init; } = "BTCUSDT";
    public string Instrument { get; init; } = "CRYPTO:BTC/USDT";
    public string Interval { get; init; } = "1m";
    public int WarmupCandles { get; init; } = 250;
    public int FrameCapacity { get; init; } = 500;
    public Uri RestBaseAddress { get; init; } = new("https://data-api.binance.vision/");
    public Uri WebSocketBaseAddress { get; init; } = new("wss://data-stream.binance.vision/ws/");
    public int RequestTimeoutSeconds { get; init; } = 15;
    public int ReconnectMinimumSeconds { get; init; } = 1;
    public int ReconnectMaximumSeconds { get; init; } = 30;

    internal (InstrumentKey Instrument, BarInterval Interval) Validate()
    {
        if (string.IsNullOrWhiteSpace(Symbol) ||
            Symbol.Length is < 5 or > 24 ||
            Symbol.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "The Binance symbol must contain 5 to 24 ASCII letters or digits.",
                nameof(Symbol));
        }

        var instrument = new InstrumentKey(Instrument);
        BarInterval interval = BinanceInterval.Parse(Interval);
        if (WarmupCandles is < 21 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WarmupCandles),
                "WarmupCandles must be from 21 to Binance's 1000-candle limit.");
        }

        if (FrameCapacity is < 50 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameCapacity));
        }

        if (!RestBaseAddress.IsAbsoluteUri || RestBaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The REST base address must use HTTPS.", nameof(RestBaseAddress));
        }

        if (!WebSocketBaseAddress.IsAbsoluteUri || WebSocketBaseAddress.Scheme != "wss")
        {
            throw new ArgumentException(
                "The WebSocket base address must use WSS.",
                nameof(WebSocketBaseAddress));
        }

        if (RequestTimeoutSeconds is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds));
        }

        if (ReconnectMinimumSeconds < 1 ||
            ReconnectMaximumSeconds < ReconnectMinimumSeconds ||
            ReconnectMaximumSeconds > 300)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReconnectMaximumSeconds),
                "Reconnect delays must be ordered, positive, and no longer than five minutes.");
        }

        return (instrument, interval);
    }
}

internal static class BinanceInterval
{
    public static BarInterval Parse(string value) => value switch
    {
        "1s" => BarInterval.Seconds(1),
        "1m" => BarInterval.Minutes(1),
        "3m" => BarInterval.Minutes(3),
        "5m" => BarInterval.Minutes(5),
        "15m" => BarInterval.Minutes(15),
        "30m" => BarInterval.Minutes(30),
        "1h" => BarInterval.Hours(1),
        "2h" => BarInterval.Hours(2),
        "4h" => BarInterval.Hours(4),
        "6h" => BarInterval.Hours(6),
        "8h" => BarInterval.Hours(8),
        "12h" => BarInterval.Hours(12),
        "1d" => BarInterval.Days(1),
        "3d" => BarInterval.Days(3),
        "1w" => BarInterval.Weeks(1),
        "1M" => BarInterval.Months(1),
        _ => throw new ArgumentException($"Unsupported Binance interval '{value}'.", nameof(value))
    };
}
