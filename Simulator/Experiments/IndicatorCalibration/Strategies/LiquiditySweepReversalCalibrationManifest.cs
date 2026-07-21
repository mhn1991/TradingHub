using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// The concrete, hand-authored manifest for <c>LiquiditySweepReversalPlaybook</c>, mirroring
/// <see cref="LiquidityBreakRetestCalibrationManifest"/>'s shape. <c>TOptions</c> is
/// <see cref="LiquiditySweepReversalOptions"/> itself, matching the compatibility identity in
/// <see cref="LiquiditySweepReversalCalibrationCompatibility"/>. Categorical fields
/// (<c>AllowedPoolTypes</c>, <c>SupplyDemandConfluence</c>, <c>CciMode</c>) are out of scope for this
/// manifest - they're discrete/structural choices, not continuous thresholds this search engine can
/// coordinate-descend over.
/// </summary>
public sealed class LiquiditySweepReversalCalibrationManifest : IIndicatorCalibrationManifest<LiquiditySweepReversalOptions>
{
    public int SchemaVersion => 1;
    public string ManifestVersion => "liquidity-sweep-reversal-manifest-v1";
    public string StrategyId => LiquiditySweepReversalCalibrationCompatibility.Instance.StrategyId;
    public string OptionsSchemaVersion => LiquiditySweepReversalCalibrationCompatibility.Instance.OptionsSchemaVersion;

    public IReadOnlyList<ICalibrationParameterDescriptor<LiquiditySweepReversalOptions>> Parameters { get; } =
    [
        MinimumPoolQualityDescriptor(),
        MinimumSweepPenetrationAtrDescriptor(),
        MaximumSweepPenetrationAtrDescriptor(),
        MaximumBarsSinceSweepDescriptor(),
        MaximumZonePoolDistanceAtrDescriptor(),
        MinimumReclaimBodyRatioDescriptor(),
        MinimumConfidenceDescriptor()
    ];

    public IReadOnlyList<ICalibrationAblationDescriptor<LiquiditySweepReversalOptions>> Ablations { get; } =
    [
        RequireClosedBackInsideAblation(),
        RequirePriceActionTriggerAblation()
    ];

    public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
    [
        new CalibrationInteractionGroup
        {
            GroupId = "sweep-penetration-bounds",
            ParameterIds = ["minimum-sweep-penetration-atr", "maximum-sweep-penetration-atr"]
        },
        new CalibrationInteractionGroup
        {
            GroupId = "pool-quality-x-confidence",
            ParameterIds = ["minimum-pool-quality", "minimum-confidence"]
        },
        new CalibrationInteractionGroup
        {
            GroupId = "reclaim-x-zone-distance",
            ParameterIds = ["minimum-reclaim-body-ratio", "maximum-zone-pool-distance-atr"]
        }
    ];

    public IReadOnlyList<ICalibrationConstraint<LiquiditySweepReversalOptions>> Constraints { get; } =
    [
        new CalibrationConstraint<LiquiditySweepReversalOptions>(
            "sweep-penetration-max-above-min",
            "MaximumSweepPenetrationAtr must remain above MinimumSweepPenetrationAtr (matches LiquiditySweepReversalOptions.Validate()).",
            options => options.MaximumSweepPenetrationAtr >= options.MinimumSweepPenetrationAtr
                ? CalibrationConstraintResult.Valid
                : CalibrationConstraintResult.Invalid(
                    $"MaximumSweepPenetrationAtr ({options.MaximumSweepPenetrationAtr}) must be >= " +
                    $"MinimumSweepPenetrationAtr ({options.MinimumSweepPenetrationAtr})."))
    ];

    /// <summary>Placeholder-but-workable defaults - review and tune before any production calibration run.</summary>
    public CalibrationScoringPolicy ScoringPolicy { get; } = new()
    {
        PolicyVersion = "liquidity-sweep-reversal-scoring-v1",
        ObjectiveId = CandidateScorer.MedianExpectancyDrawdownPenalizedObjective,
        MinimumTradesPerFold = 20,
        MaximumDrawdownR = 15m,
        MinimumMedianExpectancyR = -5m,
        MinimumProfitFactor = 0m,
        DrawdownPenaltyWeight = 0.1m,
        TurnoverPenaltyWeight = 0.02m
    };

    public CalibrationAcceptancePolicy AcceptancePolicy { get; } = new()
    {
        PolicyVersion = "liquidity-sweep-reversal-acceptance-v1",
        MinimumImprovementOverBaseline = 0.01m,
        MinimumAcceptableFoldPercent = 60m,
        MaximumTrainValidationDegradation = 0.5m,
        MinimumExternalHoldoutExpectancyR = -5m,
        MaximumExternalHoldoutDrawdownR = 15m,
        MinimumExternalHoldoutTrades = 20,
        MinimumPlateauSupport = 50m
    };

