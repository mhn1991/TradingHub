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
    public StrategyPerformanceSnapshot? Performance { get; init; }
    public OpenPositionManagementSnapshot? OpenPositionManagement { get; init; }
}

public sealed record OpenPositionManagementSnapshot
{
    public required string SetupId { get; init; }
    public required string Side { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal InitialQuantity { get; init; }
    public required decimal RemainingQuantity { get; init; }
    public required int PositionReductionCount { get; init; }
    public required bool ReductionPending { get; init; }
    public required decimal InitialStop { get; init; }
    public required decimal CurrentStop { get; init; }
    public decimal? Target { get; init; }
    public required decimal CurrentOpenR { get; init; }
    public required decimal MaximumOpenR { get; init; }
    public required decimal LockedInR { get; init; }
    public required string TrailingMode { get; init; }
    public string? LastManagementAction { get; init; }
    public string? LastManagementReason { get; init; }
    public DateTimeOffset? NextManagementIntervalClose { get; init; }
}

public sealed record StrategyPerformanceSnapshot
{
    public required decimal GrossProfit { get; init; }
    public required decimal NetProfit { get; init; }
    public required decimal Commissions { get; init; }
    public required int TradeCount { get; init; }
    public required int Wins { get; init; }
    public required int Losses { get; init; }
    public required decimal WinRatePercent { get; init; }
    public decimal? ProfitFactor { get; init; }
    public decimal? AverageR { get; init; }
    public decimal? MedianR { get; init; }
    public required decimal Expectancy { get; init; }
    public required decimal MaximumDrawdown { get; init; }
    public double? AverageHoldingSeconds { get; init; }
    public double? AverageSetupSeconds { get; init; }
    public required decimal AverageMfe { get; init; }
    public required decimal AverageMae { get; init; }
    public decimal? MfeCapturedPercent { get; init; }
    public required IReadOnlyDictionary<string, int> ExitReasons { get; init; }
    // Additive performance fields must remain optional when persisted snapshots are read.
    // Jobs written before Phase 4 do not contain these properties and safely represent
    // each metric as zero until a new progress snapshot is calculated.
    public int BreakEvenActivations { get; init; }
    public int StructureTrailingActivations { get; init; }
    public int AcceptedStopAmendments { get; init; }
    public int RejectedStopAmendments { get; init; }
    public int UnsupportedStopAmendments { get; init; }
    public decimal AverageAmendmentsPerTrade { get; init; }
    public decimal AverageMaximumLockedR { get; init; }
    public decimal AverageProfitGivebackFromMfeR { get; init; }
    public int TrailingStopExits { get; init; }
    public int BreakEvenExits { get; init; }
    public int PositionReductions { get; init; }
    public decimal AveragePartialExitsPerTrade { get; init; }
    public decimal PartialExitNetProfit { get; init; }
    public int StagnationReductions { get; init; }
    public int StructuralDeteriorationReductions { get; init; }
    public int MomentumDecayReductions { get; init; }
    public int VolatilityExhaustionReductions { get; init; }
    public int SessionRiskReductions { get; init; }
    public int ExecutionCostStressReductions { get; init; }
    public int RunnerActivations { get; init; }
    public int ProfitFloorStopExits { get; init; }
    public int MfeGivebackStopExits { get; init; }
    public int ProfitFloorExits { get; init; }
    public int MaximumGivebackExits { get; init; }

