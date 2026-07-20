using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;

namespace Agent.Strategies.StructuralConfluence;

public enum StructuralConfirmationMode
{
    Disabled,
    Soft,
    Required
}

public enum StructuralConfluenceRequirement
{
    Disabled,
    Preferred,
    Required
}

public sealed record LiquiditySweepReversalOptions
{
    public bool Enabled { get; init; } = true;
    public decimal MinimumPoolQuality { get; init; } = 0.55m;
    public IReadOnlyList<LiquidityPoolType> AllowedPoolTypes { get; init; } =
        [LiquidityPoolType.EqualHighs, LiquidityPoolType.EqualLows, LiquidityPoolType.SwingHigh,
         LiquidityPoolType.SwingLow, LiquidityPoolType.RangeHigh, LiquidityPoolType.RangeLow];
    public decimal MinimumSweepPenetrationAtr { get; init; } = 0.05m;
    public decimal MaximumSweepPenetrationAtr { get; init; } = 1.25m;
    public bool RequireClosedBackInside { get; init; } = true;
    public int MaximumBarsSinceSweep { get; init; } = 12;
    public StructuralConfluenceRequirement SupplyDemandConfluence { get; init; } = StructuralConfluenceRequirement.Preferred;
    public decimal MaximumZonePoolDistanceAtr { get; init; } = 0.5m;
    public decimal MinimumReclaimBodyRatio { get; init; } = 0.25m;
    public bool RequirePriceActionTrigger { get; init; } = true;
    public bool RequireMicroStructureBreak { get; init; }
    public StructuralConfirmationMode CciMode { get; init; } = StructuralConfirmationMode.Soft;
    public decimal MinimumConfidence { get; init; } = 55m;

    public void Validate()
    {
        if (MinimumPoolQuality is < 0m or > 1m || AllowedPoolTypes is null || AllowedPoolTypes.Count == 0 ||
            AllowedPoolTypes.Distinct().Count() != AllowedPoolTypes.Count ||
            MinimumSweepPenetrationAtr < 0m || MaximumSweepPenetrationAtr < MinimumSweepPenetrationAtr ||
            MaximumBarsSinceSweep < 1 || MaximumZonePoolDistanceAtr < 0m ||
            MinimumReclaimBodyRatio is < 0m or > 1m || MinimumConfidence is < 0m or > 100m ||
            !Enum.IsDefined(SupplyDemandConfluence) || !Enum.IsDefined(CciMode))
            throw new ArgumentException("Liquidity sweep reversal options are invalid.");
    }
}

public sealed record SupplyDemandPullbackOptions
{
    public bool Enabled { get; init; } = true;
    public decimal MinimumZoneQuality { get; init; } = 0.55m;
    public IReadOnlyList<SupplyDemandZoneState> PermittedZoneStates { get; init; } =
        [SupplyDemandZoneState.ConfirmedFresh, SupplyDemandZoneState.Approached, SupplyDemandZoneState.Tested];
    public int MaximumPriorTouches { get; init; } = 1;
    public decimal MaximumPenetrationRatio { get; init; } = 0.65m;
    public bool RequireTrendAlignment { get; init; } = true;
    public bool AllowRangeBoundaryContext { get; init; }
    public bool RequirePriceActionTrigger { get; init; } = true;
    public StructuralConfirmationMode CciMode { get; init; } = StructuralConfirmationMode.Soft;
    public bool AllowRsiAlternativeConfirmation { get; init; } = true;
    public bool AllowBollingerReEntryConfirmation { get; init; } = true;
    public decimal MinimumConfidence { get; init; } = 55m;

    public void Validate()
    {
        if (MinimumZoneQuality is < 0m or > 1m || PermittedZoneStates is null || PermittedZoneStates.Count == 0 ||
            PermittedZoneStates.Distinct().Count() != PermittedZoneStates.Count ||
            MaximumPriorTouches < 0 || MaximumPenetrationRatio is < 0m or > 1m ||
            MinimumConfidence is < 0m or > 100m || !Enum.IsDefined(CciMode))
            throw new ArgumentException("Supply/demand pullback options are invalid.");
    }
}

