using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;

namespace TradeManager;

public enum TradeManagementAction
{
    Hold,
    MoveStop,
    ReducePosition,
    ReduceAndMoveStop,
    Exit
}

public enum TradeManagementEvaluationScope
{
    Combined,
    Mechanical,
    FastStructure,
    MainStructure,
    Thesis
}

public enum TradeManagementExitReason
{
    None,
    AdverseStructure,
    ProfitFloorBreached,
    MaximumGivebackBreached,
    EquityProtectionBreach,
    NeoWaveInvalidation,
    EntrySupplyDemandZoneInvalidated,
    TargetLiquidityAcceptedBreak
}

/// <summary>
/// The two position-level equity-protection actions this manager can act on
/// (PauseNewEntries/ReduceFutureRisk have no position visibility and are enforced
/// entirely inside the account/strategy safety controller, one layer up).
/// </summary>
public enum EquityProtectionPositionAction
{
    ReduceOpenPositions,
    FlattenAllPositions
}

/// <summary>
/// An account/strategy-safety-driven override, evaluated ahead of every other rule.
/// Deliberately decoupled from RiskManager's richer equity-protection tier type so
/// TradeManager stays free of a RiskManager project reference; the caller translates.
/// </summary>
public sealed record EquityProtectionDirective
{
    public required string TierId { get; init; }
    public required EquityProtectionPositionAction Action { get; init; }
    public decimal ReductionFraction { get; init; }
}

public enum TrailingStopMode
{
    Disabled,
    BreakEvenOnly,
    StructureAtr
}

public enum PositionReductionReason
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
    RegimeDegradation
}

public enum ScaleOutTriggerMode
{
    RThreshold,
    OpposingStructure,
    RThresholdOrOpposingStructure
}

public sealed record ScaleOutRule
{
    public required string StageId { get; init; }
    public decimal ActivationR { get; init; }
    public decimal MinimumOpenProfitR { get; init; }
    public decimal FractionOfInitialQuantity { get; init; }
    public ScaleOutTriggerMode TriggerMode { get; init; } = ScaleOutTriggerMode.RThreshold;
}

public sealed record ProfitFloorRule
{
    public decimal ActivationR { get; init; }
    public decimal LockedProfitR { get; init; }
}

public sealed record ProfitGivebackRule
{
    public decimal ActivationR { get; init; }
    public decimal MaximumGivebackR { get; init; }
}

public record PositionManagementOptions
{
    public TrailingStopMode Mode { get; init; } = TrailingStopMode.StructureAtr;
    /// <summary>Legacy alias for MainStructureInterval.</summary>
    public BarInterval? ManagementInterval { get; init; }
    public bool EvaluateMechanicalProtectionOnEveryExecutionFrame { get; init; } = true;
    public BarInterval? FastStructureInterval { get; init; }
    public BarInterval? MainStructureInterval { get; init; }
    public BarInterval? ThesisInterval { get; init; }
    public decimal BreakEvenActivationR { get; init; } = 1m;
    public decimal StructureTrailActivationR { get; init; } = 1.5m;
    public decimal AtrBufferMultiplier { get; init; } = 0.25m;
    public decimal BreakEvenBufferAtr { get; init; } = 0.05m;
    public decimal MinimumStopImprovementAtr { get; init; } = 0.05m;
    public decimal MinimumStopImprovementTicks { get; init; } = 1m;
    public int MinimumAnalysisBarsBetweenAmendments { get; init; } = 1;
    public bool ExitOnAdverseStructureBreak { get; init; }

    /// <summary>
    /// When enabled, an open position exits after price crosses the NEoWave invalidation
    /// level captured at entry. The rule never follows a newly reinterpreted wave count:
    /// the hypothesis and invalidation level are pinned to the position at entry.
    /// Disabled by default until shadow attribution demonstrates value.
    /// </summary>
    public bool EnableNeoWaveInvalidationExit { get; init; }

    /// <summary>
    /// Optional ATR-normalised confirmation buffer beyond the entry-pinned invalidation
    /// price. Zero means the executable price only needs to cross the invalidation level.
    /// </summary>
    public decimal NeoWaveInvalidationBufferAtr { get; init; } = 0.10m;

    /// <summary>
    /// Opt-in, entry-pinned supply/demand thesis management. Record-only analysis remains the
    /// default and cannot change an existing position while this is false.
    /// </summary>
    public bool SupplyDemandManagementEnabled { get; init; }

    /// <summary>
    /// Opt-in, entry-pinned inferred-liquidity thesis management. Disabled by default pending
    /// held-out outcome attribution.
    /// </summary>
    public bool LiquidityManagementEnabled { get; init; }

    /// <summary>Must match the revision pinned to the position at entry.</summary>
    public string StructuralManagementPolicyRevision { get; init; } = "structural-management-v1";

    public bool PreserveBracketTarget { get; init; } = true;
    public bool IncludeEstimatedExitCostsAtBreakEven { get; init; } = true;

    public bool EnableScaleOut { get; init; }
    public IReadOnlyList<ScaleOutRule> ScaleOutRules { get; init; } = [];
    public decimal MinimumRunnerFraction { get; init; } = 0.4m;
    public decimal OpposingStructureProximityAtr { get; init; } = 0.35m;
    public int MinimumAnalysisBarsBetweenReductions { get; init; } = 1;

    /// <summary>
    /// Structural trail levels closer than this (in ATR) are treated as micro-noise.
    /// </summary>
    public decimal MinimumStructuralTrailDistanceAtr { get; init; } = 0.35m;

    /// <summary>
    /// Structural trail levels farther than this (in ATR) are ignored in favour of
    /// nearer meaningful structure or mechanical protection.
    /// </summary>
    public decimal MaximumStructuralTrailDistanceAtr { get; init; } = 2.50m;

    /// <summary>Minimum price-zone strength required for trail / opposing-structure use.</summary>
    public decimal MinimumZoneStrengthForManagement { get; init; } = 40m;

    /// <summary>Minimum channel confidence required for trail / opposing-structure use.</summary>
    public decimal MinimumChannelConfidenceForManagement { get; init; } = 40m;

    /// <summary>
    /// When true, prefer structural levels whose pivot/zone activity is at or after entry.
    /// </summary>
    public bool PreferPostEntryStructure { get; init; } = true;

    /// <summary>
    /// When true, R-threshold scale-outs fire only on fast/main management bars, not on
    /// pure mechanical execution frames.
    /// </summary>
    public bool ScaleOutRThresholdRequiresManagementBar { get; init; } = true;

    /// <summary>
    /// When true, profit-floor / giveback breaches prefer advancing the stop first and
    /// only hard-exit when the floor stop is already in place or can no longer be placed.
    /// </summary>
    public bool PreferFloorStopBeforeHardExit { get; init; } = true;

    public bool EnableProfitFloor { get; init; }
    public IReadOnlyList<ProfitFloorRule> ProfitFloorRules { get; init; } = [];
    public bool EnableMaximumGiveback { get; init; }
    public IReadOnlyList<ProfitGivebackRule> MaximumGivebackRules { get; init; } = [];

    public bool EnableStagnationReduction { get; init; }
    public decimal StagnationMinimumOpenProfitR { get; init; } = 0.75m;
    public int StagnationBars { get; init; } = 12;
    public decimal StagnationMinimumMfeAdvanceR { get; init; } = 0.10m;
    public decimal StagnationReductionFraction { get; init; } = 0.15m;

    /// <summary>
    /// When true, the stagnation-bars threshold is interpolated by
    /// <see cref="ManagedTradeState.EntryConfidence"/> instead of using the fixed
    /// <see cref="StagnationBars"/> value: low-confidence entries are given less patience
    /// (reduced sooner), high-confidence entries more (held longer before reducing).
    /// </summary>
    public bool EnableConfidenceScaledStagnation { get; init; }
    public int MinimumStagnationBarsAtLowConfidence { get; init; } = 8;
    public int MaximumStagnationBarsAtHighConfidence { get; init; } = 20;

    public bool EnableStructuralDeteriorationReduction { get; init; }
    public decimal StructuralDeteriorationReductionFraction { get; init; } = 0.20m;
    public int MaximumStructuralDeteriorationReductions { get; init; } = 1;

    public bool EnableMomentumDecayReduction { get; init; }
    public decimal MomentumDecayMinimumOpenProfitR { get; init; } = 1.25m;
    public decimal MomentumDecayReductionFraction { get; init; } = 0.15m;
    public int MaximumMomentumDecayReductions { get; init; } = 1;

    public bool EnableVolatilityExhaustionReduction { get; init; }
    public decimal VolatilityExhaustionMinimumOpenProfitR { get; init; } = 1.50m;
    public decimal VolatilityExhaustionReductionFraction { get; init; } = 0.15m;
    public int MaximumVolatilityExhaustionReductions { get; init; } = 1;

