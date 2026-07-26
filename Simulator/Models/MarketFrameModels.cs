using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using Simulator.MarketData;
using TradeManager;
using TradingCore.Pipeline;

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
    /// <summary>Completed analysis-base candle, present only on analysis close frames.</summary>
    public Candle? AnalysisBaseCandle { get; init; }
    public required IReadOnlySet<BarInterval> ClosedIntervals { get; init; }
    /// <summary>Primary/back-compat resolved snapshots - the first analysis profile trading
    /// this instrument. Consumers that are not yet profile-aware (replay/execution-detail
    /// capture) read this directly; strategy evaluation prefers
    /// <see cref="SnapshotsByProfile"/>[session's own profile] when present via
    /// <c>StrategySimulationSession.ResolveSnapshots</c>.</summary>
    public required IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Snapshots { get; init; }
    /// <summary>Per-analysis-profile resolved snapshots for this instrument at this candle.
    /// Null for callers/tests that construct a <see cref="MarketFrame"/> directly without
    /// going through a multi-profile-aware engine - <see cref="Snapshots"/> is always the
    /// safe fallback in that case.</summary>
    public IReadOnlyDictionary<AnalysisProfileKey, IReadOnlyDictionary<BarInterval, AnalysisSnapshot>>? SnapshotsByProfile { get; init; }
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
    public IReadOnlyList<StrategyReplayEvent> Events { get; init; } = [];
    public string? ExecutionDetailSetupId { get; init; }
    public bool CaptureExecutionDetail { get; init; }
    public string? Status { get; init; }
    public TimeSpan ProcessingTime { get; init; }
}

public enum StrategyReplayEventType
{
    SetupCreated,
    SetupAdvanced,
    SetupExpired,
    SetupInvalidated,
    StructuralPlaybookEvaluated,
    PriceActionEvaluated,
    PriceActionConfirmed,
    SignalCreated,
    RiskRejected,
    OrderSubmitted,
    OrderRejected,
    OrderFilled,
    PositionOpened,
    BreakEvenActivated,
    StructureTrailActivated,
    ProfitFloorActivated,
    MaximumGivebackProtectionActivated,
    TradeManagementEvaluated,
    PartialExitRecommended,
    PartialExitSubmitted,
    PartialExitAccepted,
    PartialExitRejected,
    ProtectiveQuantityUpdated,
    RunnerActivated,
    SafetyStateChanged,
    StopAmendmentRequested,
    StopAmendmentAccepted,
    StopAmendmentRejected,
    StopAmendmentUnsupported,
    StopHit,
    TargetHit,
    StrategyCloseRequested,
    PositionClosed,
    TradeCompleted,
    StrategyFailed,
    TradingConditionEvaluated,
    TradingConditionRejected,
    MarketRegimeChanged,
    MarketRegimeConfirmed,
    RiskBudgetAdjusted,
    PortfolioOpportunityCreated,
    PortfolioOpportunityRanked,
    PortfolioRiskReserved,
    PortfolioRiskReservationRejected,
    PortfolioRiskReservationReleased,
    PortfolioQuantityReduced,
    CurrencyExposureLimitReached,
    CorrelationPenaltyApplied,
    AccountHighWatermarkUpdated,
    StrategyHighWatermarkUpdated,
    EquityProtectionTierActivated,
    EquityProtectionReductionRequested,
    EquityProtectionFlattenRequested,
    EquityProtectionRecovered,
    FinancingCharged,
    ExecutionSpreadAdjusted,
    ExecutionSlippageApplied,
    GapThroughStop,
    PartialFill,
    OperationFaultInjected,
    SetupCalibrationEvaluated,
    SetupCalibrationRejected,
    MetaLabelEvaluated,
    MetaLabelRejected
}

public sealed record StrategyReplayEvent
{
    public required StrategyReplayEventType Type { get; init; }
    public required string StrategyId { get; init; }
    public string? SetupId { get; init; }
    public string? PositionId { get; init; }
    public string? DecisionId { get; init; }
    public string? OrderId { get; init; }
    public string? ReservationId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset EventTime { get; init; }
    public decimal? PreviousStop { get; init; }
    public decimal? ProposedStop { get; init; }
    public decimal? AcceptedStop { get; init; }
    public StopAmendmentReason? AmendmentReason { get; init; }
    public decimal? OpenProfitR { get; init; }
    public decimal? LockedProfitR { get; init; }
    public string? AnalysisInterval { get; init; }
    public long? AnalysisSnapshotVersion { get; init; }
    public string? Reason { get; init; }
    public string? ReasonCode { get; init; }
    public decimal? PreviousValue { get; init; }
    public decimal? NewValue { get; init; }
    public string? OptionOrModelVersion { get; init; }
    public string? EvaluationSetupId { get; init; }
    public string? PlaybookId { get; init; }
    public PriceActionDirection? StructuralDirection { get; init; }
    public StructuralSetupLifecycle? StructuralLifecycle { get; init; }
    public bool? IsEntryEligible { get; init; }
    public bool? IsReady { get; init; }
    public bool? IsSelected { get; init; }
    public StructuralPlaybookOutcome? PlaybookOutcome { get; init; }
    public string? PrimaryBlockingReasonCode { get; init; }
    public IReadOnlyList<string> FailedGateReasonCodes { get; init; } = [];
    public IReadOnlyList<string> SupportingEvidence { get; init; } = [];
    public IReadOnlyList<string> ConflictingEvidence { get; init; } = [];
    public decimal? StructuralConfidence { get; init; }
    public decimal? MandatoryQualityFloor { get; init; }
    public PriceActionEventType? PriceActionTrigger { get; init; }
    public decimal? PriceActionConfidence { get; init; }
    public decimal? QuantityBefore { get; init; }
    public decimal? QuantityChanged { get; init; }
    public decimal? QuantityRemaining { get; init; }
    public decimal? RealizedProfitLoss { get; init; }
    public decimal? RealizedR { get; init; }
    public string? ReductionStageId { get; init; }
    public PositionReductionReason? PositionReductionReason { get; init; }
    public decimal? ProfitFloorR { get; init; }
    public decimal? MaximumGivebackFloorR { get; init; }
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
