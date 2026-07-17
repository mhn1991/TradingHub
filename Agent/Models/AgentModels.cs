using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Agent.Models;

public enum AgentAction
{
    Observe,
    Buy,
    Sell,
    Close,
    Cancel
}

public sealed record AgentDecision
{
    public string? DecisionId { get; init; }
    /// <summary>Optional caller-owned idempotency key for broker submission.</summary>
    public string? ClientOrderId { get; init; }
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
    public DateTimeOffset? MarketDataAvailableAt { get; init; }
    public string? StrategyId { get; init; }
    public CurrencyStrengthSnapshot? CurrencyStrength { get; init; }
}
