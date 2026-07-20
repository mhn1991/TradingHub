using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using TradeManager;
using TradingCore.Pipeline;

namespace LiveTrading.Shadow.Outcomes;

public sealed record LiveShadowOutcomeOptions
{
    public bool Enabled { get; init; } = true;
    public int MaximumOpenPaperPositions { get; init; } = 20;
    public decimal DefaultQuantity { get; init; } = 1_000m;
    public decimal EntrySlippageBasisPoints { get; init; } = 0.5m;
    public decimal ExitSlippageBasisPoints { get; init; } = 0.5m;
    public decimal CommissionBasisPointsPerSide { get; init; }
    public decimal FinancingBasisPointsPerDay { get; init; }
    public bool RejectSecondPositionPerInstrument { get; init; } = true;

    public void Validate()
    {
        if (MaximumOpenPaperPositions < 1 || DefaultQuantity <= 0m ||
            EntrySlippageBasisPoints is < 0m or > 100m ||
            ExitSlippageBasisPoints is < 0m or > 100m ||
            CommissionBasisPointsPerSide is < 0m or > 100m ||
            FinancingBasisPointsPerDay is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(LiveShadowOutcomeOptions));
    }
}

public enum ShadowCandidateDisposition { Observed, Admitted, Rejected, Duplicate }
public enum ShadowPaperFillKind { Entry, PartialExit, Exit }
public enum ShadowOutcomeState { Completed, Ambiguous }
public enum ShadowExitReason
{
    StopLoss,
    TakeProfit,
    StopAndTargetSameCandle,
    StructuralInvalidation,
    Management,
    RecoveryAmbiguity
}

