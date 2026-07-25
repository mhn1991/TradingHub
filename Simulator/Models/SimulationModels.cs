using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.MarketData;
using ChartAnnotator.Regime;
using ChartAnnotator.TargetManagement;
using Simulator.Execution;
using Simulator.Financing;

namespace Simulator.Models;

public enum OcoFillPolicy
{
    StopLossFirst,
    TakeProfitFirst,
    NearestToOpenFirst
}

public sealed record SimulationOptions
{
    public BrokerKind ModelledBroker { get; init; } = BrokerKind.Oanda;
    public string AccountId { get; init; } = "simulated-account";
    public string BaseCurrency { get; init; } = "USD";
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.001m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public bool EnforceMarginRequirements { get; init; } = true;
    public OcoFillPolicy OcoFillPolicy { get; init; } = OcoFillPolicy.StopLossFirst;
    public BaseCandleGapPolicy BaseCandleGapPolicy { get; init; } = BaseCandleGapPolicy.Throw;
    public bool CloseOpenPositionsAtEnd { get; init; }
    public IReadOnlyDictionary<string, decimal> QuoteToBaseCurrencyRates { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public int CandleCapacity { get; init; } = 2_000;
    public int LedgerCapacity { get; init; } = 20_000;
    public int OrderEventCapacity { get; init; } = 4_096;
    public ExecutionModelOptions ExecutionModel { get; init; } = new();
    public FinancingOptions Financing { get; init; } = new();
}

public enum LedgerEntryType
{
    Deposit,
    Commission,
    RealisedProfitLoss,
    MarginReserved,
    MarginReleased,
    Financing
}

public sealed record LedgerEntry
{
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required LedgerEntryType Type { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? OrderId { get; init; }
    public string? Description { get; init; }
}


public enum SimulatedTradeExitReason
{
    InitialStopLoss,
    BreakEvenStop,
    TrailedStructureStop,
    ProfitFloorStop,
    MfeGivebackStop,
    StopLoss,
    TakeProfit,
    ReverseStrategyClose,
    StrategyClose,
    StructuralInvalidation,
    TradeManagerStructureExit,
    NeoWaveInvalidationExit,
    ProfitFloorExit,
    MaximumGivebackExit,
    SafetyClose,
    EndOfSimulation,
    Unknown
}

public sealed record StopAmendmentRecord
{
    public required long RequestedSequence { get; init; }
    public long? EffectiveSequence { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public required decimal PreviousStopPrice { get; init; }
    public required decimal ProposedStopPrice { get; init; }
    public decimal? AcceptedStopPrice { get; init; }
    public required decimal OpenProfitR { get; init; }
    public required decimal LockedProfitR { get; init; }
    public required StopAmendmentReason Reason { get; init; }
    public required string Explanation { get; init; }
    public required ProtectiveStopAmendmentStatus Status { get; init; }
    public string? PreviousStopOrderId { get; init; }
    public string? CurrentStopOrderId { get; init; }
    public string? RejectionReason { get; init; }
    public string? AnalysisInterval { get; init; }
    public long? AnalysisSnapshotVersion { get; init; }
    public decimal? Atr { get; init; }
    public decimal? StructuralLevel { get; init; }
    public string? StructureSource { get; init; }
    public decimal? RawEntryPrice { get; init; }
    public decimal? CostAdjustedBreakEvenPrice { get; init; }
    public decimal? AtrBufferPrice { get; init; }
}

public enum PartialExitReason
{
    ScaleOutProfit,
    OpposingStructure,
    Stagnation,
    StructuralDeterioration,
    MomentumDecay,
    VolatilityExhaustion,
    SessionRisk,
    ExecutionCostStress,
    RiskReduction,
    RegimeDegradation,
    Manual,
    Unknown,
    /// <summary>Target-aware partial from an adaptive <see cref="ChartAnnotator.TargetManagement.TradeTargetPlan"/> (plan §4.4).</summary>
    AdaptiveTargetCheckpoint
}

public sealed record PartialExitRecord
{
    public required string ExitId { get; init; }
    public required string StageId { get; init; }
    public required long RequestedSequence { get; init; }
    public required long ExecutionSequence { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset ExecutedAt { get; init; }
    public required decimal QuantityBefore { get; init; }
    public required decimal QuantityClosed { get; init; }
    public required decimal QuantityRemaining { get; init; }
    public required decimal ExitPrice { get; init; }
    public required decimal GrossProfitLoss { get; init; }
    public required decimal AllocatedEntryCommission { get; init; }
    public required decimal ExitCommission { get; init; }
    public required decimal NetProfitLoss { get; init; }
    public required decimal RealizedR { get; init; }
    public required decimal OpenProfitRBeforeExit { get; init; }
    public required PartialExitReason Reason { get; init; }
    public string? StructureSource { get; init; }
    public decimal? StructuralLevel { get; init; }
    public required string Explanation { get; init; }
    public string? BrokerOrderId { get; init; }
}

public sealed record SimulatedTradeRecord
{
    public string StrategyId { get; init; } = "unknown";
    public string PlaybookId { get; init; } = "unknown";
    public required string StrategyName { get; init; }
    public required string SetupId { get; init; }
    public string? PositionId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required DateTimeOffset SetupStartedAt { get; init; }
    public DateTimeOffset? ConfirmationAt { get; init; }
    public required DateTimeOffset SignalCreatedAt { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public decimal? SignalPrice { get; init; }
    public decimal? EntryPrice { get; init; }
    public decimal? ExitPrice { get; init; }
    public decimal? AverageExitPrice { get; init; }
    /// <summary>Compatibility quantity; always the original opened quantity.</summary>
    public decimal Quantity { get; init; }
    public decimal InitialQuantity { get; init; }
    public decimal RemainingQuantity { get; init; }
    /// <summary>Compatibility alias for the original immutable initial stop.</summary>
    public decimal? StopLossPrice { get; init; }
    public decimal? InitialStopLossPrice { get; init; }
    public decimal? CurrentStopLossPrice { get; init; }
    public decimal? FinalStopLossPrice { get; init; }
    public string? InitialStopOrderId { get; init; }
    public string? CurrentStopOrderId { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public decimal? ExpectedRewardRisk { get; init; }
    public decimal? MaximumFavourableExcursionPrice { get; init; }
    public decimal MaximumFavourableExcursionAmount { get; init; }
    public decimal? MaximumFavourableExcursionR { get; init; }
    public DateTimeOffset? MaximumFavourableExcursionAt { get; init; }
    public decimal? MaximumAdverseExcursionPrice { get; init; }
    public decimal MaximumAdverseExcursionAmount { get; init; }
    public decimal? MaximumAdverseExcursionR { get; init; }
    public DateTimeOffset? MaximumAdverseExcursionAt { get; init; }
    public decimal GrossProfitLoss { get; init; }
    public decimal EntryCommission { get; init; }
    public decimal Commission { get; init; }
    public decimal NetProfitLoss { get; init; }
    public decimal RealizedPartialGrossProfitLoss { get; init; }
    public decimal RealizedPartialCommission { get; init; }
    public decimal RealizedPartialNetProfitLoss { get; init; }
    public decimal? RMultiple { get; init; }
    public int StopAmendmentCount { get; init; }
    public DateTimeOffset? BreakEvenActivatedAt { get; init; }
    public DateTimeOffset? StructureTrailingActivatedAt { get; init; }
    public decimal MaximumLockedInR { get; init; }
    public int PositionReductionCount { get; init; }
    public DateTimeOffset? RunnerActivatedAt { get; init; }
    public DateTimeOffset? ProfitFloorActivatedAt { get; init; }
    public DateTimeOffset? MaximumGivebackProtectionActivatedAt { get; init; }
    public IReadOnlyList<string> CompletedReductionStageIds { get; init; } = [];
    public IReadOnlyList<PartialExitRecord> PartialExits { get; init; } = [];
    public IReadOnlyList<StopAmendmentRecord> StopAmendments { get; init; } = [];
    public SimulatedTradeExitReason ExitReason { get; init; }
    public string? StopSource { get; init; }
    public string? TargetSource { get; init; }
    public required string SetupReason { get; init; }
    public string? ExitReasonText { get; init; }
    public decimal TotalFinancing { get; init; }
    public decimal NetProfitAfterFinancing { get; init; }
    public MarketRegime EntryRegime { get; init; } = MarketRegime.Unknown;
    public MarketRegime CurrentRegime { get; init; } = MarketRegime.Unknown;
    public string EntryManagementProfileId { get; init; } = "default";
    public string CurrentManagementProfileId { get; init; } = "default";
    public string? ManagementProfileSwitchReason { get; init; }
    public decimal EntryConfidence { get; init; }
    public string EntrySetupType { get; init; } = "Unknown";
    /// <summary>
    /// AgentDecision.PlaybookId at entry (e.g. "structural.indicator-confluence") - null for
    /// agents that don't set it (Legacy/Improved Progressive). Lets trade management vary by
    /// which playbook produced the trade rather than only by strategy id - see
    /// PlaybookAwareTradeManager, which uses this to give IndicatorConfluencePlaybook's ATR-only
    /// trades (no zone/pool to anchor a structural trail to) their own profile instead of
    /// StructuralDefaults' zone-anchored one.
    /// </summary>
    public string? EntryPlaybookId { get; init; }
    public string EntrySession { get; init; } = "Unknown";
    public string EntryVolatilityBucket { get; init; } = "Unknown";
    public decimal? EntryRegimeConfidence { get; init; }
    public decimal? BaseRequestedQuantity { get; init; }
    public decimal? AllocatedQuantity { get; init; }
    public decimal? PlannedStopRiskAccountCurrency { get; init; }
    public string? PortfolioReservationId { get; init; }
    public string? CorrelationClusterId { get; init; }
    public decimal? RegimeRiskMultiplier { get; init; }
    public decimal? TradingConditionRiskMultiplier { get; init; }
    public decimal? CorrelationRiskMultiplier { get; init; }
    public decimal? StrategyAllocationRiskMultiplier { get; init; }
    public decimal? EquityProtectionRiskMultiplier { get; init; }
    public decimal? SetupCalibrationRiskMultiplier { get; init; }
    public decimal? MetaLabelRiskMultiplier { get; init; }
    public decimal? NeoWaveRiskMultiplier { get; init; }
    public decimal? StructuralEvidenceRiskMultiplier { get; init; }
    public Guid? EntrySupplyDemandZoneId { get; init; }
    public decimal? EntrySupplyDemandZoneLowerPrice { get; init; }
    public decimal? EntrySupplyDemandZoneUpperPrice { get; init; }
    public string? EntrySupplyDemandZoneState { get; init; }
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
    public string? EntryNeoWaveHypothesisId { get; init; }
    public decimal? EntryNeoWaveInvalidationPrice { get; init; }
    public string? EntryNeoWavePatternType { get; init; }
    public decimal? EntryNeoWaveStructuralScore { get; init; }
    public decimal? EntryNeoWaveConflictScore { get; init; }
    public decimal? FinalRiskBudgetMultiplier { get; init; }
    /// <summary>
    /// Fraction of causal analysis snapshots whose regime direction agreed with the entry side
    /// at decision time (<see cref="RiskManager.Calibration.MetaLabelFeatureFactory.ComputeMultiTimeframeAlignment"/>).
    /// Captured for every trade regardless of whether a meta-model is configured, so
    /// <c>calibrate-metamodel</c> can bucket by it later.
    /// </summary>
    public decimal? EntryMultiTimeframeAlignment { get; init; }
    public string? EntryCciConfirmationState { get; init; }
    public string? EntryStructuralConfluenceState { get; init; }
    /// <summary>
    /// Bar-by-bar MFE/MAE excursion path from entry to close. Null unless
    /// <see cref="BacktestRuntimeOptions.DetailedExcursionTracking"/> is enabled (off by
    /// default - zero size/behavior change for every run that doesn't opt in). Feeds
    /// QuantResearch's trade-management cohort calibration, which needs a path rather than
    /// just the peak <see cref="MaximumFavourableExcursionR"/>/<see cref="MaximumAdverseExcursionR"/>.
    /// </summary>
    public IReadOnlyList<SimulatedTradePathPoint>? ExcursionPath { get; init; }

    /// <summary>
    /// Adaptive target management (Structural Indicator and Adaptive Target Management Plan
    /// §5.2, §5.5). Null unless the trade was opened under a structural-confluence-v2 managed
    /// policy, so every existing persisted/replayed record deserializes unchanged.
    /// </summary>
    public TradeExitPolicy? ExitPolicy { get; init; }
    public TradeTargetPlan? TargetPlan { get; init; }
    public IReadOnlyList<TargetPlanRevision> TargetPlanRevisions { get; init; } = [];
    /// <summary>Weighted planned reward at entry, per plan §3.7's <c>PlannedR</c> definition.</summary>
    public decimal? PlannedR { get; init; }
    /// <summary>Net trade P&amp;L divided by the original, never-recalculated risk cash (plan §3.7).</summary>
    public decimal? RealizedR { get; init; }
    /// <summary>Original cost-adjusted risk cash, pinned at entry (plan §3.7).</summary>
    public decimal? InitialRiskCash { get; init; }
}

public sealed record SimulatedTradePathPoint
{
    public required int BarsAfterEntry { get; init; }
    public required decimal MfeR { get; init; }
    public required decimal MaeR { get; init; }
}

public sealed record SimulationResult
{
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required decimal StartingBalance { get; init; }
    public required decimal FinalBalance { get; init; }
    public required decimal FinalEquity { get; init; }
    public required decimal UnrealizedProfitLoss { get; init; }
    public required decimal NetProfit { get; init; }
    public required decimal TotalCommission { get; init; }
    public required int SubmittedOrders { get; init; }
    public required int FilledOrders { get; init; }
    public required int RejectedOrders { get; init; }
    public required IReadOnlyList<BrokerPosition> OpenPositions { get; init; }
    public required IReadOnlyList<LedgerEntry> Ledger { get; init; }
    public IReadOnlyList<SimulatedTradeRecord> Trades { get; init; } = [];
    public SimulationExecutionPerformance ExecutionPerformance { get; init; } = new();

    /// <summary>Persistent equity-protection peak equity for the run; 0 when the feature was disabled.</summary>
    public decimal EquityProtectionPeakEquity { get; init; }

    /// <summary>Distinct equity-protection tiers activated at least once during the run.</summary>
    public int EquityProtectionActivationCount { get; init; }
}

public sealed record SimulationExecutionPerformance
{
    public decimal AverageSpreadPrice { get; init; }
    public decimal AverageSlippagePrice { get; init; }
    public decimal AverageStopSlippagePrice { get; init; }
    public int GapFills { get; init; }
    public int PartialFills { get; init; }
    public int RejectedAmendments { get; init; }
    public decimal FinancingTotal { get; init; }
}
