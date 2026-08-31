using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.NeoWave;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Agent.Models;

public enum AgentAction
{
    Observe,
    Buy,
    Sell,
    Close,
    Cancel
}

public enum StructuralSetupLifecycle
{
    Dormant,
    Armed,
    CatalystObserved,
    AwaitingTrigger,
    CandidateProduced,
    Invalidated,
    Expired
}

public enum StructuralPlaybookOutcome
{
    NotReady,
    EntryBlocked,
    RoutedOut,
    ArbitrationConflict,
    ArbitrationLost,
    Selected
}

public sealed record StructuralPlaybookDiagnostic
{
    public required string PlaybookId { get; init; }
    public required string PlaybookVersion { get; init; }
    public required PriceActionDirection Direction { get; init; }
    public string? SetupId { get; init; }
    public required StructuralSetupLifecycle Lifecycle { get; init; }
    public required bool IsEntryEligible { get; init; }
    public required bool IsReady { get; init; }
    public required bool IsSelected { get; init; }
    public required StructuralPlaybookOutcome Outcome { get; init; }
    public required string EvaluationReasonCode { get; init; }
    public required string OutcomeReasonCode { get; init; }
    public string? PrimaryBlockingReasonCode { get; init; }
    public IReadOnlyList<string> FailedGateReasonCodes { get; init; } = [];
    public IReadOnlyList<string> SupportingEvidence { get; init; } = [];
    public IReadOnlyList<string> ConflictingEvidence { get; init; } = [];
    public decimal Confidence { get; init; }
    public decimal MandatoryQualityFloor { get; init; }
}