public sealed record ShadowCandidateAdmission
{
    public required LiveTradeCandidate Candidate { get; init; }
    public required StrategyActivationMode Mode { get; init; }
    public required ShadowCandidateDisposition Disposition { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public string? RejectionCode { get; init; }
    public string? Explanation { get; init; }
    public LiveQuoteSnapshot? ExecutableQuote { get; init; }
    public ShadowPaperFill? EntryFill { get; init; }
    public ShadowPaperPosition? OpenedPosition { get; init; }
    public IReadOnlyList<string> ProjectionStreams { get; init; } =
        ["shadow-candidates", "shadow-paper-fills", "shadow-paper-positions"];
}

public sealed record ShadowPaperFill
{
    public required string FillId { get; init; }
    public required string CandidateId { get; init; }
    public required string PositionId { get; init; }
    public required ShadowPaperFillKind Kind { get; init; }
    public required AgentAction Side { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal Price { get; init; }
    public required decimal SlippageCost { get; init; }
    public required decimal Commission { get; init; }
    public required DateTimeOffset FilledAt { get; init; }
    public required string Assumption { get; init; }
}

public sealed record ShadowPaperPosition
{
    public required string PositionId { get; init; }
    public required string CandidateId { get; init; }
    public required string DecisionId { get; init; }
    public string? SetupId { get; init; }
    public string? PlaybookId { get; init; }
    public string? PlaybookVersion { get; init; }
    public required string StrategyId { get; init; }
    public AgentInstanceKey? AgentInstance { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required AgentAction Side { get; init; }
    public decimal EntryConfidence { get; init; }
    public MarketRegime EntryRegime { get; init; } = MarketRegime.Unknown;
    public required decimal InitialQuantity { get; init; }
    public required decimal RemainingQuantity { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal InitialStopPrice { get; init; }
    public required decimal StopPrice { get; init; }
    public required decimal TargetPrice { get; init; }
    public required decimal InitialRiskPerUnit { get; init; }
    public required decimal EntryCommission { get; init; }
    public required decimal AccruedFinancing { get; init; }
    public required decimal RealizedGrossProfitLoss { get; init; }
    public required decimal RealizedCosts { get; init; }
    public required decimal MaximumFavourableExcursionR { get; init; }
    public required decimal MaximumAdverseExcursionR { get; init; }
    public decimal? LastMarkPrice { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required DateTimeOffset LastFinancingAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required Guid PolicyBundleId { get; init; }
    public required int PolicyRevision { get; init; }
    public required string AnalysisProfileHash { get; init; }
    public Guid? EntrySupplyDemandZoneId { get; init; }
    public decimal? EntrySupplyDemandZoneLowerPrice { get; init; }
    public decimal? EntrySupplyDemandZoneUpperPrice { get; init; }
    public SupplyDemandZoneState? EntrySupplyDemandZoneState { get; init; }
    public string? EntrySupplyDemandProfileHash { get; init; }
    public Guid? OriginatingLiquidityPoolId { get; init; }
    public Guid? OriginatingLiquiditySweepId { get; init; }
    public string? OriginatingLiquidityProfileHash { get; init; }
    public Guid? TargetLiquidityPoolId { get; init; }
    public string? TargetLiquidityProfileHash { get; init; }
    public decimal? StructuralInvalidationReference { get; init; }
    public bool EntrySupplyDemandManagementEnabled { get; init; }
    public bool EntryLiquidityManagementEnabled { get; init; }
    public string? StructuralManagementPolicyRevision { get; init; }
    public IReadOnlyList<string> CompletedReductionStageIds { get; init; } = [];
    public int AnalysisBarsSinceLastReduction { get; init; } = int.MaxValue;
    public int AnalysisBarsSinceLastAmendment { get; init; } = int.MaxValue;
    public int AnalysisBarsWithoutNewMfe { get; init; }
    public decimal LastObservedMfeR { get; init; }
    public long? LastReductionSnapshotVersion { get; init; }
    public long? LastAmendmentSnapshotVersion { get; init; }
    public bool StagnationReductionCompleted { get; init; }
    public int StructuralDeteriorationReductionCount { get; init; }
    public int MomentumDecayReductionCount { get; init; }
    public int VolatilityExhaustionReductionCount { get; init; }
    public int RegimeDegradationReductionCount { get; init; }
    public bool VolatilityExpansionSeenSinceEntry { get; init; }
    public bool RiskWindowReductionCompleted { get; init; }
    public bool ExecutionCostStressReductionCompleted { get; init; }
    public string? LastManagementAction { get; init; }
}

public sealed record ShadowPaperPositionUpdate
{
    public required ShadowPaperPosition Position { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

public sealed record ShadowManagementRequest
{
    public required string PositionId { get; init; }
    public decimal? NewStopPrice { get; init; }
    public decimal? QuantityToClose { get; init; }
    public string? StageId { get; init; }
    public PositionReductionReason? ReductionReason { get; init; }
    public long? MarketSequence { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
}

public sealed record ShadowTradeManagementEvaluation
{
    public int SchemaVersion { get; init; } = 1;
    public required string PositionId { get; init; }
    public required long MarketSequence { get; init; }
    public required TradeManagementEvaluationScope Scope { get; init; }
    public required BarInterval AnalysisInterval { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required string ConfigurationHash { get; init; }
    public required TradeManagementRecommendation Recommendation { get; init; }
    public required bool Applied { get; init; }
    public required string Explanation { get; init; }
}

public sealed record ShadowManagementEvent
{
    public required ShadowManagementRequest Request { get; init; }
    public required bool Applied { get; init; }
    public required string Explanation { get; init; }
    public ShadowPaperFill? Fill { get; init; }
    public ShadowPaperPosition? Position { get; init; }
}

public sealed record ShadowTradeOutcome
{
    public required string OutcomeId { get; init; }
    public required ShadowOutcomeState State { get; init; }
    public required ShadowExitReason ExitReason { get; init; }
    public required ShadowPaperPosition Position { get; init; }
    public ShadowPaperFill? ExitFill { get; init; }
    public required decimal GrossProfitLoss { get; init; }
    public required decimal NetProfitLoss { get; init; }
    public required decimal NetR { get; init; }
    public required decimal TotalCosts { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required string Assumption { get; init; }
}

public sealed record ShadowAdmissionResult
{
    public required ShadowCandidateAdmission Admission { get; init; }
    public ShadowPaperPosition? Position { get; init; }
}

public sealed record LiveShadowOutcomeCheckpoint
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<string> SeenCandidateIds { get; init; } = [];
    public IReadOnlyList<ShadowPaperPosition> OpenPositions { get; init; } = [];
    public long CandidateCount { get; init; }
    public long AdmittedCount { get; init; }
    public long RejectedCount { get; init; }
    public long CompletedCount { get; init; }
    public long AmbiguousCount { get; init; }
    public decimal NetProfitLoss { get; init; }
    public decimal NetR { get; init; }
    public string? LastPlaybookId { get; init; }
    public string? LastCandidateId { get; init; }
    public string? LastRejection { get; init; }
    public int? LastPolicyRevision { get; init; }
}

public sealed record LiveShadowOutcomeSnapshot
{
    public required bool Enabled { get; init; }
    public required long CandidateCount { get; init; }
    public required long AdmittedCount { get; init; }
    public required long RejectedCount { get; init; }
    public required int OpenPositionCount { get; init; }
    public required long CompletedCount { get; init; }
    public required long AmbiguousCount { get; init; }
    public required decimal NetProfitLoss { get; init; }
    public required decimal NetR { get; init; }
    public required decimal UnrealizedProfitLoss { get; init; }
    public string? LastPlaybookId { get; init; }
    public string? LastCandidateId { get; init; }
    public string? LastRejection { get; init; }
    public decimal? LastStopPrice { get; init; }
    public decimal? LastTargetPrice { get; init; }
    public int? LastPolicyRevision { get; init; }
    public required IReadOnlyList<ShadowPaperPosition> OpenPositions { get; init; }
}
