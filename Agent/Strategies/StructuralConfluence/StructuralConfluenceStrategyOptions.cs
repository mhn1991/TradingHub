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
    public int MaximumBarsSinceAcceptedBreak { get; init; } = 18;
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

    public void Validate()
    {
        if (MinimumPoolQuality is < 0m or > 1m || MaximumBarsSinceAcceptedBreak < 1 ||
            MinimumAcceptanceCloses < 1 || MinimumDisplacementAtr < 0m || MaximumRetestDistanceAtr < 0m ||
            MinimumAdx < 0m || MinimumEfficiencyRatio is < 0m or > 1m ||
            MinimumConfidence is < 0m or > 100m || !Enum.IsDefined(CciMode) || !Enum.IsDefined(ExpansionMode))
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
    public StructuralContextOptions Context { get; init; } = new();
    public StructuralTriggerOptions Trigger { get; init; } = new();
    public StructuralConfirmationOptions Confirmation { get; init; } = new();
    public StructuralArbitrationOptions Arbitration { get; init; } = new();
    public StructuralGeometryOptions Geometry { get; init; } = new();
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
        if (!LiquiditySweepReversal.Enabled && !SupplyDemandPullback.Enabled && !LiquidityBreakRetest.Enabled)
            throw new ArgumentException("At least one structural playbook must be enabled.");
    }
}
