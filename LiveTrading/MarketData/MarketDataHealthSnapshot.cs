using Brokers.Models;
using TradingCore.MarketData;

namespace LiveTrading.MarketData;

public enum LiveMarketState
{
    Disabled,
    WarmingUp,
    Ready,
    CatchingUp,
    Stale,
    Degraded,
    Faulted
}

public sealed record MarketDataHealthSnapshot
{
    public required InstrumentKey Instrument { get; init; }
    public required LiveMarketState State { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public DateTimeOffset? LastQuoteAt { get; init; }
    public DateTimeOffset? LastM1CloseAt { get; init; }
    public required bool IsQuoteStale { get; init; }
    public required long ProcessedCandleCount { get; init; }
    public required long DuplicateCandleCount { get; init; }
    public required long GapDetectedCount { get; init; }
    public required IReadOnlyList<DataQualityIssue> RecentIssues { get; init; }
}