    public static StrategyPerformanceSnapshot FromTrades(
        IReadOnlyList<SimulatedTradeRecord> trades)
    {
        SimulatedTradeRecord[] closed = trades.Where(trade => trade.ClosedAt is not null).ToArray();
        decimal gross = closed.Sum(trade => trade.GrossProfitLoss);
        decimal net = closed.Sum(trade => trade.NetProfitLoss);
        decimal commissions = closed.Sum(trade => trade.Commission);
        int wins = closed.Count(trade => trade.NetProfitLoss > 0m);
        int losses = closed.Count(trade => trade.NetProfitLoss < 0m);
        decimal grossWins = closed.Where(trade => trade.NetProfitLoss > 0m).Sum(trade => trade.NetProfitLoss);
        decimal grossLosses = Math.Abs(closed.Where(trade => trade.NetProfitLoss < 0m).Sum(trade => trade.NetProfitLoss));
        decimal[] rValues = closed.Where(trade => trade.RMultiple is not null)
            .Select(trade => trade.RMultiple!.Value)
            .OrderBy(value => value)
            .ToArray();

        decimal running = 0m;
        decimal peak = 0m;
        decimal maximumDrawdown = 0m;
        foreach (SimulatedTradeRecord trade in closed.OrderBy(trade => trade.ClosedAt))
        {
            running += trade.NetProfitLoss;
            peak = Math.Max(peak, running);
            maximumDrawdown = Math.Max(maximumDrawdown, peak - running);
        }

        double[] holdingSeconds = closed
            .Where(trade => trade.OpenedAt is not null && trade.ClosedAt is not null)
            .Select(trade => (trade.ClosedAt!.Value - trade.OpenedAt!.Value).TotalSeconds)
            .ToArray();
        double[] setupSeconds = closed
            .Where(trade => trade.OpenedAt is not null)
            .Select(trade => (trade.OpenedAt!.Value - trade.SetupStartedAt).TotalSeconds)
            .ToArray();
        SimulatedTradeRecord[] withMfe = closed
            .Where(trade => trade.MaximumFavourableExcursionAmount > 0m)
            .ToArray();

        return new StrategyPerformanceSnapshot
        {
            GrossProfit = gross,
            NetProfit = net,
            Commissions = commissions,
            TradeCount = closed.Length,
            Wins = wins,
            Losses = losses,
            WinRatePercent = closed.Length == 0 ? 0m : 100m * wins / closed.Length,
            ProfitFactor = grossLosses > 0m ? grossWins / grossLosses : null,
            AverageR = rValues.Length == 0 ? null : rValues.Average(),
            MedianR = rValues.Length == 0
                ? null
                : rValues.Length % 2 == 1
                    ? rValues[rValues.Length / 2]
                    : (rValues[rValues.Length / 2 - 1] + rValues[rValues.Length / 2]) / 2m,
            Expectancy = closed.Length == 0 ? 0m : net / closed.Length,
            MaximumDrawdown = maximumDrawdown,
            AverageHoldingSeconds = holdingSeconds.Length == 0 ? null : holdingSeconds.Average(),
            AverageSetupSeconds = setupSeconds.Length == 0 ? null : setupSeconds.Average(),
            AverageMfe = closed.Length == 0
                ? 0m
                : closed.Average(trade => trade.MaximumFavourableExcursionAmount),
            AverageMae = closed.Length == 0
                ? 0m
                : closed.Average(trade => trade.MaximumAdverseExcursionAmount),
            MfeCapturedPercent = withMfe.Length == 0
                ? null
                : withMfe.Average(trade =>
                    100m * trade.GrossProfitLoss / trade.MaximumFavourableExcursionAmount),
            ExitReasons = closed
                .GroupBy(trade => trade.ExitReason.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            BreakEvenActivations = closed.Count(trade => trade.BreakEvenActivatedAt is not null),
            StructureTrailingActivations = closed.Count(trade => trade.StructureTrailingActivatedAt is not null),
            AcceptedStopAmendments = closed.Sum(trade => trade.StopAmendments.Count(amendment =>
                amendment.Status is ProtectiveStopAmendmentStatus.Accepted or
                    ProtectiveStopAmendmentStatus.Replaced)),
            RejectedStopAmendments = closed.Sum(trade => trade.StopAmendments.Count(amendment =>
                amendment.Status == ProtectiveStopAmendmentStatus.Rejected)),
            UnsupportedStopAmendments = closed.Sum(trade => trade.StopAmendments.Count(amendment =>
                amendment.Status == ProtectiveStopAmendmentStatus.Unsupported)),
            AverageAmendmentsPerTrade = closed.Length == 0
                ? 0m
                : closed.Average(trade => (decimal)trade.StopAmendmentCount),
            AverageMaximumLockedR = closed.Length == 0
                ? 0m
                : closed.Average(trade => trade.MaximumLockedInR),
            AverageProfitGivebackFromMfeR = closed.Length == 0
                ? 0m
                : closed.Average(trade =>
                    Math.Max(0m, (trade.MaximumFavourableExcursionR ?? 0m) - (trade.RMultiple ?? 0m))),
            TrailingStopExits = closed.Count(trade =>
                trade.ExitReason == SimulatedTradeExitReason.TrailedStructureStop),
            BreakEvenExits = closed.Count(trade =>
                trade.ExitReason == SimulatedTradeExitReason.BreakEvenStop),
            PositionReductions = closed.Sum(trade => trade.PositionReductionCount),
            AveragePartialExitsPerTrade = closed.Length == 0
                ? 0m
                : closed.Average(trade => (decimal)trade.PartialExits.Count),
            PartialExitNetProfit = closed.Sum(trade => trade.RealizedPartialNetProfitLoss),
            StagnationReductions = closed.Sum(trade => trade.PartialExits.Count(exit =>
                exit.Reason == PartialExitReason.Stagnation)),
            StructuralDeteriorationReductions = closed.Sum(trade => trade.PartialExits.Count(exit =>
                exit.Reason == PartialExitReason.StructuralDeterioration)),
            MomentumDecayReductions = closed.Sum(trade => trade.PartialExits.Count(exit =>
                exit.Reason == PartialExitReason.MomentumDecay)),
            VolatilityExhaustionReductions = closed.Sum(trade => trade.PartialExits.Count(exit =>
                exit.Reason == PartialExitReason.VolatilityExhaustion)),
            SessionRiskReductions = closed.Sum(trade => trade.PartialExits.Count(exit =>
                exit.Reason == PartialExitReason.SessionRisk)),
            ExecutionCostStressReductions = closed.Sum(trade => trade.PartialExits.Count(exit =>
                exit.Reason == PartialExitReason.ExecutionCostStress)),
            RunnerActivations = closed.Count(trade => trade.RunnerActivatedAt is not null),
            ProfitFloorStopExits = closed.Count(trade =>
                trade.ExitReason == SimulatedTradeExitReason.ProfitFloorStop),
            MfeGivebackStopExits = closed.Count(trade =>
                trade.ExitReason == SimulatedTradeExitReason.MfeGivebackStop),
            ProfitFloorExits = closed.Count(trade =>
                trade.ExitReason == SimulatedTradeExitReason.ProfitFloorExit),
            MaximumGivebackExits = closed.Count(trade =>
                trade.ExitReason == SimulatedTradeExitReason.MaximumGivebackExit)
        };
    }
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
    public string? SimulationConfigurationId { get; init; }
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
    public long ManagementEvaluations { get; init; }
    public long StopAmendmentRequests { get; init; }
    public long AcceptedStopAmendments { get; init; }
    public long RejectedStopAmendments { get; init; }
    public long PositionReductionRequests { get; init; }
    public long AcceptedPositionReductions { get; init; }
    public long RejectedPositionReductions { get; init; }
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