    /// <summary>
    /// Protective reduction when the position's current-timeframe market regime
    /// degrades to a not-tradeable state (HighVolatilityDisorder/IlliquidUnsafe).
    /// Cannot fire while regime classification is disabled, since
    /// AnalysisSnapshot.MarketRegime then stays MarketRegimeSnapshot.Unknown
    /// (IsTradeable == true), so this is safe to default-enable.
    /// </summary>
    public bool EnableRegimeDegradationReduction { get; init; }
    public decimal RegimeDegradationMinimumOpenProfitR { get; init; } = 1.0m;
    public decimal RegimeDegradationReductionFraction { get; init; } = 0.25m;
    public int MaximumRegimeDegradationReductions { get; init; } = 1;

    /// <summary>
    /// Optional broker/session risk window in UTC. Both values must be supplied together.
    /// The window may cross midnight (for example 21:45 through 22:15 UTC).
    /// </summary>
    public bool EnableRiskWindowReduction { get; init; }
    public TimeOnly? RiskWindowStartUtc { get; init; }
    public TimeOnly? RiskWindowEndUtc { get; init; }
    public decimal RiskWindowMinimumOpenProfitR { get; init; } = 0.50m;
    public decimal RiskWindowReductionFraction { get; init; } = 0.25m;

    public bool EnableExecutionCostStressReduction { get; init; }
    public decimal MaximumSpreadToAtrRatio { get; init; } = 0.20m;
    public decimal ExecutionCostStressMinimumOpenProfitR { get; init; } = 0.50m;
    public decimal ExecutionCostStressReductionFraction { get; init; } = 0.15m;

    public static PositionManagementOptions LegacyDefaults { get; } = new()
    {
        Mode = TrailingStopMode.StructureAtr,
        EvaluateMechanicalProtectionOnEveryExecutionFrame = true,
        FastStructureInterval = BarInterval.Minutes(5),
        MainStructureInterval = BarInterval.Minutes(15),
        ThesisInterval = BarInterval.Hours(1),
        BreakEvenActivationR = 1m,
        StructureTrailActivationR = 1.5m,
        AtrBufferMultiplier = 0.25m,
        PreserveBracketTarget = false,
        EnableScaleOut = true,
        ScaleOutRules =
        [
            new ScaleOutRule
            {
                StageId = "scale-1r",
                ActivationR = 1m,
                MinimumOpenProfitR = 1m,
                FractionOfInitialQuantity = 0.20m,
                TriggerMode = ScaleOutTriggerMode.RThreshold
            },
            new ScaleOutRule
            {
                StageId = "scale-1.5r-or-structure",
                ActivationR = 1.5m,
                MinimumOpenProfitR = 1m,
                FractionOfInitialQuantity = 0.20m,
                TriggerMode = ScaleOutTriggerMode.RThresholdOrOpposingStructure
            }
        ],
        MinimumRunnerFraction = 0.40m,
        EnableProfitFloor = true,
        ProfitFloorRules =
        [
            new ProfitFloorRule { ActivationR = 1m, LockedProfitR = 0m },
            new ProfitFloorRule { ActivationR = 1.5m, LockedProfitR = 0.25m },
            new ProfitFloorRule { ActivationR = 2m, LockedProfitR = 0.75m },
            new ProfitFloorRule { ActivationR = 3m, LockedProfitR = 2m }
        ],
        EnableMaximumGiveback = true,
        MaximumGivebackRules =
        [
            new ProfitGivebackRule { ActivationR = 2m, MaximumGivebackR = 0.75m },
            new ProfitGivebackRule { ActivationR = 3m, MaximumGivebackR = 0.50m }
        ],
        EnableStagnationReduction = true,
        StagnationMinimumOpenProfitR = 0.75m,
        StagnationBars = 12,
        StagnationReductionFraction = 0.15m,
        EnableStructuralDeteriorationReduction = true,
        StructuralDeteriorationReductionFraction = 0.20m,
        MaximumStructuralDeteriorationReductions = 1,
        EnableMomentumDecayReduction = true,
        MomentumDecayMinimumOpenProfitR = 1.25m,
        MomentumDecayReductionFraction = 0.15m,
        EnableVolatilityExhaustionReduction = true,
        VolatilityExhaustionMinimumOpenProfitR = 1.50m,
        VolatilityExhaustionReductionFraction = 0.15m,
        EnableRegimeDegradationReduction = true,
        RegimeDegradationMinimumOpenProfitR = 1.0m,
        RegimeDegradationReductionFraction = 0.25m,
        // Session/rollover timing is broker and DST specific, so it is deliberately opt-in.
        EnableRiskWindowReduction = false,
        EnableExecutionCostStressReduction = false
    };

    public static PositionManagementOptions ImprovedDefaults { get; } = new()
    {
        Mode = TrailingStopMode.StructureAtr,
        EvaluateMechanicalProtectionOnEveryExecutionFrame = true,
        FastStructureInterval = BarInterval.Minutes(5),
        MainStructureInterval = BarInterval.Minutes(15),
        ThesisInterval = BarInterval.Hours(1),
        BreakEvenActivationR = 1m,
        StructureTrailActivationR = 2m,
        AtrBufferMultiplier = 0.25m,
        PreserveBracketTarget = true,
        EnableScaleOut = true,
        ScaleOutRules =
        [
            new ScaleOutRule
            {
                StageId = "scale-1r",
                ActivationR = 1m,
                MinimumOpenProfitR = 1m,
                FractionOfInitialQuantity = 0.15m,
                TriggerMode = ScaleOutTriggerMode.RThreshold
            },
            new ScaleOutRule
            {
                StageId = "scale-2r-or-structure",
                ActivationR = 2m,
                MinimumOpenProfitR = 1.25m,
                FractionOfInitialQuantity = 0.15m,
                TriggerMode = ScaleOutTriggerMode.RThresholdOrOpposingStructure
            }
        ],
        MinimumRunnerFraction = 0.50m,
        EnableProfitFloor = true,
        ProfitFloorRules =
        [
            new ProfitFloorRule { ActivationR = 1m, LockedProfitR = 0m },
            new ProfitFloorRule { ActivationR = 2m, LockedProfitR = 0.50m },
            new ProfitFloorRule { ActivationR = 3m, LockedProfitR = 1.50m }
        ],
        EnableMaximumGiveback = true,
        MaximumGivebackRules =
        [
            new ProfitGivebackRule { ActivationR = 2.5m, MaximumGivebackR = 0.75m },
            new ProfitGivebackRule { ActivationR = 3.5m, MaximumGivebackR = 0.50m }
        ],
        EnableStagnationReduction = true,
        StagnationMinimumOpenProfitR = 1m,
        StagnationBars = 16,
        StagnationReductionFraction = 0.10m,
        EnableStructuralDeteriorationReduction = true,
        StructuralDeteriorationReductionFraction = 0.15m,
        MaximumStructuralDeteriorationReductions = 1,
        EnableMomentumDecayReduction = true,
        MomentumDecayMinimumOpenProfitR = 1.50m,
        MomentumDecayReductionFraction = 0.10m,
        EnableVolatilityExhaustionReduction = true,
        VolatilityExhaustionMinimumOpenProfitR = 2.00m,
        VolatilityExhaustionReductionFraction = 0.10m,
        EnableRegimeDegradationReduction = true,
        RegimeDegradationMinimumOpenProfitR = 1.25m,
        RegimeDegradationReductionFraction = 0.20m,
        EnableRiskWindowReduction = false,
        EnableExecutionCostStressReduction = false
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) ||
            (ManagementInterval is BarInterval interval && !interval.IsValid) ||
            (FastStructureInterval is BarInterval fast && !fast.IsValid) ||
            (MainStructureInterval is BarInterval main && !main.IsValid) ||
            (ThesisInterval is BarInterval thesis && !thesis.IsValid) ||
            BreakEvenActivationR <= 0m ||
            StructureTrailActivationR < BreakEvenActivationR ||
            AtrBufferMultiplier < 0m || BreakEvenBufferAtr < 0m ||
            MinimumStopImprovementAtr < 0m || MinimumStopImprovementTicks < 0m ||
            MinimumAnalysisBarsBetweenAmendments < 1 ||
            NeoWaveInvalidationBufferAtr < 0m ||
            string.IsNullOrWhiteSpace(StructuralManagementPolicyRevision) ||
            MinimumRunnerFraction is < 0m or >= 1m ||
            OpposingStructureProximityAtr < 0m ||
            MinimumAnalysisBarsBetweenReductions < 1 ||
            MinimumStructuralTrailDistanceAtr < 0m ||
            MaximumStructuralTrailDistanceAtr < MinimumStructuralTrailDistanceAtr ||
            MinimumZoneStrengthForManagement is < 0m or > 100m ||
            MinimumChannelConfidenceForManagement is < 0m or > 100m ||
            StagnationMinimumOpenProfitR < 0m || StagnationBars < 1 ||
            StagnationMinimumMfeAdvanceR < 0m ||
            MinimumStagnationBarsAtLowConfidence < 1 ||
            MaximumStagnationBarsAtHighConfidence < MinimumStagnationBarsAtLowConfidence ||
            StagnationReductionFraction is <= 0m or >= 1m ||
            StructuralDeteriorationReductionFraction is <= 0m or >= 1m ||
            MaximumStructuralDeteriorationReductions < 0 ||
            MomentumDecayMinimumOpenProfitR < 0m ||
            MomentumDecayReductionFraction is <= 0m or >= 1m ||
            MaximumMomentumDecayReductions < 0 ||
            VolatilityExhaustionMinimumOpenProfitR < 0m ||
            VolatilityExhaustionReductionFraction is <= 0m or >= 1m ||
            MaximumVolatilityExhaustionReductions < 0 ||
            RegimeDegradationMinimumOpenProfitR < 0m ||
            RegimeDegradationReductionFraction is <= 0m or >= 1m ||
            MaximumRegimeDegradationReductions < 0 ||
            RiskWindowMinimumOpenProfitR < 0m ||
            RiskWindowReductionFraction is <= 0m or >= 1m ||
            MaximumSpreadToAtrRatio <= 0m ||
            ExecutionCostStressMinimumOpenProfitR < 0m ||
            ExecutionCostStressReductionFraction is <= 0m or >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(PositionManagementOptions));
        }

        if (FastStructureInterval is BarInterval orderedFast &&
            MainStructureInterval is BarInterval orderedMain &&
            BarIntervalParser.CompareDuration(orderedFast, orderedMain) > 0)
        {
            throw new ArgumentException("Fast structure interval cannot be coarser than main structure interval.");
        }
        if (MainStructureInterval is BarInterval orderedMainInterval &&
            ThesisInterval is BarInterval orderedThesis &&
            BarIntervalParser.CompareDuration(orderedMainInterval, orderedThesis) > 0)
        {
            throw new ArgumentException("Main structure interval cannot be coarser than thesis interval.");
        }

        if (EnableRiskWindowReduction &&
            (RiskWindowStartUtc is null || RiskWindowEndUtc is null ||
             RiskWindowStartUtc == RiskWindowEndUtc))
        {
            throw new ArgumentException(
                "An enabled risk window requires distinct UTC start and end times.");
        }

        if (ScaleOutRules is null || ProfitFloorRules is null || MaximumGivebackRules is null)
            throw new ArgumentException("Profit-protection rule collections cannot be null.");

        if (ScaleOutRules.Any(rule =>
                string.IsNullOrWhiteSpace(rule.StageId) ||
                rule.ActivationR <= 0m ||
                rule.MinimumOpenProfitR < 0m ||
                rule.FractionOfInitialQuantity is <= 0m or >= 1m ||
                !Enum.IsDefined(rule.TriggerMode)) ||
            ScaleOutRules.Select(rule => rule.StageId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != ScaleOutRules.Count)
        {
            throw new ArgumentException("Scale-out stages must be unique and contain valid thresholds and fractions.");
        }

        decimal maximumPlannedReduction = ScaleOutRules.Sum(rule => rule.FractionOfInitialQuantity);
        if (maximumPlannedReduction > 1m - MinimumRunnerFraction + 0.00000001m)
            throw new ArgumentException("Scale-out stages would reduce the position below the configured runner fraction.");

        if (ProfitFloorRules.Any(rule =>
                rule.ActivationR <= 0m || rule.LockedProfitR < 0m ||
                rule.LockedProfitR >= rule.ActivationR))
        {
            throw new ArgumentException("Profit-floor rules must lock a non-negative R below their activation R.");
        }

        if (MaximumGivebackRules.Any(rule =>
                rule.ActivationR <= 0m || rule.MaximumGivebackR <= 0m ||
                rule.MaximumGivebackR >= rule.ActivationR))
        {
            throw new ArgumentException("Giveback rules must have a positive giveback smaller than their activation R.");
        }
    }
}