public sealed record AgentDecision
{
    public string? DecisionId { get; init; }
    /// <summary>Optional caller-owned idempotency key for broker submission.</summary>
    public string? ClientOrderId { get; init; }
    /// <summary>The broker order targeted by a <see cref="AgentAction.Cancel"/> decision.</summary>
    public string? BrokerOrderId { get; init; }
    public string? SetupId { get; init; }
    public string? StrategyName { get; init; }
    public string? StrategyId { get; init; }
    public DateTimeOffset? SetupStartedAt { get; init; }
    public DateTimeOffset? ConfirmationAt { get; init; }
    public BarInterval? SignalInterval { get; init; }
    public required AgentAction Action { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public decimal? SuggestedQuantity { get; init; }
    public QuantityUnit QuantityUnit { get; init; } = QuantityUnit.Units;
    public StandardOrderType OrderType { get; init; } = StandardOrderType.Market;
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public string? StopSource { get; init; }
    public string? TargetSource { get; init; }
    public decimal? ExpectedRewardRisk { get; init; }

    /// <summary>
    /// Adaptive target management (Structural Indicator and Adaptive Target Management Plan
    /// §5.2). Null for every legacy/v1 decision and for non-structural agents, so old records
    /// deserialize with unchanged legacy behavior. When set, <see cref="TakeProfitPrice"/> is
    /// only populated for <see cref="TradeExitPolicy.FixedStructuralTarget"/>; managed policies
    /// leave it null because a hard broker target is never submitted for them (plan §4.3).
    /// </summary>
    public TradeExitPolicy? ExitPolicy { get; init; }
    public TradeTargetPlan? TargetPlan { get; init; }

    public required decimal Confidence { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Reason { get; init; }
    public string? ReasonCode { get; init; }
    public PriceActionEventType? PriceActionTrigger { get; init; }
    public decimal? PriceActionConfidence { get; init; }
    /// <summary>Composite setup type that gated/annotated the entry, when present.</summary>
    public PriceActionSetupType? PriceActionSetupType { get; init; }
    public string? PriceActionSetupId { get; init; }
    public decimal? PriceActionSetupReferenceLevel { get; init; }

    public string? PlaybookId { get; init; }
    public string? PlaybookVersion { get; init; }
    public string? StructuralSetupId { get; init; }
    public StructuralSetupLifecycle? StructuralLifecycle { get; init; }
    public decimal? ContextQuality { get; init; }
    public decimal? LocationQuality { get; init; }
    public decimal? CatalystQuality { get; init; }
    public decimal? TriggerQuality { get; init; }
    public decimal? ConfirmationQuality { get; init; }
    public decimal? GeometryQuality { get; init; }
    public string? CciConfirmationState { get; init; }
    public decimal? EntryCci { get; init; }
    public decimal? EntryCciMomentumChange { get; init; }
    public string? EntryCciRelationship { get; init; }
    public decimal? SweepPenetrationAtr { get; init; }
    public decimal? ReclaimStrength { get; init; }
    public int? LiquidityPoolTouchCount { get; init; }
    public int? SupplyDemandZoneTouchCount { get; init; }
    public decimal? SupplyDemandPenetrationRatio { get; init; }
    public bool? SupplyDemandLiquidityConfluence { get; init; }
    public decimal? DistanceToNearestTargetAtr { get; init; }
    public decimal? DistanceToInvalidationAtr { get; init; }
    public IReadOnlyList<string> StructuralSetupReasonCodes { get; init; } = [];
    public IReadOnlyList<StructuralPlaybookDiagnostic> StructuralPlaybookDiagnostics { get; init; } = [];

    /// <summary>
    /// Regime-routing diagnostics (spec §9.3). All null when regime routing is
    /// disabled, so disabled behaviour stays byte-identical to before this feature.
    /// </summary>
    public MarketRegime? RegimeLabel { get; init; }
    public decimal? RegimeConfidence { get; init; }
    public string? RegimePolicyId { get; init; }
    public string? RegimeEntryProfileId { get; init; }
    public string? RegimeManagementProfileId { get; init; }
    public decimal? RegimeRiskMultiplier { get; init; }

    /// <summary>
    /// Equity high-watermark protection diagnostics (spec §18). Null when the feature
    /// is disabled, so disabled behaviour stays byte-identical to before this feature.
    /// </summary>
    public decimal? EquityProtectionRiskMultiplier { get; init; }
    public string? EquityProtectionActivatedTierIds { get; init; }
    public decimal? TradingConditionRiskMultiplier { get; init; }
    public string? TradingConditionReasonCode { get; init; }
    public decimal? SpreadAtr { get; init; }
    public decimal? AtrPercentile { get; init; }
    public decimal? CorrelationRiskMultiplier { get; init; }
    public decimal? StrategyAllocationRiskMultiplier { get; init; }
    public decimal? SetupCalibrationRiskMultiplier { get; init; }
    public decimal? MetaLabelRiskMultiplier { get; init; }
    public decimal? MetaLabelProbability { get; init; }
    public string? MetaLabelModelVersion { get; init; }
    public string? MetaLabelReasonCode { get; init; }
    public string? PortfolioReservationId { get; init; }
    public string? RiskClusterId { get; init; }
    public decimal? PortfolioOriginalQuantity { get; init; }
    public decimal? PortfolioAllocatedQuantity { get; init; }
    public decimal? FinalRiskBudgetMultiplier { get; init; }
    /// <summary>
    /// True only when a central portfolio coordinator has already performed monetary sizing,
    /// ranked the opportunity, and reserved the exact quantity. Execution must still repeat
    /// broker/account safety and hard pre-trade risk checks, but must not size the quantity a
    /// second time or reapply risk multipliers.
    /// </summary>
    public bool QuantityIsPortfolioApproved { get; init; }

    /// <summary>
    /// Optional, independent value-location evidence (spec §13.3). Empty when the
    /// feature is disabled or no anchor with a distance was available, so disabled
    /// behaviour stays byte-identical to before this feature.
    /// </summary>
    public IReadOnlyList<string> ValueLocationEvidenceReasonCodes { get; init; } = [];
    public decimal? ValueLocationDistanceAtr { get; init; }
    public IReadOnlyList<string> TrendQualityReasonCodes { get; init; } = [];
    public IReadOnlyList<string> CurrencyStrengthReasonCodes { get; init; } = [];
    public decimal? CurrencyStrengthDifferential { get; init; }

    /// <summary>Optional causal wave-structure evidence. Null/empty when disabled or unavailable.</summary>
    public NeoWavePatternType? NeoWavePatternType { get; init; }
    public NeoWaveDirection? NeoWaveDirection { get; init; }
    public string? NeoWaveHypothesisId { get; init; }
    public decimal? NeoWaveStructuralScore { get; init; }
    public decimal? NeoWaveMaturity { get; init; }
    public decimal? NeoWaveConflictScore { get; init; }
    public decimal? NeoWaveInvalidationPrice { get; init; }
    public decimal? NeoWaveInvalidationDistanceAtr { get; init; }
    public IReadOnlyList<string> NeoWaveReasonCodes { get; init; } = [];
    /// <summary>Bounded to 0..1; NEoWave evidence can reduce but never increase risk.</summary>
    public decimal? NeoWaveRiskMultiplier { get; init; }

    public Guid? SupplyDemandZoneId { get; init; }
    public SupplyDemandZoneType? SupplyDemandZoneType { get; init; }
    public SupplyDemandZoneState? SupplyDemandZoneState { get; init; }
    public decimal? SupplyDemandZoneQuality { get; init; }
    public decimal? SupplyDemandZoneDistanceAtr { get; init; }
    public string? SupplyDemandProfileHash { get; init; }
    public IReadOnlyList<string> SupplyDemandReasonCodes { get; init; } = [];
    public decimal? SupplyDemandRiskMultiplier { get; init; }

    public Guid? LiquidityPoolId { get; init; }
    public LiquidityPoolType? LiquidityPoolType { get; init; }
    public LiquidityPoolState? LiquidityPoolState { get; init; }
    public LiquiditySide? LiquiditySide { get; init; }
    public decimal? LiquidityPoolQuality { get; init; }
    public decimal? LiquidityPoolDistanceAtr { get; init; }
    public Guid? LiquiditySweepId { get; init; }
    public Guid? SupplyDemandLiquidityConfluenceId { get; init; }
    public string? LiquidityProfileHash { get; init; }
    public IReadOnlyList<string> LiquidityReasonCodes { get; init; } = [];
    public decimal? LiquidityEvidenceRiskMultiplier { get; init; }
    /// <summary>Combined structural evidence multiplier, always capped to 0..1.</summary>
    public decimal? StructuralEvidenceRiskMultiplier { get; init; }

    // Immutable entry-time thesis. Trade managers may observe newer analysis, but must not
    // silently replace these references.
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
}

public sealed class MultiTimeframeAnalysis
{
    private readonly IReadOnlyDictionary<BarInterval, AnalysisSnapshot> _timeframes;

    public MultiTimeframeAnalysis(
        InstrumentKey instrument,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> timeframes)
    {
        Instrument = instrument;
        Timestamp = timestamp;
        _timeframes = timeframes ?? throw new ArgumentNullException(nameof(timeframes));
    }

    public InstrumentKey Instrument { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Timeframes => _timeframes;

    public AnalysisSnapshot Get(BarInterval interval) =>
        _timeframes.TryGetValue(interval, out AnalysisSnapshot? snapshot)
            ? snapshot
            : throw new KeyNotFoundException($"No analysis is available for {interval}.");

    public bool TryGet(BarInterval interval, out AnalysisSnapshot snapshot) =>
        _timeframes.TryGetValue(interval, out snapshot!);
}

public sealed record AgentMarketContext
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required MultiTimeframeAnalysis Analysis { get; init; }
    public required AccountSnapshot Account { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    public required IReadOnlyList<BrokerOrder> OpenOrders { get; init; }
    public decimal? ExecutableSpread { get; init; }
    /// <summary>
    /// Estimated full round-trip transaction cost (spread + slippage + commission, both entry
    /// and exit legs) in the instrument's own price units. Unlike <see cref="ExecutableSpread"/>
    /// (spread only, used for entry-price modelling), this is meant for cost-aware risk gates -
    /// a stop this tight would already lose a large fraction of its planned R to costs alone,
    /// regardless of which market it's trading, since it scales with that market's own spread/
    /// slippage/commission rather than a single flat multiple.
    /// </summary>
    public decimal? RoundTripCostEstimate { get; init; }
    public DateTimeOffset? MarketDataAvailableAt { get; init; }
    public string? StrategyId { get; init; }
    public CurrencyStrengthSnapshot? CurrencyStrength { get; init; }
}
