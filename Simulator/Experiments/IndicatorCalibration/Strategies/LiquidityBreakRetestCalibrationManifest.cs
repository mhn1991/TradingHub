using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// The concrete, hand-authored manifest for <c>LiquidityBreakRetestPlaybook</c> (blueprint §19
/// Phase 8 - the second calibration-enabled strategy, delivered only after Phase 6/7's first
/// vertical slice was reviewed). <c>TOptions</c> is <see cref="LiquidityBreakRetestOptions"/>
/// itself, matching the compatibility identity frozen in
/// <see cref="LiquidityBreakRetestCalibrationCompatibility"/>. Unlike
/// <c>IndicatorConfluenceOptions</c>, this playbook has no plain-ATR stop/target of its own (its
/// stop/target snap to nearby structure via the shared <c>StructuralGeometryOptions</c>, which is
/// out of scope here since it's shared across every playbook, not specific to this one) - so this
/// manifest has no Stop/Target category parameter, which is expected, not a gap.
/// </summary>
public sealed class LiquidityBreakRetestCalibrationManifest : IIndicatorCalibrationManifest<LiquidityBreakRetestOptions>
{
    public int SchemaVersion => 1;
    public string ManifestVersion => "liquidity-break-retest-manifest-v1";
    public string StrategyId => LiquidityBreakRetestCalibrationCompatibility.Instance.StrategyId;
    public string OptionsSchemaVersion => LiquidityBreakRetestCalibrationCompatibility.Instance.OptionsSchemaVersion;

    public IReadOnlyList<ICalibrationParameterDescriptor<LiquidityBreakRetestOptions>> Parameters { get; } =
    [
        MinimumPoolQualityDescriptor(),
        MaximumBarsSinceAcceptedBreakDescriptor(),
        MinimumDisplacementAtrDescriptor(),
        MaximumRetestDistanceAtrDescriptor(),
        MinimumAdxDescriptor(),
        MinimumConfidenceDescriptor()
    ];

    public IReadOnlyList<ICalibrationAblationDescriptor<LiquidityBreakRetestOptions>> Ablations { get; } =
    [
        RequireRetestEventAblation(),
        RequirePriceActionTriggerAblation()
    ];

    public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
    [
        new CalibrationInteractionGroup
        {
            GroupId = "displacement-x-retest-distance",
            ParameterIds = ["minimum-displacement-atr", "maximum-retest-distance-atr"]
        },
        new CalibrationInteractionGroup
        {
            GroupId = "adx-x-require-retest-event",
            ParameterIds = ["minimum-adx", "require-retest-event"]
        },
        new CalibrationInteractionGroup
        {
            GroupId = "pool-quality-x-confidence",
            ParameterIds = ["minimum-pool-quality", "minimum-confidence"]
        }
    ];

    public IReadOnlyList<ICalibrationConstraint<LiquidityBreakRetestOptions>> Constraints { get; } =
    [
        new CalibrationConstraint<LiquidityBreakRetestOptions>(
            "retest-distance-below-displacement",
            "A retest that travels further than the original breakout displacement is a reversal, not a retest.",
            options => options.MaximumRetestDistanceAtr < options.MinimumDisplacementAtr
                ? CalibrationConstraintResult.Valid
                : CalibrationConstraintResult.Invalid(
                    $"MaximumRetestDistanceAtr ({options.MaximumRetestDistanceAtr}) must be less than " +
                    $"MinimumDisplacementAtr ({options.MinimumDisplacementAtr})."))
    ];

    /// <summary>Placeholder-but-workable defaults - review and tune before any production calibration run.</summary>
    public CalibrationScoringPolicy ScoringPolicy { get; } = new()
    {
        PolicyVersion = "liquidity-break-retest-scoring-v1",
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
        PolicyVersion = "liquidity-break-retest-acceptance-v1",
        MinimumImprovementOverBaseline = 0.01m,
        MinimumAcceptableFoldPercent = 60m,
        MaximumTrainValidationDegradation = 0.5m,
        MinimumExternalHoldoutExpectancyR = -5m,
        MaximumExternalHoldoutDrawdownR = 15m,
        MinimumExternalHoldoutTrades = 20,
        MinimumPlateauSupport = 50m
    };

