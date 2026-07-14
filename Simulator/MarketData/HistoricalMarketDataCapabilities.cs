using Brokers.Models;

namespace Simulator.MarketData;

public enum HistoricalDataSourceKind
{
    OandaCandles,
    RecordedQuotes,
    ImportedSecondCandles,
    BinanceCandles,
    InlineTestData
}

public enum SimulationPrecisionMode
{
    /// <summary>1m execution, 1m analysis base — default development mode.</summary>
    Fast,
    /// <summary>Broker-native precision: OANDA 5s execution, 1m analysis base.</summary>
    BrokerNativePrecision,
    /// <summary>1s execution from recorded/imported data only — not OANDA candles.</summary>
    HighPrecision
}

public interface IHistoricalMarketDataCapabilities
{
    string SourceName { get; }
    HistoricalDataSourceKind Kind { get; }
    IReadOnlySet<BarInterval> SupportedExecutionIntervals { get; }
    bool SupportsHistoricalBidAsk { get; }
    bool SupportsTicks { get; }
    bool SupportsInterval(BarInterval interval);
}

public sealed class OandaCandleCapabilities : IHistoricalMarketDataCapabilities
{
    public static OandaCandleCapabilities Instance { get; } = new();

    private static readonly HashSet<BarInterval> Supported =
    [
        BarInterval.Seconds(5),
        BarInterval.Seconds(10),
        BarInterval.Seconds(15),
        BarInterval.Seconds(30),
        BarInterval.Minutes(1),
        BarInterval.Minutes(2),
        BarInterval.Minutes(4),
        BarInterval.Minutes(5),
        BarInterval.Minutes(10),
        BarInterval.Minutes(15),
        BarInterval.Minutes(30),
        BarInterval.Hours(1),
        BarInterval.Hours(2),
        BarInterval.Hours(3),
        BarInterval.Hours(4),
        BarInterval.Hours(6),
        BarInterval.Hours(8),
        BarInterval.Hours(12),
        BarInterval.Days(1),
        BarInterval.Weeks(1),
        BarInterval.Months(1)
    ];

    public string SourceName => "OANDA Candles";
    public HistoricalDataSourceKind Kind => HistoricalDataSourceKind.OandaCandles;
    public IReadOnlySet<BarInterval> SupportedExecutionIntervals => Supported;
    public bool SupportsHistoricalBidAsk => false; // mid-only path today
    public bool SupportsTicks => false;
    public bool SupportsInterval(BarInterval interval) => Supported.Contains(interval);
}


public sealed class BinanceCandleCapabilities : IHistoricalMarketDataCapabilities
{
    public static BinanceCandleCapabilities Instance { get; } = new();

    private static readonly HashSet<BarInterval> Supported =
    [
        BarInterval.Seconds(1),
        BarInterval.Minutes(1),
        BarInterval.Minutes(3),
        BarInterval.Minutes(5),
        BarInterval.Minutes(15),
        BarInterval.Minutes(30),
        BarInterval.Hours(1),
        BarInterval.Hours(2),
        BarInterval.Hours(4),
        BarInterval.Hours(6),
        BarInterval.Hours(8),
        BarInterval.Hours(12),
        BarInterval.Days(1),
        BarInterval.Days(3),
        BarInterval.Weeks(1),
        BarInterval.Months(1)
    ];

    public string SourceName => "Binance public spot candles";
    public HistoricalDataSourceKind Kind => HistoricalDataSourceKind.BinanceCandles;
    public IReadOnlySet<BarInterval> SupportedExecutionIntervals => Supported;
    public bool SupportsHistoricalBidAsk => false;
    public bool SupportsTicks => false;
    public bool SupportsInterval(BarInterval interval) => Supported.Contains(interval);
}

public sealed class InlineTestDataCapabilities : IHistoricalMarketDataCapabilities
{
    public static InlineTestDataCapabilities Instance { get; } = new();

    public string SourceName => "Inline test data";
    public HistoricalDataSourceKind Kind => HistoricalDataSourceKind.InlineTestData;
    public IReadOnlySet<BarInterval> SupportedExecutionIntervals { get; } =
        new HashSet<BarInterval>
        {
            BarInterval.Seconds(1),
            BarInterval.Seconds(5),
            BarInterval.Minutes(1),
            BarInterval.Minutes(5)
        };
    public bool SupportsHistoricalBidAsk => false;
    public bool SupportsTicks => false;
    public bool SupportsInterval(BarInterval interval) => SupportedExecutionIntervals.Contains(interval);
}