public sealed record LiquidityBreakRetestOptions
{
    public bool Enabled { get; init; } = true;
    public decimal MinimumPoolQuality { get; init; } = 0.55m;
    /// <summary>
    /// Measured in evidence.Setup.Interval bars (30m by default) to match the granularity of the
    /// underlying LiquidityAnalyzer, which runs on the setup timeframe, not the trigger timeframe
    /// - the old default (18) was tuned assuming trigger-interval bars, understating this window
    /// 6x (90 minutes instead of the intended ~9 hours) and expiring every accepted break before a
    /// retest could realistically occur. Matches LiquidityCalculationProfile's own default
    /// MaximumBarsToTrackAcceptedBreak (20) so the playbook doesn't cut candidates off earlier
    /// than the analyzer itself keeps tracking them.
    /// </summary>
    public int MaximumBarsSinceAcceptedBreak { get; init; } = 20;
    public int MinimumAcceptanceCloses { get; init; } = 1;
    public decimal MinimumDisplacementAtr { get; init; } = 0.5m;
    public decimal MaximumRetestDistanceAtr { get; init; } = 0.35m;
    public bool RequireRetestEvent { get; init; } = true;
    public bool RequirePriceActionTrigger { get; init; } = true;
    public StructuralConfirmationMode CciMode { get; init; } = StructuralConfirmationMode.Soft;
    public StructuralConfirmationMode ExpansionMode { get; init; } = StructuralConfirmationMode.Soft;
    public decimal MinimumAdx { get; init; } = 18m;
    public decimal MinimumEfficiencyRatio { get; init; } = 0.35m;
    public decimal MinimumConfidence { get; init; } = 55m;

    /// <summary>
    /// A pool with at least this many cooldown-throttled distinct touches (see
    /// LiquidityPool.DistinctTouchCount) exempts the PoolQuality gate: a confirmed second test is
    /// itself validating evidence, and the shared QualityScore's freshness penalty otherwise
    /// fights the very touches a break-retest setup requires by definition (break + retest = at
    /// least 2). QualityScore itself is untouched - this only changes how the gate reads it.
    /// </summary>
    public int MinimumDistinctTouchesForQualityExemption { get; init; } = 2;

    public void Validate()
    {
        if (MinimumPoolQuality is < 0m or > 1m || MaximumBarsSinceAcceptedBreak < 1 ||
            MinimumAcceptanceCloses < 1 || MinimumDisplacementAtr < 0m || MaximumRetestDistanceAtr < 0m ||
            MinimumAdx is < 0m or > 100m || MinimumEfficiencyRatio is < 0m or > 1m ||
            MinimumConfidence is < 0m or > 100m || !Enum.IsDefined(CciMode) || !Enum.IsDefined(ExpansionMode) ||
            MinimumDistinctTouchesForQualityExemption < 1)
            throw new ArgumentException("Liquidity break/retest options are invalid.");
    }
}

public sealed record StructuralContextOptions
{
    public bool StrongOppositionVeto { get; init; } = true;
    public decimal MinimumContextConfidence { get; init; } = 40m;
}

public sealed record StructuralTriggerOptions
{
    public decimal MinimumPriceActionConfidence { get; init; } = 50m;
}

public sealed record StructuralConfirmationOptions
{
    public decimal SoftAlignedAdjustment { get; init; } = 6m;
    public decimal SoftConflictAdjustment { get; init; } = -8m;
}

public sealed record StructuralArbitrationOptions
{
    public decimal SameDirectionConfluenceAdjustment { get; init; } = 3m;
}

public sealed record StructuralGeometryOptions
{
    public decimal MaximumStopDistanceAtr { get; init; } = 4m;
    public decimal MinimumObstacleDistanceAtr { get; init; } = 0.15m;
}

