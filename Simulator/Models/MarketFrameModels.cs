using Brokers.Models;
using ChartAnnotator.Models;
using Simulator.MarketData;

namespace Simulator.Models;

/// <summary>
/// Immutable market snapshot delivered to every strategy worker for a single base-candle step.
/// Analysis snapshots are fully updated before strategy evaluation (phase B).
/// </summary>
public sealed record MarketFrame
{
    public required long Sequence { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required MarketCandle ExecutionCandle { get; init; }
    public required IReadOnlySet<BarInterval> ClosedIntervals { get; init; }
    public required IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Snapshots { get; init; }
    public required string InputStreamId { get; init; }
    public required bool IsWarmup { get; init; }
    public required bool IsLastCandle { get; init; }
}

public sealed record StrategyFrameMessage(
    MarketFrame Frame,
    bool Complete);

public sealed record StrategyFrameResult
{
    public required string StrategyId { get; init; }
    public required string StrategyName { get; init; }
    public required long Sequence { get; init; }
    public required decimal Balance { get; init; }
    public required decimal Equity { get; init; }
    public required decimal UnrealizedProfitLoss { get; init; }
    public required int OpenPositions { get; init; }
    public required int CompletedTrades { get; init; }
    public required int ActiveSetups { get; init; }
    public SimulatedTradeRecord? NewlyCompletedTrade { get; init; }
    public string? Status { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}

public sealed record HistoricalCandleRequest(
    InstrumentKey Instrument,
    BarInterval BaseInterval,
    DateTimeOffset From,
    DateTimeOffset To,
    bool RefreshCache = false,
    bool UseHistoricalBidAsk = true,
    int PageSize = 5_000,
    string? CacheDirectory = null,
    bool NoCache = false);