    public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);

    private static ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> MinimumPoolQualityDescriptor() =>
        new CalibrationParameterDescriptor<LiquidityBreakRetestOptions>(
            "minimum-pool-quality", "LiquidityBreakRetest.MinimumPoolQuality",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToOne, CalibrationSpacing.Linear,
            defaultValue: 0.55m, hardMinimum: 0.1m, hardMaximum: 0.95m,
            coarseGrid: [0.40m, 0.45m, 0.55m, 0.60m, 0.65m], refinementStep: 0.02m,
            conservativeStartingValue: 0.65m, permissiveStartingValue: 0.40m, declaredSearchOrder: 0,
            read: o => o.MinimumPoolQuality, apply: (o, v) => o with { MinimumPoolQuality = v });

    private static ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> MaximumBarsSinceAcceptedBreakDescriptor() =>
        new CalibrationParameterDescriptor<LiquidityBreakRetestOptions>(
            "maximum-bars-since-accepted-break", "LiquidityBreakRetest.MaximumBarsSinceAcceptedBreak",
            CalibrationParameterCategory.Entry, CalibrationValueKind.Count, CalibrationSpacing.Linear,
            defaultValue: 20m, hardMinimum: 5m, hardMaximum: 60m,
            coarseGrid: [10m, 15m, 20m, 25m, 30m], refinementStep: 1m,
            conservativeStartingValue: 15m, permissiveStartingValue: 30m, declaredSearchOrder: 1,
            read: o => o.MaximumBarsSinceAcceptedBreak,
            apply: (o, v) => o with { MaximumBarsSinceAcceptedBreak = (int)v });

    private static ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> MinimumDisplacementAtrDescriptor() =>
        new CalibrationParameterDescriptor<LiquidityBreakRetestOptions>(
            "minimum-displacement-atr", "LiquidityBreakRetest.MinimumDisplacementAtr",
            CalibrationParameterCategory.Entry, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 0.5m, hardMinimum: 0.15m, hardMaximum: 2m,
            coarseGrid: [0.25m, 0.4m, 0.5m, 0.65m, 0.8m], refinementStep: 0.05m,
            conservativeStartingValue: 0.8m, permissiveStartingValue: 0.25m, declaredSearchOrder: 2,
            read: o => o.MinimumDisplacementAtr, apply: (o, v) => o with { MinimumDisplacementAtr = v });

    private static ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> MaximumRetestDistanceAtrDescriptor() =>
        new CalibrationParameterDescriptor<LiquidityBreakRetestOptions>(
            "maximum-retest-distance-atr", "LiquidityBreakRetest.MaximumRetestDistanceAtr",
            CalibrationParameterCategory.Entry, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 0.35m, hardMinimum: 0.1m, hardMaximum: 1.5m,
            coarseGrid: [0.2m, 0.3m, 0.35m, 0.45m, 0.55m], refinementStep: 0.05m,
            conservativeStartingValue: 0.2m, permissiveStartingValue: 0.55m, declaredSearchOrder: 3,
            read: o => o.MaximumRetestDistanceAtr, apply: (o, v) => o with { MaximumRetestDistanceAtr = v });

    private static ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> MinimumAdxDescriptor() =>
        new CalibrationParameterDescriptor<LiquidityBreakRetestOptions>(
            "minimum-adx", "LiquidityBreakRetest.MinimumAdx",
            CalibrationParameterCategory.Entry, CalibrationValueKind.IndicatorLevel, CalibrationSpacing.Linear,
            defaultValue: 18m, hardMinimum: 5m, hardMaximum: 50m,
            coarseGrid: [12m, 15m, 18m, 22m, 26m], refinementStep: 1m,
            conservativeStartingValue: 26m, permissiveStartingValue: 12m, declaredSearchOrder: 4,
            read: o => o.MinimumAdx, apply: (o, v) => o with { MinimumAdx = v });

    private static ICalibrationParameterDescriptor<LiquidityBreakRetestOptions> MinimumConfidenceDescriptor() =>
        new CalibrationParameterDescriptor<LiquidityBreakRetestOptions>(
            "minimum-confidence", "LiquidityBreakRetest.MinimumConfidence",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 55m, hardMinimum: 0m, hardMaximum: 100m,
            coarseGrid: [45m, 50m, 55m, 60m, 65m], refinementStep: 1m,
            conservativeStartingValue: 65m, permissiveStartingValue: 45m, declaredSearchOrder: 5,
            read: o => o.MinimumConfidence, apply: (o, v) => o with { MinimumConfidence = v });

    private static ICalibrationAblationDescriptor<LiquidityBreakRetestOptions> RequireRetestEventAblation() =>
        new CalibrationAblationDescriptor<LiquidityBreakRetestOptions>(
            "require-retest-event", "LiquidityBreakRetest.RequireRetestEvent", defaultValue: true,
            read: o => o.RequireRetestEvent, apply: (o, v) => o with { RequireRetestEvent = v });

    private static ICalibrationAblationDescriptor<LiquidityBreakRetestOptions> RequirePriceActionTriggerAblation() =>
        new CalibrationAblationDescriptor<LiquidityBreakRetestOptions>(
            "require-price-action-trigger", "LiquidityBreakRetest.RequirePriceActionTrigger", defaultValue: true,
            read: o => o.RequirePriceActionTrigger, apply: (o, v) => o with { RequirePriceActionTrigger = v });
}