/// <summary>
/// Configuration for the tiered target-map builder and adaptive exit routing introduced by the
/// Structural Indicator and Adaptive Target Management Plan. Disabled by default so every
/// existing structural-confluence-v1 profile keeps its exact nearest-obstacle bracket behavior;
/// the v2 profile is the only caller that turns this on.
/// </summary>
public sealed record AdaptiveTargetManagementOptions
{
    public bool Enabled { get; init; }

    /// <summary>Minimum conservative terminal opportunity for PartialThenRunner admission (plan §3.8).</summary>
    public decimal ManagedOpportunityMinimumR { get; init; } = 2m;
    /// <summary>Minimum weighted planned reward for PartialThenRunner admission (plan §3.8).</summary>
    public decimal MinimumWeightedPlannedRewardR { get; init; } = 1.5m;
    /// <summary>Minimum synthetic expansion projection for ManagedExpansion admission (plan §3.8).</summary>
    public decimal ManagedExpansionProjectionMinimumR { get; init; } = 2m;

    /// <summary>Candidate execution-price clustering distance (plan §3.4, §9).</summary>
    public decimal TargetClusterDistanceAtr { get; init; } = 0.25m;
    /// <summary>Maximum ranked candidates retained after ranking (plan §3.3).</summary>
    public int MaximumPersistedCandidates { get; init; } = 8;
    /// <summary>
    /// Terminal-role eligibility distance cap, in entry/trigger-timeframe ATR. Ranking prefers
    /// coarser-timeframe candidates within the same tier (see TargetMapBuilder.ClusterAndRank),
    /// which without this cap lets a higher-timeframe zone many ATR away win as the terminal
    /// target for a much finer entry timeframe - a real, observed failure mode (a 2H zone ~26 ATR
    /// away selected as the target for a 5-minute-entry trade). Candidates beyond this distance
    /// are excluded from Terminal eligibility regardless of tier/quality.
    /// </summary>
    public decimal MaximumTerminalDistanceAtr { get; init; } = 8m;

    /// <summary>Tier A opposing-zone quality floor (plan §3.3).</summary>
    public decimal TierAMinimumZoneQuality { get; init; } = 0.55m;
    /// <summary>Tier A maximum permitted prior touches on an opposing zone (plan §3.3).</summary>
    public int TierAMaximumPriorTouches { get; init; } = 1;
    /// <summary>Tier A liquidity-pool quality floor (plan §3.3).</summary>
    public decimal TierAMinimumPoolQuality { get; init; } = 0.65m;
    /// <summary>Tier A liquidity-pool prominence floor (plan §3.3).</summary>
    public decimal TierAMinimumPoolProminence { get; init; } = 0.60m;
    /// <summary>
    /// Setup-timeframe "sufficient quality but lower prominence" floor for a Tier B liquidity
    /// pool (plan §3.3 describes this qualitatively without a number; this is the chosen floor,
    /// deliberately below <see cref="TierAMinimumPoolQuality"/>).
    /// </summary>
    public decimal TierBMinimumPoolQuality { get; init; } = 0.40m;
    /// <summary>Setup-timeframe Tier B opposing-zone quality floor (plan §3.3, same interpretation as <see cref="TierBMinimumPoolQuality"/>).</summary>
    public decimal TierBMinimumZoneQuality { get; init; } = 0.35m;

    /// <summary>Default one-time partial-exit fraction of original quantity (plan §4.4, §9).</summary>
    public decimal DefaultPartialFraction { get; init; } = 0.25m;
    /// <summary>Minimum runner fraction that must always remain after planned scale-outs (plan §9).</summary>
    public decimal MinimumRunnerFraction { get; init; } = 0.50m;
    /// <summary>Minimum R at or beyond which a structural checkpoint qualifies for the partial (plan §4.4).</summary>
    public decimal CheckpointMinimumR { get; init; } = 1.25m;
    /// <summary>Synthetic partial-checkpoint R used only for PartialThenRunner when no structural checkpoint exists (plan §4.4).</summary>
    public decimal SyntheticPartialCheckpointR { get; init; } = 1.5m;