/// <summary>Compatibility name retained for callers from the original TradeManager API.</summary>
public sealed record StructureBasedTradeManagementOptions : PositionManagementOptions;

public sealed record ManagedTradeState
{
    public string StrategyId { get; init; } = "unknown";
    public string InstrumentGroup { get; init; } = "Unknown";
    public string SetupType { get; init; } = "Unknown";
    public string EntrySession { get; init; } = "Unknown";
    public string EntryVolatilityBucket { get; init; } = "Unknown";
    public decimal EntryConfidence { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal InitialStopPrice { get; init; }
    public required decimal CurrentStopPrice { get; init; }
    /// <summary>Executable bid for longs or executable ask for shorts.</summary>
    public required decimal CurrentPrice { get; init; }
    public decimal TakeProfitPrice { get; init; }
    public decimal InitialQuantity { get; init; } = 1m;
    public decimal CurrentQuantity { get; init; } = 1m;
    public decimal MinimumQuantityIncrement { get; init; } = 1m;
    public decimal MaximumFavourableExcursionR { get; init; }
    public IReadOnlySet<string> CompletedReductionStageIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool HasPendingReduction { get; init; }
    public long? LastReductionSnapshotVersion { get; init; }
    public int AnalysisBarsSinceLastReduction { get; init; } = int.MaxValue;
    public int AnalysisBarsWithoutNewMfe { get; init; }
    public bool StagnationReductionCompleted { get; init; }
    public int StructuralDeteriorationReductionCount { get; init; }
    public int MomentumDecayReductionCount { get; init; }
    public int VolatilityExhaustionReductionCount { get; init; }
    public int RegimeDegradationReductionCount { get; init; }
    public bool VolatilityExpansionSeenSinceEntry { get; init; }
    public bool RiskWindowReductionCompleted { get; init; }
    public bool ExecutionCostStressReductionCompleted { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    public decimal MinimumPriceIncrement { get; init; } = 0.00001m;
    public decimal EntryCommissionPrice { get; init; }
    public decimal ExpectedExitCommissionPrice { get; init; }
    public decimal SpreadPrice { get; init; }
    public decimal SlippagePrice { get; init; }
    public long? LastAmendmentSnapshotVersion { get; init; }
    public int AnalysisBarsSinceLastAmendment { get; init; } = int.MaxValue;
    public MarketRegime EntryRegime { get; init; } = MarketRegime.Unknown;
    public string EntryManagementProfileId { get; init; } = "default";

    /// <summary>Preferred directional wave hypothesis captured at entry, for audit only.</summary>
    public string? EntryNeoWaveHypothesisId { get; init; }

    /// <summary>
    /// Structural invalidation price captured at entry. It is immutable for the position
    /// and may be used only when <see cref="PositionManagementOptions.EnableNeoWaveInvalidationExit"/>
    /// is explicitly enabled.
    /// </summary>
    public decimal? EntryNeoWaveInvalidationPrice { get; init; }

    /// <summary>Immutable supply/demand thesis reference captured at entry.</summary>
    public Guid? EntrySupplyDemandZoneId { get; init; }

    /// <summary>Immutable inferred-liquidity target captured at entry.</summary>
    public Guid? TargetLiquidityPoolId { get; init; }

    /// <summary>Management policy revision captured at entry.</summary>
    public string? StructuralManagementPolicyRevision { get; init; }

    /// <summary>Supply/demand management opt-in captured at entry.</summary>
    public bool EntrySupplyDemandManagementEnabled { get; init; }

    /// <summary>Liquidity management opt-in captured at entry.</summary>
    public bool EntryLiquidityManagementEnabled { get; init; }
}

public sealed record PositionReductionRecommendation
{
    public required string StageId { get; init; }
    public required decimal QuantityToClose { get; init; }
    public required decimal FractionOfInitialQuantity { get; init; }
    public required decimal QuantityRemainingAfterReduction { get; init; }
    public required PositionReductionReason Reason { get; init; }
    public string? StructureSource { get; init; }
    public decimal? StructuralLevel { get; init; }
    public required string Explanation { get; init; }
}

public sealed record TradeManagementRecommendation
{
    public required TradeManagementAction Action { get; init; }
    public decimal? ProposedStopPrice { get; init; }
    public PositionReductionRecommendation? PositionReduction { get; init; }
    public TradeManagementExitReason ExitReason { get; init; }
    public required decimal OpenProfitR { get; init; }
    public decimal? LockedProfitR { get; init; }
    public decimal? ProfitFloorR { get; init; }
    public decimal? MaximumGivebackFloorR { get; init; }
    public StopAmendmentReason AmendmentReason { get; init; } = StopAmendmentReason.Other;
    public decimal? RawEntryPrice { get; init; }
    public decimal? CostAdjustedBreakEvenPrice { get; init; }
    public decimal? AtrBufferPrice { get; init; }
    public decimal? Atr { get; init; }
    public decimal? StructuralLevel { get; init; }
    public string? StructureSource { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
}

public interface IStructureBasedTradeManager
{
    TradeManagementRecommendation Evaluate(ManagedTradeState trade, AnalysisSnapshot analysis);

    TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        EquityProtectionDirective? equityProtection = null);
}

/// <summary>
/// Produces deterministic recommendations only. Initial R never changes after a stop amendment.
/// Structure selects meaningful levels; ATR adds volatility spacing; profit floors, MFE giveback,
/// staged reductions and stagnation rules protect realised and unrealised profit.
/// Broker mutation remains an ExecutionManager responsibility.
/// </summary>
public sealed class StructureBasedTradeManager : IStructureBasedTradeManager
{
    private readonly PositionManagementOptions _options;

    public StructureBasedTradeManager(PositionManagementOptions? options = null)
    {
        _options = options ?? new PositionManagementOptions();
        _options.Validate();
    }

    public TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis) =>
        Evaluate(trade, analysis, TradeManagementEvaluationScope.Combined);

    public TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        EquityProtectionDirective? equityProtection = null)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(analysis);
        Validate(trade, analysis);
        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope));

        // Structural evaluations also include mechanical protection so a structural
        // action cannot suppress a tighter break-even/profit-floor/MFE candidate on the
        // same execution frame. Pure thesis evaluation remains exit-only.
        bool includeMechanical = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.Mechanical or
            TradeManagementEvaluationScope.FastStructure or
            TradeManagementEvaluationScope.MainStructure;
        bool includeFastStructure = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.FastStructure;
        bool includeMainStructure = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.MainStructure;
        bool includeThesis = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.MainStructure or
            TradeManagementEvaluationScope.Thesis;

        decimal initialRisk = Math.Abs(trade.EntryPrice - trade.InitialStopPrice);
        decimal favourableMove = trade.Side == OrderSide.Buy
            ? trade.CurrentPrice - trade.EntryPrice
            : trade.EntryPrice - trade.CurrentPrice;
        decimal openProfitR = favourableMove / initialRisk;
        decimal maximumFavourableR = Math.Max(openProfitR, trade.MaximumFavourableExcursionR);
        bool adverseBreak = IsAdverseBreak(trade.Side, analysis.MarketStructure.Break);
        decimal? atr = analysis.Indicators.Atr is decimal atrValue && atrValue > 0m
            ? atrValue
            : null;

        // Account/strategy-level equity protection overrides every other rule below,
        // regardless of scope - it's a safety escalation, not a technical management rule.
        if (equityProtection is not null)
        {
            if (equityProtection.Action == EquityProtectionPositionAction.FlattenAllPositions)
            {
                return Exit(
                    openProfitR,
                    TradeManagementExitReason.EquityProtectionBreach,
                    "EquityProtectionFlatten",
                    $"Equity protection tier '{equityProtection.TierId}' requires flattening this position.");
            }

            string reductionStageId = $"equity-protection-{equityProtection.TierId}";
            if (!trade.CompletedReductionStageIds.Contains(reductionStageId))
            {
                PositionReductionRecommendation? equityReduction = CreateReduction(
                    trade,
                    reductionStageId,
                    equityProtection.ReductionFraction,
                    PositionReductionReason.RiskReduction,
                    $"Equity protection tier '{equityProtection.TierId}' requires reducing open exposure.");
                if (equityReduction is not null)
                {
                    return new TradeManagementRecommendation
                    {
                        Action = TradeManagementAction.ReducePosition,
                        PositionReduction = equityReduction,
                        OpenProfitR = openProfitR,
                        ReasonCode = "EquityProtectionReduce",
                        Reason = equityReduction.Explanation
                    };
                }
            }
        }

        if (includeThesis &&
            _options.EnableNeoWaveInvalidationExit &&
            trade.EntryNeoWaveInvalidationPrice is decimal neoWaveInvalidation)
        {
            decimal confirmationBuffer = (atr ?? 0m) * _options.NeoWaveInvalidationBufferAtr;
            bool invalidated = trade.Side == OrderSide.Buy
                ? trade.CurrentPrice < neoWaveInvalidation - confirmationBuffer
                : trade.CurrentPrice > neoWaveInvalidation + confirmationBuffer;
            if (invalidated)
            {
                string hypothesis = string.IsNullOrWhiteSpace(trade.EntryNeoWaveHypothesisId)
                    ? "entry wave hypothesis"
                    : $"entry wave hypothesis '{trade.EntryNeoWaveHypothesisId}'";
                return Exit(
                    openProfitR,
                    TradeManagementExitReason.NeoWaveInvalidation,
                    "NeoWaveEntryHypothesisInvalidated",
                    $"Price crossed the {hypothesis} invalidation level {neoWaveInvalidation} " +
                    $"with an ATR confirmation buffer of {confirmationBuffer}.");
            }
        }

        bool structuralPolicyMatches = string.Equals(
            trade.StructuralManagementPolicyRevision,
            _options.StructuralManagementPolicyRevision,
            StringComparison.Ordinal);

        if (includeThesis && structuralPolicyMatches &&
            _options.SupplyDemandManagementEnabled &&
            trade.EntrySupplyDemandManagementEnabled &&
            trade.EntrySupplyDemandZoneId is Guid entryZoneId &&
            analysis.SupplyDemand.RecentEvents.Any(evt =>
                evt.ZoneId == entryZoneId &&
                evt.EventType == SupplyDemandZoneEventType.Invalidated &&
                evt.AvailableAt == analysis.AvailableAt))
        {
            return Exit(
                openProfitR,
                TradeManagementExitReason.EntrySupplyDemandZoneInvalidated,
                "EntrySupplyDemandZoneInvalidated",
                $"The entry-pinned supply/demand zone '{entryZoneId}' was invalidated by the current causal snapshot.");
        }

        if (includeThesis && structuralPolicyMatches &&
            _options.LiquidityManagementEnabled &&
            trade.EntryLiquidityManagementEnabled &&
            trade.TargetLiquidityPoolId is Guid targetPoolId &&
            analysis.Liquidity.RecentEvents.Any(evt =>
                evt.PoolId == targetPoolId &&
                evt.EventType == LiquidityEventType.AcceptedBreak &&
                evt.AvailableAt == analysis.AvailableAt) &&
            analysis.Liquidity.Pools.FirstOrDefault(pool => pool.PoolId == targetPoolId) is { } targetPool &&
            IsAdverseLiquiditySide(trade.Side, targetPool.Side))
        {
            return Exit(
                openProfitR,
                TradeManagementExitReason.TargetLiquidityAcceptedBreak,
                "EntryTargetLiquidityAcceptedBreak",
                $"The entry-pinned {targetPool.Side} inferred-liquidity pool '{targetPoolId}' recorded an adverse accepted break.");
        }

        if (includeThesis && _options.ExitOnAdverseStructureBreak && adverseBreak)
        {
            return Exit(
                openProfitR,
                TradeManagementExitReason.AdverseStructure,
                "AdverseStructureExit",
                $"An adverse {analysis.MarketStructure.Break} market-structure break was confirmed.");
        }

        ProfitProtectionFloor? floor = includeMechanical
            ? FindProfitProtectionFloor(maximumFavourableR)
            : null;

        // P2: profit-floor / giveback prefer stop advancement first. Hard exit is only a
        // safety net when the floor stop is already protecting or can no longer be placed
        // (price has already traded through the floor level).
        if (floor is not null && openProfitR <= floor.LockedR)
        {
            decimal floorPrice = PriceForLockedR(
                trade.Side,
                trade.EntryPrice,
                initialRisk,
                floor.LockedR);
            bool stopAlreadyAtFloor = trade.Side == OrderSide.Buy
                ? trade.CurrentStopPrice + trade.MinimumPriceIncrement >= floorPrice
                : trade.CurrentStopPrice - trade.MinimumPriceIncrement <= floorPrice;
            bool floorStopPlaceable = IsBeforeCurrentPrice(
                trade.Side,
                floorPrice,
                trade.CurrentPrice);

            if (!_options.PreferFloorStopBeforeHardExit ||
                stopAlreadyAtFloor ||
                !floorStopPlaceable)
            {
                return Exit(
                    openProfitR,
                    floor.Source == ProfitProtectionSource.MaximumGiveback
                        ? TradeManagementExitReason.MaximumGivebackBreached
                        : TradeManagementExitReason.ProfitFloorBreached,
                    floor.Source == ProfitProtectionSource.MaximumGiveback
                        ? "MaximumGivebackBreached"
                        : "ProfitFloorBreached",
                    $"Open profit fell to {openProfitR:F2}R after protection had locked {floor.LockedR:F2}R.",
                    floor);
            }
        }

        StopCandidate? stopCandidate = BuildStopCandidate(
            trade,
            analysis,
            openProfitR,
            maximumFavourableR,
            atr,
            initialRisk,
            floor,
            includeMechanical,
            includeFastStructure || includeMainStructure);
        PositionReductionRecommendation? reduction = FindPositionReduction(
            trade,
            analysis,
            openProfitR,
            atr,
            adverseBreak,
            scope);

        bool moveStop = stopCandidate is not null;
        TradeManagementAction action = (reduction, moveStop) switch
        {
            (not null, true) => TradeManagementAction.ReduceAndMoveStop,
            (not null, false) => TradeManagementAction.ReducePosition,
            (null, true) => TradeManagementAction.MoveStop,
            _ => TradeManagementAction.Hold
        };

        if (action == TradeManagementAction.Hold)
        {
            return Hold(
                openProfitR,
                ResolveHoldReasonCode(trade, analysis, openProfitR, atr),
                ResolveHoldReason(trade, analysis, openProfitR, atr));
        }

        string reason = string.Join(" ", new[]
        {
            reduction?.Explanation,
            stopCandidate?.Explanation
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new TradeManagementRecommendation
        {
            Action = action,
            ProposedStopPrice = stopCandidate?.Price,
            PositionReduction = reduction,
            OpenProfitR = openProfitR,
            LockedProfitR = stopCandidate?.LockedR,
            ProfitFloorR = floor?.Source == ProfitProtectionSource.ProfitFloor ? floor.LockedR : null,
            MaximumGivebackFloorR = floor?.Source == ProfitProtectionSource.MaximumGiveback ? floor.LockedR : null,
            AmendmentReason = stopCandidate?.Reason ?? StopAmendmentReason.Other,
            RawEntryPrice = trade.EntryPrice,
            CostAdjustedBreakEvenPrice = stopCandidate?.CostAdjustedBreakEvenPrice,
            AtrBufferPrice = stopCandidate?.AtrBuffer,
            Atr = atr,
            StructuralLevel = stopCandidate?.StructuralLevel ?? reduction?.StructuralLevel,
            StructureSource = stopCandidate?.Source ?? reduction?.StructureSource,
            ReasonCode = action.ToString(),
            Reason = reason
        };
    }

    private StopCandidate? BuildStopCandidate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal openProfitR,
        decimal maximumFavourableR,
        decimal? atr,
        decimal initialRisk,
        ProfitProtectionFloor? floor,
        bool includeMechanical,
        bool includeStructure)
    {
        if (trade.LastAmendmentSnapshotVersion is long lastVersion && analysis.Version <= lastVersion)
            return null;
        if (trade.AnalysisBarsSinceLastAmendment < _options.MinimumAnalysisBarsBetweenAmendments)
            return null;

        var candidates = new List<StopCandidate>();
        if (includeMechanical &&
            _options.Mode != TrailingStopMode.Disabled &&
            openProfitR >= _options.BreakEvenActivationR)
        {
            BreakEvenCalculation breakEven = CalculateBreakEven(trade, atr ?? 0m);
            candidates.Add(new StopCandidate(
                breakEven.FinalPrice,
                ToLockedR(trade.Side, trade.EntryPrice, initialRisk, breakEven.FinalPrice),
                StopAmendmentReason.BreakEven,
                "Cost-adjusted break-even",
                $"Break-even protection after {openProfitR:F2}R; raw entry {trade.EntryPrice}, " +
                $"cost-adjusted {breakEven.CostAdjustedPrice}, ATR buffer {breakEven.AtrBuffer}.",
                CostAdjustedBreakEvenPrice: breakEven.CostAdjustedPrice,
                AtrBuffer: breakEven.AtrBuffer));
        }

        if (includeStructure &&
            _options.Mode == TrailingStopMode.StructureAtr &&
            openProfitR >= _options.StructureTrailActivationR &&
            atr is > 0m &&
            FindStructuralCandidate(trade, analysis, atr.Value) is StructuralCandidate structural)
        {
            candidates.Add(new StopCandidate(
                structural.StopPrice,
                ToLockedR(trade.Side, trade.EntryPrice, initialRisk, structural.StopPrice),
                structural.Reason,
                structural.Source,
                $"Trailing behind {structural.Source} at {structural.Level} with " +
                $"{_options.AtrBufferMultiplier:F2} ATR ({atr.Value * _options.AtrBufferMultiplier}) buffer.",
                StructuralLevel: structural.Level));
        }

        if (includeMechanical && floor is not null)
        {
            decimal floorPrice = PriceForLockedR(trade.Side, trade.EntryPrice, initialRisk, floor.LockedR);
            candidates.Add(new StopCandidate(
                floorPrice,
                floor.LockedR,
                floor.Source == ProfitProtectionSource.MaximumGiveback
                    ? StopAmendmentReason.MfeGiveback
                    : StopAmendmentReason.ProfitFloor,
                floor.Source == ProfitProtectionSource.MaximumGiveback
                    ? "MFE maximum-giveback floor"
                    : "Profit-floor ratchet",
                floor.Explanation));
        }

        StopCandidate? candidate = trade.Side == OrderSide.Buy
            ? candidates.OrderByDescending(item => item.Price).FirstOrDefault()
            : candidates.OrderBy(item => item.Price).FirstOrDefault();
        if (candidate is null)
            return null;

        decimal atrImprovement = atr is > 0m
            ? atr.Value * _options.MinimumStopImprovementAtr
            : 0m;
        decimal minimumImprovement = Math.Max(
            atrImprovement,
            trade.MinimumPriceIncrement * _options.MinimumStopImprovementTicks);
        return ImprovesCurrentStop(trade, candidate.Price, minimumImprovement) &&
            IsBeforeCurrentPrice(trade.Side, candidate.Price, trade.CurrentPrice)
            ? candidate
            : null;
    }

    private PositionReductionRecommendation? FindPositionReduction(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal openProfitR,
        decimal? atr,
        bool adverseBreak,
        TradeManagementEvaluationScope scope)
    {
        if (trade.HasPendingReduction ||
            trade.LastReductionSnapshotVersion == analysis.Version ||
            trade.AnalysisBarsSinceLastReduction < _options.MinimumAnalysisBarsBetweenReductions)
        {
            return null;
        }

        bool mechanical = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.Mechanical or
            TradeManagementEvaluationScope.FastStructure or
            TradeManagementEvaluationScope.MainStructure;
        bool fastStructure = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.FastStructure;
        bool mainStructure = scope is TradeManagementEvaluationScope.Combined or
            TradeManagementEvaluationScope.MainStructure;

        if ((fastStructure || mainStructure) &&
            _options.EnableStructuralDeteriorationReduction && adverseBreak &&
            trade.StructuralDeteriorationReductionCount < _options.MaximumStructuralDeteriorationReductions)
        {
            return CreateReduction(
                trade,
                $"structure-deterioration-{trade.StructuralDeteriorationReductionCount + 1}",
                _options.StructuralDeteriorationReductionFraction,
                PositionReductionReason.StructuralDeterioration,
                $"Reduced exposure after an adverse {analysis.MarketStructure.Break} structure break.");
        }

        if (mainStructure && _options.EnableMomentumDecayReduction &&
            openProfitR >= _options.MomentumDecayMinimumOpenProfitR &&
            trade.MomentumDecayReductionCount < _options.MaximumMomentumDecayReductions &&
            HasConfirmedMomentumDecay(trade.Side, analysis, out string momentumExplanation))
        {
            return CreateReduction(
                trade,
                $"momentum-decay-{trade.MomentumDecayReductionCount + 1}",
                _options.MomentumDecayReductionFraction,
                PositionReductionReason.MomentumDecay,
                momentumExplanation);
        }

        if (mainStructure && _options.EnableVolatilityExhaustionReduction &&
            openProfitR >= _options.VolatilityExhaustionMinimumOpenProfitR &&
            trade.VolatilityExhaustionReductionCount < _options.MaximumVolatilityExhaustionReductions &&
            HasConfirmedVolatilityExhaustion(
                trade.Side,
                analysis,
                trade.VolatilityExpansionSeenSinceEntry,
                out string volatilityExplanation))
        {
            return CreateReduction(
                trade,
                $"volatility-exhaustion-{trade.VolatilityExhaustionReductionCount + 1}",
                _options.VolatilityExhaustionReductionFraction,
                PositionReductionReason.VolatilityExhaustion,
                volatilityExplanation);
        }

        if (mainStructure && _options.EnableRegimeDegradationReduction &&
            !analysis.MarketRegime.IsTradeable &&
            openProfitR >= _options.RegimeDegradationMinimumOpenProfitR &&
            trade.RegimeDegradationReductionCount < _options.MaximumRegimeDegradationReductions)
        {
            return CreateReduction(
                trade,
                $"regime-degradation-{trade.RegimeDegradationReductionCount + 1}",
                _options.RegimeDegradationReductionFraction,
                PositionReductionReason.RegimeDegradation,
                $"Reduced exposure after the regime degraded to {analysis.MarketRegime.Regime} " +
                $"({analysis.MarketRegime.ReasonCode}).");
        }

        if (mechanical && _options.EnableRiskWindowReduction &&
            !trade.RiskWindowReductionCompleted &&
            openProfitR >= _options.RiskWindowMinimumOpenProfitR &&
            IsInsideRiskWindow(trade.EvaluatedAt))
        {
            string start = _options.RiskWindowStartUtc!.Value.ToString("HH:mm");
            string end = _options.RiskWindowEndUtc!.Value.ToString("HH:mm");
            return CreateReduction(
                trade,
                $"risk-window-{trade.EvaluatedAt:yyyyMMdd}",
                _options.RiskWindowReductionFraction,
                PositionReductionReason.SessionRisk,
                $"Reduced profitable exposure inside configured UTC risk window {start}-{end}.");
        }

        decimal? spreadToAtr = atr is > 0m ? trade.SpreadPrice / atr.Value : null;
        if (mechanical && _options.EnableExecutionCostStressReduction &&
            !trade.ExecutionCostStressReductionCompleted &&
            openProfitR >= _options.ExecutionCostStressMinimumOpenProfitR &&
            spreadToAtr is decimal ratio && ratio >= _options.MaximumSpreadToAtrRatio)
        {
            return CreateReduction(
                trade,
                "execution-cost-stress",
                _options.ExecutionCostStressReductionFraction,
                PositionReductionReason.ExecutionCostStress,
                $"Spread/ATR ratio {ratio:F3} reached the configured stress threshold " +
                $"{_options.MaximumSpreadToAtrRatio:F3}.");
        }

        if (_options.EnableScaleOut)
        {
            // P2: R-threshold scale-outs require a management-bar scope (fast/main), not a
            // pure mechanical execution frame, so 1s/5s noise does not fire partials.
            bool managementBar = fastStructure || mainStructure ||
                scope == TradeManagementEvaluationScope.Combined;
            bool allowRThresholdScaleOut = !_options.ScaleOutRThresholdRequiresManagementBar ||
                managementBar;

            OpposingStructure? opposing = (fastStructure || mainStructure) && atr is > 0m
                ? FindOpposingStructure(trade, analysis, atr.Value)
                : null;
            foreach (ScaleOutRule rule in _options.ScaleOutRules.OrderBy(item => item.ActivationR))
            {
                if (trade.CompletedReductionStageIds.Contains(rule.StageId))
                    continue;

                bool rTriggered = openProfitR >= rule.ActivationR;
                bool structureTriggered = opposing is not null && openProfitR >= rule.MinimumOpenProfitR;
                bool triggered = rule.TriggerMode switch
                {
                    ScaleOutTriggerMode.RThreshold =>
                        allowRThresholdScaleOut && rTriggered,
                    ScaleOutTriggerMode.OpposingStructure =>
                        (fastStructure || mainStructure) && structureTriggered,
                    ScaleOutTriggerMode.RThresholdOrOpposingStructure =>
                        (allowRThresholdScaleOut && rTriggered) ||
                        ((fastStructure || mainStructure) && structureTriggered),
                    _ => false
                };
                if (!triggered)
                    continue;

                PositionReductionReason reason = structureTriggered && !rTriggered
                    ? PositionReductionReason.OpposingStructure
                    : PositionReductionReason.ScaleOutProfit;
                string explanation = structureTriggered && opposing is not null
                    ? $"Scale-out stage '{rule.StageId}' reached {opposing.Source} at {opposing.Level}."
                    : $"Scale-out stage '{rule.StageId}' activated at {openProfitR:F2}R.";
                return CreateReduction(
                    trade,
                    rule.StageId,
                    rule.FractionOfInitialQuantity,
                    reason,
                    explanation,
                    opposing?.Source,
                    opposing?.Level);
            }
        }

        if (mainStructure && _options.EnableStagnationReduction &&
            !trade.StagnationReductionCompleted &&
            openProfitR >= _options.StagnationMinimumOpenProfitR &&
            trade.AnalysisBarsWithoutNewMfe >= EffectiveStagnationBars(trade))
        {
            return CreateReduction(
                trade,
                "stagnation-reduction",
                _options.StagnationReductionFraction,
                PositionReductionReason.Stagnation,
                $"No meaningful new MFE was recorded for {trade.AnalysisBarsWithoutNewMfe} management bars.");
        }

        return null;
    }

    private int EffectiveStagnationBars(ManagedTradeState trade)
    {
        if (!_options.EnableConfidenceScaledStagnation)
            return _options.StagnationBars;

        decimal confidenceFraction = Math.Clamp(trade.EntryConfidence, 0m, 100m) / 100m;
        decimal interpolated = _options.MinimumStagnationBarsAtLowConfidence +
            confidenceFraction *
            (_options.MaximumStagnationBarsAtHighConfidence - _options.MinimumStagnationBarsAtLowConfidence);
        return (int)Math.Round(interpolated, MidpointRounding.AwayFromZero);
    }

    private bool IsInsideRiskWindow(DateTimeOffset timestamp)
    {
        if (_options.RiskWindowStartUtc is not TimeOnly start ||
            _options.RiskWindowEndUtc is not TimeOnly end)
        {
            return false;
        }

        TimeOnly value = TimeOnly.FromDateTime(timestamp.UtcDateTime);
        return start < end
            ? value >= start && value < end
            : value >= start || value < end;
    }

    private static bool HasConfirmedMomentumDecay(
        OrderSide side,
        AnalysisSnapshot analysis,
        out string explanation)
    {
        RsiAnalysisSnapshot rsi = analysis.Indicators.RsiAnalysis;
        AdxAnalysisSnapshot adx = analysis.Indicators.AdxAnalysis;
        RsiRelationshipType relationship = rsi.LatestRelationship?.Type ?? RsiRelationshipType.None;
        bool relationshipIsRecent = rsi.LatestRelationship?.AgeCandles is >= 0 and <= 5;
        bool adverseDivergence = relationshipIsRecent && (side == OrderSide.Buy
            ? relationship == RsiRelationshipType.RegularBearishDivergence
            : relationship == RsiRelationshipType.RegularBullishDivergence);
        bool adverseMomentum = side == OrderSide.Buy
            ? rsi.MomentumDirection == MomentumDirection.Falling
            : rsi.MomentumDirection == MomentumDirection.Rising;
        bool weakeningStrength = adx.StrengthDirection == MomentumDirection.Falling ||
            !adx.IsTrendStrengthening;
        // PriceActionSnapshot.Events contains only events confirmed by the current
        // completed analysis candle. ConfirmedSequence is a market-event sequence, whereas
        // AnalysisSnapshot.Version is a per-timeframe version; comparing them is invalid.
        bool adversePriceAction = analysis.PriceAction.Events.Any(item =>
            item.ConfirmedAt <= analysis.AvailableAt &&
            (side == OrderSide.Buy
                ? item.Type is PriceActionEventType.BearishChangeOfCharacter or
                    PriceActionEventType.BearishDisplacement or
                    PriceActionEventType.BearishRejection
                : item.Type is PriceActionEventType.BullishChangeOfCharacter or
                    PriceActionEventType.BullishDisplacement or
                    PriceActionEventType.BullishRejection));

        int evidence = (adverseDivergence ? 1 : 0) +
            (adverseMomentum ? 1 : 0) +
            (weakeningStrength ? 1 : 0) +
            (adversePriceAction ? 1 : 0);
        if (evidence < 2 || (!adverseDivergence && !adversePriceAction))
        {
            explanation = string.Empty;
            return false;
        }

        explanation = $"Reduced exposure after {evidence} momentum-decay confirmations: " +
            $"divergence={adverseDivergence}, adverseMomentum={adverseMomentum}, " +
            $"ADXWeakening={weakeningStrength}, adversePriceAction={adversePriceAction}.";
        return true;
    }

    private static bool HasConfirmedVolatilityExhaustion(
        OrderSide side,
        AnalysisSnapshot analysis,
        bool expansionSeenSinceEntry,
        out string explanation)
    {
        BollingerAnalysisSnapshot bollinger = analysis.Indicators.BollingerAnalysis;
        AdxAnalysisSnapshot adx = analysis.Indicators.AdxAnalysis;
        decimal close = analysis.LatestCandle.Prices.Close;
        decimal? middle = analysis.Indicators.BollingerMiddle;
        bool lostMiddle = middle is decimal value &&
            (side == OrderSide.Buy ? close < value : close > value);
        bool volatilityContracting = bollinger.WidthDirection == VolatilityDirection.Contracting ||
            bollinger.BandwidthChangePercent is < 0m;
        bool priorExpansionContext = expansionSeenSinceEntry;
        bool trendWeakening = adx.StrengthDirection == MomentumDirection.Falling ||
            !adx.IsTrendStrengthening;
        bool noFavourableBreak = side == OrderSide.Buy
            ? analysis.MarketStructure.Break != MarketStructureBreak.Bullish
            : analysis.MarketStructure.Break != MarketStructureBreak.Bearish;

        if (!(lostMiddle && volatilityContracting && priorExpansionContext &&
              trendWeakening && noFavourableBreak))
        {
            explanation = string.Empty;
            return false;
        }

        explanation = "Reduced exposure after volatility expansion lost momentum: " +
            "a prior expansion was observed after entry, Bollinger width contracted, " +
            "price lost the middle band, ADX weakened, and no new favourable structure break was confirmed.";
        return true;
    }

    private PositionReductionRecommendation? CreateReduction(
        ManagedTradeState trade,
        string stageId,
        decimal fractionOfInitialQuantity,
        PositionReductionReason reason,
        string explanation,
        string? structureSource = null,
        decimal? structuralLevel = null)
    {
        decimal minimumRemaining = trade.InitialQuantity * _options.MinimumRunnerFraction;
        decimal maximumReducible = Math.Max(0m, trade.CurrentQuantity - minimumRemaining);
        decimal desired = trade.InitialQuantity * fractionOfInitialQuantity;
        decimal quantity = Math.Min(desired, maximumReducible);
        quantity = RoundDown(quantity, trade.MinimumQuantityIncrement);
        if (quantity <= 0m || trade.CurrentQuantity - quantity <= 0m)
            return null;

        return new PositionReductionRecommendation
        {
            StageId = stageId,
            QuantityToClose = quantity,
            FractionOfInitialQuantity = quantity / trade.InitialQuantity,
            QuantityRemainingAfterReduction = trade.CurrentQuantity - quantity,
            Reason = reason,
            StructureSource = structureSource,
            StructuralLevel = structuralLevel,
            Explanation = explanation
        };
    }

    private ProfitProtectionFloor? FindProfitProtectionFloor(decimal maximumFavourableR)
    {
        var floors = new List<ProfitProtectionFloor>();
        if (_options.EnableProfitFloor)
        {
            ProfitFloorRule? rule = _options.ProfitFloorRules
                .Where(item => maximumFavourableR >= item.ActivationR)
                .OrderByDescending(item => item.LockedProfitR)
                .FirstOrDefault();
            if (rule is not null)
            {
                floors.Add(new ProfitProtectionFloor(
                    rule.LockedProfitR,
                    ProfitProtectionSource.ProfitFloor,
                    $"Profit-floor ratchet activated at {rule.ActivationR:F2}R and locks {rule.LockedProfitR:F2}R."));
            }
        }

        if (_options.EnableMaximumGiveback)
        {
            ProfitGivebackRule? rule = _options.MaximumGivebackRules
                .Where(item => maximumFavourableR >= item.ActivationR)
                .OrderByDescending(item => item.ActivationR)
                .FirstOrDefault();
            if (rule is not null)
            {
                decimal locked = Math.Max(0m, maximumFavourableR - rule.MaximumGivebackR);
                floors.Add(new ProfitProtectionFloor(
                    locked,
                    ProfitProtectionSource.MaximumGiveback,
                    $"MFE reached {maximumFavourableR:F2}R; maximum giveback {rule.MaximumGivebackR:F2}R " +
                    $"locks {locked:F2}R."));
            }
        }

        return floors.OrderByDescending(item => item.LockedR).FirstOrDefault();
    }

    private OpposingStructure? FindOpposingStructure(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal atr)
    {
        decimal proximity = atr * _options.OpposingStructureProximityAtr;
        var candidates = new List<OpposingStructure>();
        if (trade.Side == OrderSide.Buy)
        {
            candidates.AddRange(analysis.PriceZones
                .Where(zone =>
                    (zone.Type is PriceZoneType.Resistance or PriceZoneType.Mixed) &&
                    zone.Strength >= _options.MinimumZoneStrengthForManagement)
                .Select(zone => new
                {
                    Zone = zone,
                    Distance = trade.CurrentPrice < zone.LowerPrice
                        ? zone.LowerPrice - trade.CurrentPrice
                        : trade.CurrentPrice <= zone.UpperPrice ? 0m : decimal.MaxValue
                })
                .Where(item => item.Distance <= proximity)
                .Select(item => new OpposingStructure(
                    item.Zone.LowerPrice,
                    $"{item.Zone.Type.ToString().ToLowerInvariant()} zone")));
            candidates.AddRange(analysis.Channels
                .Where(channel => channel.Confidence >= _options.MinimumChannelConfidenceForManagement)
                .Select(channel => new
                {
                    Level = channel.UpperLine.PriceAt(analysis.AvailableAt),
                    channel.Confidence
                })
                .Where(item => item.Level >= trade.CurrentPrice && item.Level - trade.CurrentPrice <= proximity)
                .Select(item => new OpposingStructure(item.Level, $"upper channel ({item.Confidence:F0}% confidence)")));
            return candidates.OrderBy(item => item.Level).FirstOrDefault();
        }

        candidates.AddRange(analysis.PriceZones
            .Where(zone =>
                (zone.Type is PriceZoneType.Support or PriceZoneType.Mixed) &&
                zone.Strength >= _options.MinimumZoneStrengthForManagement)
            .Select(zone => new
            {
                Zone = zone,
                Distance = trade.CurrentPrice > zone.UpperPrice
                    ? trade.CurrentPrice - zone.UpperPrice
                    : trade.CurrentPrice >= zone.LowerPrice ? 0m : decimal.MaxValue
            })
            .Where(item => item.Distance <= proximity)
            .Select(item => new OpposingStructure(
                item.Zone.UpperPrice,
                $"{item.Zone.Type.ToString().ToLowerInvariant()} zone")));
        candidates.AddRange(analysis.Channels
            .Where(channel => channel.Confidence >= _options.MinimumChannelConfidenceForManagement)
            .Select(channel => new
            {
                Level = channel.LowerLine.PriceAt(analysis.AvailableAt),
                channel.Confidence
            })
            .Where(item => item.Level <= trade.CurrentPrice && trade.CurrentPrice - item.Level <= proximity)
            .Select(item => new OpposingStructure(item.Level, $"lower channel ({item.Confidence:F0}% confidence)")));
        return candidates.OrderByDescending(item => item.Level).FirstOrDefault();
    }

    private BreakEvenCalculation CalculateBreakEven(ManagedTradeState trade, decimal atr)
    {
        decimal costs = trade.EntryCommissionPrice + trade.SpreadPrice + trade.SlippagePrice;
        if (_options.IncludeEstimatedExitCostsAtBreakEven)
            costs += trade.ExpectedExitCommissionPrice + trade.SlippagePrice;
        decimal costAdjusted = trade.Side == OrderSide.Buy
            ? trade.EntryPrice + costs
            : trade.EntryPrice - costs;
        decimal atrBuffer = atr * _options.BreakEvenBufferAtr;
        decimal final = trade.Side == OrderSide.Buy
            ? costAdjusted + atrBuffer
            : costAdjusted - atrBuffer;
        return new BreakEvenCalculation(costAdjusted, atrBuffer, final);
    }

    private StructuralCandidate? FindStructuralCandidate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal atr)
    {
        decimal buffer = atr * _options.AtrBufferMultiplier;
        decimal minDistance = atr * _options.MinimumStructuralTrailDistanceAtr;
        decimal maxDistance = atr * _options.MaximumStructuralTrailDistanceAtr;
        var scored = new List<(StructuralCandidate Candidate, decimal Score, decimal Distance)>();

        void Consider(decimal level, StopAmendmentReason reason, string source, bool postEntry, decimal quality)
        {
            if (trade.Side == OrderSide.Buy ? level >= trade.CurrentPrice : level <= trade.CurrentPrice)
                return;

            decimal stopPrice = trade.Side == OrderSide.Buy ? level - buffer : level + buffer;
            if (!IsBeforeCurrentPrice(trade.Side, stopPrice, trade.CurrentPrice))
                return;

            decimal distance = Math.Abs(trade.CurrentPrice - level);
            if (distance < minDistance || distance > maxDistance)
                return;

            // Prefer post-entry structure, confirmed swings over zones/channels,
            // stronger quality, and a moderate ~1 ATR distance.
            decimal distanceScore = 1m - Math.Abs(distance / atr - 1m) / 2m;
            distanceScore = Math.Clamp(distanceScore, 0m, 1m);
            decimal typeBonus = reason switch
            {
                StopAmendmentReason.StructureSwing => 18m,
                StopAmendmentReason.StructureZone => 6m,
                StopAmendmentReason.StructureChannel => 0m,
                _ => 0m
            };
            decimal score =
                (postEntry || !_options.PreferPostEntryStructure ? 40m : 10m) +
                typeBonus +
                quality * 0.30m +
                distanceScore * 25m;

            scored.Add((
                new StructuralCandidate(level, stopPrice, reason, source),
                score,
                distance));
        }

        if (trade.Side == OrderSide.Buy)
        {
            foreach (SwingPoint swing in analysis.Swings.Where(swing =>
                         swing.Type == SwingType.Low &&
                         swing.Price < trade.CurrentPrice &&
                         swing.ConfirmedAt <= analysis.AvailableAt))
            {
                // ManagedTradeState has no open time; treat levels beyond the initial stop
                // as in-trade structure (formed/held after the original risk definition).
                bool postEntry = swing.Price > trade.InitialStopPrice;
                decimal quality = Math.Clamp(swing.Strength * 20m, 0m, 100m);
                Consider(
                    swing.Price,
                    StopAmendmentReason.StructureSwing,
                    $"confirmed swing low ({swing.PivotTime:O})",
                    postEntry,
                    quality);
            }

            foreach (PriceZone zone in analysis.PriceZones.Where(zone =>
                         (zone.Type is PriceZoneType.Support or PriceZoneType.Mixed) &&
                         zone.Strength >= _options.MinimumZoneStrengthForManagement &&
                         zone.UpperPrice < trade.CurrentPrice))
            {
                bool postEntry = zone.UpperPrice > trade.InitialStopPrice;
                Consider(
                    zone.LowerPrice,
                    StopAmendmentReason.StructureZone,
                    $"confirmed {zone.Type.ToString().ToLowerInvariant()} zone",
                    postEntry,
                    zone.Strength);
            }

            foreach (PriceChannel channel in analysis.Channels.Where(channel =>
                         channel.Confidence >= _options.MinimumChannelConfidenceForManagement))
            {
                decimal level = channel.LowerLine.PriceAt(analysis.AvailableAt);
                if (level >= trade.CurrentPrice)
                    continue;
                bool postEntry = level > trade.InitialStopPrice;
                Consider(
                    level,
                    StopAmendmentReason.StructureChannel,
                    $"lower structural channel ({channel.Confidence:F0}% confidence)",
                    postEntry,
                    channel.Confidence);
            }
        }
        else
        {
            foreach (SwingPoint swing in analysis.Swings.Where(swing =>
                         swing.Type == SwingType.High &&
                         swing.Price > trade.CurrentPrice &&
                         swing.ConfirmedAt <= analysis.AvailableAt))
            {
                bool postEntry = swing.Price < trade.InitialStopPrice;
                decimal quality = Math.Clamp(swing.Strength * 20m, 0m, 100m);
                Consider(
                    swing.Price,
                    StopAmendmentReason.StructureSwing,
                    $"confirmed swing high ({swing.PivotTime:O})",
                    postEntry,
                    quality);
            }

            foreach (PriceZone zone in analysis.PriceZones.Where(zone =>
                         (zone.Type is PriceZoneType.Resistance or PriceZoneType.Mixed) &&
                         zone.Strength >= _options.MinimumZoneStrengthForManagement &&
                         zone.LowerPrice > trade.CurrentPrice))
            {
                bool postEntry = zone.LowerPrice < trade.InitialStopPrice;
                Consider(
                    zone.UpperPrice,
                    StopAmendmentReason.StructureZone,
                    $"confirmed {zone.Type.ToString().ToLowerInvariant()} zone",
                    postEntry,
                    zone.Strength);
            }

            foreach (PriceChannel channel in analysis.Channels.Where(channel =>
                         channel.Confidence >= _options.MinimumChannelConfidenceForManagement))
            {
                decimal level = channel.UpperLine.PriceAt(analysis.AvailableAt);
                if (level <= trade.CurrentPrice)
                    continue;
                bool postEntry = level < trade.InitialStopPrice;
                Consider(
                    level,
                    StopAmendmentReason.StructureChannel,
                    $"upper structural channel ({channel.Confidence:F0}% confidence)",
                    postEntry,
                    channel.Confidence);
            }
        }

        if (scored.Count == 0)
            return null;

        // Highest score, then tightest protective stop (highest low / lowest high).
        return scored
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item =>
                trade.Side == OrderSide.Buy ? item.Candidate.StopPrice : -item.Candidate.StopPrice)
            .Select(item => item.Candidate)
            .First();
    }

    private static bool IsAdverseBreak(OrderSide side, MarketStructureBreak structureBreak) =>
        side switch
        {
            OrderSide.Buy => structureBreak == MarketStructureBreak.Bearish,
            OrderSide.Sell => structureBreak == MarketStructureBreak.Bullish,
            _ => false
        };

    private static bool IsAdverseLiquiditySide(OrderSide side, LiquiditySide liquiditySide) =>
        side switch
        {
            OrderSide.Buy => liquiditySide == LiquiditySide.SellSide,
            OrderSide.Sell => liquiditySide == LiquiditySide.BuySide,
            _ => false
        };

    private static bool ImprovesCurrentStop(
        ManagedTradeState trade,
        decimal candidate,
        decimal minimumImprovement) => trade.Side switch
        {
            OrderSide.Buy => candidate >= trade.CurrentStopPrice + minimumImprovement,
            OrderSide.Sell => candidate <= trade.CurrentStopPrice - minimumImprovement,
            _ => false
        };

    private static bool IsBeforeCurrentPrice(OrderSide side, decimal stop, decimal currentPrice) =>
        side == OrderSide.Buy ? stop < currentPrice : stop > currentPrice;

    private static decimal PriceForLockedR(
        OrderSide side,
        decimal entryPrice,
        decimal initialRisk,
        decimal lockedR) => side == OrderSide.Buy
        ? entryPrice + initialRisk * lockedR
        : entryPrice - initialRisk * lockedR;

    private static decimal ToLockedR(
        OrderSide side,
        decimal entryPrice,
        decimal initialRisk,
        decimal price) => side == OrderSide.Buy
        ? (price - entryPrice) / initialRisk
        : (entryPrice - price) / initialRisk;

    private static decimal RoundDown(decimal value, decimal increment)
    {
        if (increment <= 0m)
            return value;
        return Math.Floor(value / increment) * increment;
    }

    private static TradeManagementRecommendation Hold(
        decimal openProfitR,
        string reasonCode,
        string reason) => new()
    {
        Action = TradeManagementAction.Hold,
        OpenProfitR = openProfitR,
        ReasonCode = reasonCode,
        Reason = reason
    };

    private static TradeManagementRecommendation Exit(
        decimal openProfitR,
        TradeManagementExitReason exitReason,
        string reasonCode,
        string reason,
        ProfitProtectionFloor? floor = null) => new()
    {
        Action = TradeManagementAction.Exit,
        ExitReason = exitReason,
        OpenProfitR = openProfitR,
        LockedProfitR = floor?.LockedR,
        ProfitFloorR = floor?.Source == ProfitProtectionSource.ProfitFloor ? floor.LockedR : null,
        MaximumGivebackFloorR = floor?.Source == ProfitProtectionSource.MaximumGiveback ? floor.LockedR : null,
        ReasonCode = reasonCode,
        Reason = reason
    };

    private string ResolveHoldReasonCode(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal openProfitR,
        decimal? atr)
    {
        if (trade.HasPendingReduction)
            return "PositionReductionPending";
        if (trade.LastAmendmentSnapshotVersion is long version && analysis.Version <= version)
            return "SnapshotAlreadyProcessed";
        if (_options.Mode != TrailingStopMode.Disabled && atr is null)
            return "AtrNotReady";
        if (openProfitR < _options.BreakEvenActivationR)
            return "BelowBreakEvenThreshold";
        return "NoValidProfitProtectionImprovement";
    }

    private string ResolveHoldReason(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        decimal openProfitR,
        decimal? atr)
    {
        if (trade.HasPendingReduction)
            return "A previously submitted position reduction is still pending.";
        if (trade.LastAmendmentSnapshotVersion is long version && analysis.Version <= version)
            return "This analysis snapshot has already produced an amendment.";
        if (_options.Mode != TrailingStopMode.Disabled && atr is null)
            return "ATR is not ready, so ATR-dependent stop movement cannot be calculated.";
        if (openProfitR < _options.BreakEvenActivationR)
            return $"Open profit {openProfitR:F2}R has not reached the break-even threshold.";
        return "No rule produced a safe stop improvement or non-duplicate position reduction.";
    }

    private static void Validate(ManagedTradeState trade, AnalysisSnapshot analysis)
    {
        if (trade.Instrument.IsEmpty || trade.Instrument != analysis.Instrument)
            throw new ArgumentException("Trade and analysis instruments must match.", nameof(trade));
        if (trade.EntryPrice <= 0m || trade.InitialStopPrice <= 0m ||
            trade.CurrentStopPrice <= 0m || trade.CurrentPrice <= 0m ||
            trade.MinimumPriceIncrement <= 0m || trade.EntryPrice == trade.InitialStopPrice ||
            trade.EntryCommissionPrice < 0m || trade.ExpectedExitCommissionPrice < 0m ||
            trade.SpreadPrice < 0m || trade.SlippagePrice < 0m ||
            trade.InitialQuantity <= 0m || trade.CurrentQuantity <= 0m ||
            trade.CurrentQuantity > trade.InitialQuantity || trade.MinimumQuantityIncrement <= 0m ||
            trade.MaximumFavourableExcursionR < 0m ||
            trade.AnalysisBarsWithoutNewMfe < 0 ||
            trade.StructuralDeteriorationReductionCount < 0 ||
            trade.CompletedReductionStageIds is null)
        {
            throw new ArgumentException("Trade prices, costs, quantities, and initial risk are invalid.", nameof(trade));
        }

        bool initialStopValid = trade.Side switch
        {
            OrderSide.Buy => trade.InitialStopPrice < trade.EntryPrice,
            OrderSide.Sell => trade.InitialStopPrice > trade.EntryPrice,
            _ => false
        };
        if (!initialStopValid)
            throw new ArgumentException("The initial stop is on the wrong side of entry.", nameof(trade));
    }

    private enum ProfitProtectionSource
    {
        ProfitFloor,
        MaximumGiveback
    }

    private sealed record ProfitProtectionFloor(
        decimal LockedR,
        ProfitProtectionSource Source,
        string Explanation);

    private sealed record BreakEvenCalculation(
        decimal CostAdjustedPrice,
        decimal AtrBuffer,
        decimal FinalPrice);

    private sealed record StructuralCandidate(
        decimal Level,
        decimal StopPrice,
        StopAmendmentReason Reason,
        string Source);

    private sealed record OpposingStructure(decimal Level, string Source);

    private sealed record StopCandidate(
        decimal Price,
        decimal LockedR,
        StopAmendmentReason Reason,
        string Source,
        string Explanation,
        decimal? StructuralLevel = null,
        decimal? CostAdjustedBreakEvenPrice = null,
        decimal? AtrBuffer = null);
}