    public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MinimumPoolQualityDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "minimum-pool-quality", "LiquiditySweepReversal.MinimumPoolQuality",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToOne, CalibrationSpacing.Linear,
            defaultValue: 0.55m, hardMinimum: 0.1m, hardMaximum: 0.95m,
            coarseGrid: [0.40m, 0.45m, 0.55m, 0.60m, 0.65m], refinementStep: 0.02m,
            conservativeStartingValue: 0.65m, permissiveStartingValue: 0.40m, declaredSearchOrder: 0,
            read: o => o.MinimumPoolQuality, apply: (o, v) => o with { MinimumPoolQuality = v });

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MinimumSweepPenetrationAtrDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "minimum-sweep-penetration-atr", "LiquiditySweepReversal.MinimumSweepPenetrationAtr",
            CalibrationParameterCategory.Entry, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 0.05m, hardMinimum: 0.01m, hardMaximum: 0.5m,
            coarseGrid: [0.02m, 0.03m, 0.05m, 0.08m, 0.12m], refinementStep: 0.01m,
            conservativeStartingValue: 0.08m, permissiveStartingValue: 0.02m, declaredSearchOrder: 1,
            read: o => o.MinimumSweepPenetrationAtr, apply: (o, v) => o with { MinimumSweepPenetrationAtr = v });

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MaximumSweepPenetrationAtrDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "maximum-sweep-penetration-atr", "LiquiditySweepReversal.MaximumSweepPenetrationAtr",
            CalibrationParameterCategory.Entry, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 1.25m, hardMinimum: 0.5m, hardMaximum: 3m,
            coarseGrid: [0.9m, 1.05m, 1.25m, 1.5m, 1.75m], refinementStep: 0.05m,
            conservativeStartingValue: 0.9m, permissiveStartingValue: 1.75m, declaredSearchOrder: 2,
            read: o => o.MaximumSweepPenetrationAtr, apply: (o, v) => o with { MaximumSweepPenetrationAtr = v });

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MaximumBarsSinceSweepDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "maximum-bars-since-sweep", "LiquiditySweepReversal.MaximumBarsSinceSweep",
            CalibrationParameterCategory.Entry, CalibrationValueKind.Count, CalibrationSpacing.Linear,
            defaultValue: 12m, hardMinimum: 3m, hardMaximum: 40m,
            coarseGrid: [8m, 10m, 12m, 15m, 18m], refinementStep: 1m,
            conservativeStartingValue: 8m, permissiveStartingValue: 18m, declaredSearchOrder: 3,
            read: o => o.MaximumBarsSinceSweep, apply: (o, v) => o with { MaximumBarsSinceSweep = (int)v });

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MaximumZonePoolDistanceAtrDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "maximum-zone-pool-distance-atr", "LiquiditySweepReversal.MaximumZonePoolDistanceAtr",
            CalibrationParameterCategory.Confirmation, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 0.5m, hardMinimum: 0.1m, hardMaximum: 1.5m,
            coarseGrid: [0.35m, 0.4m, 0.5m, 0.6m, 0.7m], refinementStep: 0.05m,
            conservativeStartingValue: 0.35m, permissiveStartingValue: 0.7m, declaredSearchOrder: 4,
            read: o => o.MaximumZonePoolDistanceAtr, apply: (o, v) => o with { MaximumZonePoolDistanceAtr = v });

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MinimumReclaimBodyRatioDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "minimum-reclaim-body-ratio", "LiquiditySweepReversal.MinimumReclaimBodyRatio",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToOne, CalibrationSpacing.Linear,
            defaultValue: 0.25m, hardMinimum: 0.05m, hardMaximum: 0.75m,
            coarseGrid: [0.15m, 0.2m, 0.25m, 0.35m, 0.45m], refinementStep: 0.02m,
            conservativeStartingValue: 0.45m, permissiveStartingValue: 0.15m, declaredSearchOrder: 5,
            read: o => o.MinimumReclaimBodyRatio, apply: (o, v) => o with { MinimumReclaimBodyRatio = v });

    private static ICalibrationParameterDescriptor<LiquiditySweepReversalOptions> MinimumConfidenceDescriptor() =>
        new CalibrationParameterDescriptor<LiquiditySweepReversalOptions>(
            "minimum-confidence", "LiquiditySweepReversal.MinimumConfidence",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 55m, hardMinimum: 0m, hardMaximum: 100m,
            coarseGrid: [45m, 50m, 55m, 60m, 65m], refinementStep: 1m,
            conservativeStartingValue: 65m, permissiveStartingValue: 45m, declaredSearchOrder: 6,
            read: o => o.MinimumConfidence, apply: (o, v) => o with { MinimumConfidence = v });

    private static ICalibrationAblationDescriptor<LiquiditySweepReversalOptions> RequireClosedBackInsideAblation() =>
        new CalibrationAblationDescriptor<LiquiditySweepReversalOptions>(
            "require-closed-back-inside", "LiquiditySweepReversal.RequireClosedBackInside", defaultValue: true,
            read: o => o.RequireClosedBackInside, apply: (o, v) => o with { RequireClosedBackInside = v });

    private static ICalibrationAblationDescriptor<LiquiditySweepReversalOptions> RequirePriceActionTriggerAblation() =>
        new CalibrationAblationDescriptor<LiquiditySweepReversalOptions>(
            "require-price-action-trigger", "LiquiditySweepReversal.RequirePriceActionTrigger", defaultValue: true,
            read: o => o.RequirePriceActionTrigger, apply: (o, v) => o with { RequirePriceActionTrigger = v });
}