public sealed class ImportedSecondCandleCapabilities : IHistoricalMarketDataCapabilities
{
    public static ImportedSecondCandleCapabilities Instance { get; } = new();

    public string SourceName => "Imported second candles";
    public HistoricalDataSourceKind Kind => HistoricalDataSourceKind.ImportedSecondCandles;
    public IReadOnlySet<BarInterval> SupportedExecutionIntervals { get; } =
        new HashSet<BarInterval> { BarInterval.Seconds(1), BarInterval.Seconds(5) };
    // The current CSV schema is timestamp,OHLC[,volume] and is parsed as midpoint data.
    // Do not advertise quote-side history until a bid/ask schema and parser exist.
    public bool SupportsHistoricalBidAsk => false;
    public bool SupportsTicks => false;
    public bool SupportsInterval(BarInterval interval) => SupportedExecutionIntervals.Contains(interval);
}

public sealed class UnavailableHistoricalCapabilities : IHistoricalMarketDataCapabilities
{
    private static readonly IReadOnlySet<BarInterval> None = new HashSet<BarInterval>();

    public UnavailableHistoricalCapabilities(string sourceName, HistoricalDataSourceKind kind)
    {
        SourceName = sourceName;
        Kind = kind;
    }

    public string SourceName { get; }
    public HistoricalDataSourceKind Kind { get; }
    public IReadOnlySet<BarInterval> SupportedExecutionIntervals => None;
    public bool SupportsHistoricalBidAsk => false;
    public bool SupportsTicks => false;
    public bool SupportsInterval(BarInterval interval) => false;
}

public sealed class HistoricalGranularityNotSupportedException : Exception
{
    public HistoricalGranularityNotSupportedException(
        string source,
        BarInterval requested,
        IEnumerable<BarInterval> supported,
        string? suggestion = null)
        : base(BuildMessage(source, requested, supported, suggestion))
    {
        SourceName = source;
        RequestedInterval = requested;
        SupportedIntervals = supported.Select(BarIntervalParser.Format).ToArray();
        Suggestion = suggestion;
        ErrorCode = "HistoricalGranularityNotSupported";
    }

    public string ErrorCode { get; }
    public string SourceName { get; }
    public BarInterval RequestedInterval { get; }
    public IReadOnlyList<string> SupportedIntervals { get; }
    public string? Suggestion { get; }

    private static string BuildMessage(
        string source,
        BarInterval requested,
        IEnumerable<BarInterval> supported,
        string? suggestion)
    {
        string list = string.Join(", ", supported.Select(BarIntervalParser.Format));
        string message =
            $"Historical granularity {BarIntervalParser.Format(requested)} is not supported by {source}. " +
            $"Supported: {list}.";
        if (!string.IsNullOrWhiteSpace(suggestion))
            message += " " + suggestion;
        return message;
    }
}

public static class HistoricalCapabilityRegistry
{
    private static readonly IHistoricalMarketDataCapabilities RecordedQuotesUnavailable =
        new UnavailableHistoricalCapabilities(
            "Recorded quote events (not implemented)",
            HistoricalDataSourceKind.RecordedQuotes);

    public static IHistoricalMarketDataCapabilities Get(HistoricalDataSourceKind kind) => kind switch
    {
        HistoricalDataSourceKind.OandaCandles => OandaCandleCapabilities.Instance,
        HistoricalDataSourceKind.ImportedSecondCandles => ImportedSecondCandleCapabilities.Instance,
        HistoricalDataSourceKind.InlineTestData => InlineTestDataCapabilities.Instance,
        HistoricalDataSourceKind.RecordedQuotes => RecordedQuotesUnavailable,
        HistoricalDataSourceKind.BinanceCandles => BinanceCandleCapabilities.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static void ValidateExecutionInterval(
        HistoricalDataSourceKind kind,
        BarInterval executionInterval)
    {
        IHistoricalMarketDataCapabilities caps = Get(kind);
        if (caps.SupportsInterval(executionInterval))
            return;

        string? suggestion = null;
        if (kind == HistoricalDataSourceKind.OandaCandles &&
            executionInterval == BarInterval.Seconds(1))
        {
            suggestion =
                "Use OANDA precision mode with 5s execution, or supply recorded/imported 1-second data. " +
                "TradingHub will not synthesize 1-second candles from larger OHLC bars.";
        }

        throw new HistoricalGranularityNotSupportedException(
            caps.SourceName,
            executionInterval,
            caps.SupportedExecutionIntervals,
            suggestion);
    }
}