    public void Validate()
    {
        if (ManagedOpportunityMinimumR <= 0m || MinimumWeightedPlannedRewardR <= 0m ||
            ManagedExpansionProjectionMinimumR <= 0m || TargetClusterDistanceAtr < 0m ||
            MaximumPersistedCandidates < 1 ||
            TierAMinimumZoneQuality is < 0m or > 1m || TierAMaximumPriorTouches < 0 ||
            TierAMinimumPoolQuality is < 0m or > 1m || TierAMinimumPoolProminence is < 0m or > 1m ||
            TierBMinimumPoolQuality is < 0m or > 1m || TierBMinimumZoneQuality is < 0m or > 1m ||
            MaximumTerminalDistanceAtr <= 0m ||
            DefaultPartialFraction is <= 0m or >= 1m ||
            MinimumRunnerFraction is <= 0m or >= 1m ||
            DefaultPartialFraction + MinimumRunnerFraction > 1m ||
            CheckpointMinimumR <= 0m || SyntheticPartialCheckpointR <= 0m)
            throw new ArgumentException("Adaptive target management options are invalid.");
    }
}

/// <summary>
/// Trend + momentum + volatility indicator confluence, deliberately one indicator per category
/// (never two answering the same question - e.g. RSI and CCI together would just be two momentum
/// oscillators agreeing with each other, not independent confirmation): ADX/DMI for trend
/// presence+direction, RSI for momentum timing, Bollinger Bands for volatility context only (not
/// as a trend signal). Has no natural structural anchor (no zone/pool/level), so unlike the other
/// three playbooks its stop/target is a plain ATR multiple rather than snapped to nearby
/// structure.
/// </summary>
public sealed record IndicatorConfluenceOptions
{
    public bool Enabled { get; init; }

    // Trend (ADX/DMI): is there a trend, and which way.
    public decimal MinimumAdx { get; init; } = 20m;
    public bool RequireTrendStrengthening { get; init; }

    // Momentum (RSI): is momentum turning in our favor right now, not already exhausted.
    public decimal MaximumRsiForBuy { get; init; } = 70m;
    public decimal MinimumRsiForSell { get; init; } = 30m;

    // Volatility (Bollinger): is there enough room to move. RequireSqueezeBreakout demands the
    // stricter "just expanding out of a squeeze" timing instead of merely "not squeezed".
    public bool RequireSqueezeBreakout { get; init; }

    public decimal MinimumConfidence { get; init; } = 55m;

    // Plain ATR-multiple stop/target (no structural anchor exists for this playbook).
    public decimal StopAtr { get; init; } = 1.5m;
    public decimal TargetAtr { get; init; } = 3.0m;

    public void Validate()
    {
        if (MinimumAdx is < 0m or > 100m || MaximumRsiForBuy is < 0m or > 100m ||
            MinimumRsiForSell is < 0m or > 100m || MinimumRsiForSell >= MaximumRsiForBuy ||
            MinimumConfidence is < 0m or > 100m || StopAtr <= 0m || TargetAtr <= 0m)
            throw new ArgumentException("Indicator confluence options are invalid.");
    }
}

