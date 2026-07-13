using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Models;

public sealed record SimulationJobHandle(
    Guid SimulationId,
    SimulationJobStatus Status);

public sealed record StrategyProgressSnapshot
{
    public required string StrategyName { get; init; }
    public required string StrategyId { get; init; }
    public required decimal Balance { get; init; }
    public required decimal Equity { get; init; }
    public required decimal UnrealizedProfitLoss { get; init; }
    public required int OpenPositions { get; init; }
    public required int CompletedTrades { get; init; }
    public required int ActiveSetups { get; init; }
    public required decimal NetProfit { get; init; }
    public string? Status { get; init; }
    public string? LastError { get; init; }
}

public sealed record SimulationJobSnapshot
{
    public required Guid Id { get; init; }
    /// <summary>Monotonic revision; repository must not overwrite N+1 with N.</summary>
    public long Revision { get; init; }
    public required SimulationJobStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>Canonical instrument key string (e.g. FX:GBP/JPY).</summary>
    public required string Instrument { get; init; }
    public required DateTimeOffset RequestedFrom { get; init; }
    public required DateTimeOffset RequestedTo { get; init; }
    public DateTimeOffset? WarmupFrom { get; init; }
    public DateTimeOffset? CurrentMarketTime { get; init; }
    public required long ProcessedBaseCandles { get; init; }
    public long? EstimatedBaseCandleCount { get; init; }
    public required decimal ProgressPercent { get; init; }
    public required decimal CandlesPerSecond { get; init; }
    public required IReadOnlyList<StrategyProgressSnapshot> Strategies { get; init; }
    public string? Error { get; init; }
    public bool IsComplete { get; init; }
    public string? OutputDirectory { get; init; }
    public string? InputStreamId { get; init; }
    public string? InputRequestId { get; init; }
    public string? InputHash { get; init; }
    public string? DataSourceStatus { get; init; }
    public HistoricalSourceProgress? SourceProgress { get; init; }
    public BacktestRequest? Request { get; init; }
    public MarketDataQualityReport? DataQuality { get; init; }
    public IReadOnlyList<StrategyWorkerMetrics>? WorkerMetrics { get; init; }
}

public sealed record BacktestProgress
{
    public required Guid SimulationId { get; init; }
    public required SimulationJobStatus Status { get; init; }
    public required DateTimeOffset CurrentMarketTime { get; init; }
    public required DateTimeOffset EvaluationStart { get; init; }
    public required DateTimeOffset EvaluationEnd { get; init; }
    public required long ProcessedBaseCandles { get; init; }
    public long? EstimatedTotalBaseCandles { get; init; }
    public required decimal ProgressPercent { get; init; }
    public required decimal CandlesPerSecond { get; init; }
    public required IReadOnlyList<StrategyProgressSnapshot> Strategies { get; init; }
    public string? DataSourceStatus { get; init; }
}

public sealed record StrategyWorkerMetrics
{
    public required string StrategyName { get; init; }
    public required long ProcessedFrames { get; init; }
    public required TimeSpan TotalProcessingTime { get; init; }
    public required TimeSpan MaximumFrameProcessingTime { get; init; }
    public required TimeSpan AverageFrameProcessingTime { get; init; }
    public required TimeSpan BarrierWaitTime { get; init; }
    public required int PeakChannelOccupancy { get; init; }
}

public sealed record HistoricalSourceProgress
{
    public required string Phase { get; init; }
    public required bool FromCache { get; init; }
    public required long CandlesRead { get; init; }
    public required int PagesRead { get; init; }
    public DateTimeOffset? LatestCandle { get; init; }
    public long? EstimatedCandles { get; init; }
    public decimal? Percent { get; init; }
}

public sealed record AnalysisSnapshotSet
{
    public required long Version { get; init; }
    public required IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Snapshots { get; init; }
}

public sealed record MarketDataQualityReport
{
    public required long CandleCount { get; init; }
    public required long DuplicateCount { get; init; }
    public required long OutOfOrderCount { get; init; }
    public required long MissingIntervalCount { get; init; }
    public required long WeekendGapCount { get; init; }
    public required long SessionGapCount { get; init; }
    public required long IncompleteAggregateCount { get; init; }
    public required DateTimeOffset FirstCandle { get; init; }
    public required DateTimeOffset LastCandle { get; init; }
    public required string InputHash { get; init; }
}

public sealed record StrategySimulationResult
{
    public required string StrategyId { get; init; }
    public required string StrategyName { get; init; }
    public required SimulationResult Result { get; init; }
    public required StrategyWorkerMetrics Metrics { get; init; }
    public bool IsComplete { get; init; } = true;
    public long? FailedAtSequence { get; init; }
    public string? FailureMessage { get; init; }
}

public sealed record ComparativeSimulationResult
{
    public required Guid SimulationId { get; init; }
    public required string InputStreamId { get; init; }
    public required string InputHash { get; init; }
    public required MarketDataQualityReport DataQuality { get; init; }
    public required IReadOnlyList<StrategySimulationResult> Strategies { get; init; }
    public required string OutputDirectory { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required long ProcessedBaseCandles { get; init; }
    public required FillModel FillModel { get; init; }
}

public sealed record StrategyFailureRecord
{
    public required Guid SimulationId { get; init; }
    public required string StrategyName { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset MarketTime { get; init; }
    public required string InputStreamId { get; init; }
    public string? CurrentSetup { get; init; }
    public string? OpenPosition { get; init; }
    public int PendingOrders { get; init; }
    public string? LastCompletedTrade { get; init; }
    public required string Exception { get; init; }
}
