using Brokers.Models;

namespace LiveTrading.MarketData;

public enum LiveMarketEventKind
{
    Quote,
    CandleClosed,
    StreamFault,
    StreamRecovered
}

public abstract record LiveMarketEvent
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required LiveMarketEventKind Kind { get; init; }
}

public sealed record LiveQuoteMarketEvent : LiveMarketEvent
{
    public required LiveQuoteSnapshot Quote { get; init; }
}

/// <summary>Always carries a base-interval (M1) candle. Higher-timeframe candles are derived
/// downstream by <see cref="ChartAnnotator.MarketData.MultiTimeframeAggregator"/> and are never
/// published as a source event.</summary>
public sealed record CandleClosedMarketEvent : LiveMarketEvent
{
    public required Candle Candle { get; init; }
    public required BarInterval Interval { get; init; }
}

public sealed record StreamFaultMarketEvent : LiveMarketEvent
{
    public required string Reason { get; init; }
}

public sealed record StreamRecoveredMarketEvent : LiveMarketEvent;