public sealed record StructuralConfluenceStrategyOptions
{
    public BarInterval ContextInterval { get; init; } = BarInterval.Hours(1);
    public IReadOnlyList<BarInterval> AdditionalContextIntervals { get; init; } = [];
    public BarInterval SetupInterval { get; init; } = BarInterval.Minutes(15);
    public BarInterval TriggerInterval { get; init; } = BarInterval.Minutes(5);
    public decimal Quantity { get; init; } = 1_000m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public decimal StopBufferAtr { get; init; } = 0.20m;
    public decimal TargetBufferAtr { get; init; } = 0.10m;
    public int MaximumTriggerBars { get; init; } = 12;
    public int MaximumArmedSetupBars { get; init; } = 24;
    public LiquiditySweepReversalOptions LiquiditySweepReversal { get; init; } = new();
    public SupplyDemandPullbackOptions SupplyDemandPullback { get; init; } = new();
    public LiquidityBreakRetestOptions LiquidityBreakRetest { get; init; } = new();
    public IndicatorConfluenceOptions IndicatorConfluence { get; init; } = new();
    public StructuralContextOptions Context { get; init; } = new();
    public StructuralTriggerOptions Trigger { get; init; } = new();
    public StructuralConfirmationOptions Confirmation { get; init; } = new();
    public StructuralArbitrationOptions Arbitration { get; init; } = new();
    public StructuralGeometryOptions Geometry { get; init; } = new();
    public AdaptiveTargetManagementOptions AdaptiveTargetManagement { get; init; } = new();
    public string StrategyVersion { get; init; } = "structural-confluence-v1";

    public IReadOnlySet<BarInterval> RequiredIntervals => new HashSet<BarInterval>(
        [TriggerInterval, SetupInterval, ContextInterval, .. AdditionalContextIntervals]);

    public void Validate()
    {
        if (!ContextInterval.IsValid || !SetupInterval.IsValid || !TriggerInterval.IsValid ||
            AdditionalContextIntervals is null || AdditionalContextIntervals.Any(item => !item.IsValid))
            throw new ArgumentException("All structural-confluence intervals must be valid.");
        if (BarIntervalParser.CompareDuration(TriggerInterval, SetupInterval) >= 0 ||
            BarIntervalParser.CompareDuration(SetupInterval, ContextInterval) >= 0)
            throw new ArgumentException("Structural intervals must satisfy TriggerInterval < SetupInterval < ContextInterval.");
        if (AdditionalContextIntervals.Distinct().Count() != AdditionalContextIntervals.Count ||
            AdditionalContextIntervals.Contains(ContextInterval) || AdditionalContextIntervals.Contains(SetupInterval) ||
            AdditionalContextIntervals.Contains(TriggerInterval) ||
            AdditionalContextIntervals.Any(item => BarIntervalParser.CompareDuration(item, SetupInterval) < 0))
            throw new ArgumentException("Additional context intervals must be unique and no finer than setup.");
        if (Quantity <= 0m || MinimumRewardRisk <= 0m || StopBufferAtr < 0m || TargetBufferAtr < 0m ||
            MaximumTriggerBars < 1 || MaximumArmedSetupBars < 1 || string.IsNullOrWhiteSpace(StrategyVersion) ||
            Context.MinimumContextConfidence is < 0m or > 100m ||
            Trigger.MinimumPriceActionConfidence is < 0m or > 100m ||
            Confirmation.SoftAlignedAdjustment is < 0m or > 10m ||
            Confirmation.SoftConflictAdjustment is < -10m or > 0m ||
            Arbitration.SameDirectionConfluenceAdjustment is < 0m or > 8m ||
            Geometry.MaximumStopDistanceAtr <= 0m || Geometry.MinimumObstacleDistanceAtr < 0m)
            throw new ArgumentException("Structural-confluence strategy options are invalid.");

        LiquiditySweepReversal.Validate();
        SupplyDemandPullback.Validate();
        LiquidityBreakRetest.Validate();
        IndicatorConfluence.Validate();
        if (!LiquiditySweepReversal.Enabled && !SupplyDemandPullback.Enabled &&
            !LiquidityBreakRetest.Enabled && !IndicatorConfluence.Enabled)
            throw new ArgumentException("At least one structural playbook must be enabled.");

        AdaptiveTargetManagement.Validate();
        if (AdaptiveTargetManagement.Enabled && string.Equals(StrategyVersion, "structural-confluence-v1", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Adaptive target management changes decision behavior and must not run under the v1 strategy version.");
        }
    }
}
