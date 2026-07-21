using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// The concrete, hand-authored manifest for <c>SupplyDemandPullbackPlaybook</c>, mirroring
/// <see cref="LiquidityBreakRetestCalibrationManifest"/>'s shape. <c>TOptions</c> is
/// <see cref="SupplyDemandPullbackOptions"/> itself, matching the compatibility identity in
/// <see cref="SupplyDemandPullbackCalibrationCompatibility"/>. Categorical fields
/// (<c>PermittedZoneStates</c>, <c>CciMode</c>) are out of scope for this manifest - discrete
/// structural choices, not continuous thresholds. <c>AllowRsiAlternativeConfirmation</c>/
/// <c>AllowBollingerReEntryConfirmation</c> are left uncalibrated too (minor secondary-confirmation
/// toggles, not the playbook's primary gates) to keep this manifest's scope comparable to the other
/// three playbooks' manifests rather than maximal.
/// </summary>
public sealed class SupplyDemandPullbackCalibrationManifest : IIndicatorCalibrationManifest<SupplyDemandPullbackOptions>
{
    public int SchemaVersion => 1;
    public string ManifestVersion => "supply-demand-pullback-manifest-v1";
    public string StrategyId => SupplyDemandPullbackCalibrationCompatibility.Instance.StrategyId;
    public string OptionsSchemaVersion => SupplyDemandPullbackCalibrationCompatibility.Instance.OptionsSchemaVersion;

    public IReadOnlyList<ICalibrationParameterDescriptor<SupplyDemandPullbackOptions>> Parameters { get; } =
    [
        MinimumZoneQualityDescriptor(),
        MaximumPriorTouchesDescriptor(),
        MaximumPenetrationRatioDescriptor(),
        MinimumConfidenceDescriptor()
    ];

    public IReadOnlyList<ICalibrationAblationDescriptor<SupplyDemandPullbackOptions>> Ablations { get; } =
    [
        RequireTrendAlignmentAblation(),
        AllowRangeBoundaryContextAblation()
    ];

    public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
    [
        new CalibrationInteractionGroup
        {
            GroupId = "zone-quality-x-confidence",
            ParameterIds = ["minimum-zone-quality", "minimum-confidence"]
        },
        new CalibrationInteractionGroup
        {
            GroupId = "prior-touches-x-penetration-ratio",
            ParameterIds = ["maximum-prior-touches", "maximum-penetration-ratio"]
        }
    ];

    public IReadOnlyList<ICalibrationConstraint<SupplyDemandPullbackOptions>> Constraints { get; } = [];

    /// <summary>Placeholder-but-workable defaults - review and tune before any production calibration run.</summary>
    public CalibrationScoringPolicy ScoringPolicy { get; } = new()
    {
        PolicyVersion = "supply-demand-pullback-scoring-v1",
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
        PolicyVersion = "supply-demand-pullback-acceptance-v1",
        MinimumImprovementOverBaseline = 0.01m,
        MinimumAcceptableFoldPercent = 60m,
        MaximumTrainValidationDegradation = 0.5m,
        MinimumExternalHoldoutExpectancyR = -5m,
        MaximumExternalHoldoutDrawdownR = 15m,
        MinimumExternalHoldoutTrades = 20,
        MinimumPlateauSupport = 50m
    };

    public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);

    private static ICalibrationParameterDescriptor<SupplyDemandPullbackOptions> MinimumZoneQualityDescriptor() =>
        new CalibrationParameterDescriptor<SupplyDemandPullbackOptions>(
            "minimum-zone-quality", "SupplyDemandPullback.MinimumZoneQuality",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToOne, CalibrationSpacing.Linear,
            defaultValue: 0.55m, hardMinimum: 0.1m, hardMaximum: 0.95m,
            coarseGrid: [0.40m, 0.45m, 0.55m, 0.60m, 0.65m], refinementStep: 0.02m,
            conservativeStartingValue: 0.65m, permissiveStartingValue: 0.40m, declaredSearchOrder: 0,
            read: o => o.MinimumZoneQuality, apply: (o, v) => o with { MinimumZoneQuality = v });

    private static ICalibrationParameterDescriptor<SupplyDemandPullbackOptions> MaximumPriorTouchesDescriptor() =>
        new CalibrationParameterDescriptor<SupplyDemandPullbackOptions>(
            "maximum-prior-touches", "SupplyDemandPullback.MaximumPriorTouches",
            CalibrationParameterCategory.Entry, CalibrationValueKind.Count, CalibrationSpacing.Linear,
            defaultValue: 1m, hardMinimum: 0m, hardMaximum: 5m,
            coarseGrid: [0m, 1m, 2m, 3m, 4m], refinementStep: 1m,
            conservativeStartingValue: 0m, permissiveStartingValue: 3m, declaredSearchOrder: 1,
            read: o => o.MaximumPriorTouches, apply: (o, v) => o with { MaximumPriorTouches = (int)v });

    private static ICalibrationParameterDescriptor<SupplyDemandPullbackOptions> MaximumPenetrationRatioDescriptor() =>
        new CalibrationParameterDescriptor<SupplyDemandPullbackOptions>(
            "maximum-penetration-ratio", "SupplyDemandPullback.MaximumPenetrationRatio",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToOne, CalibrationSpacing.Linear,
            defaultValue: 0.65m, hardMinimum: 0.1m, hardMaximum: 0.95m,
            coarseGrid: [0.45m, 0.55m, 0.65m, 0.75m, 0.85m], refinementStep: 0.02m,
            conservativeStartingValue: 0.45m, permissiveStartingValue: 0.85m, declaredSearchOrder: 2,
            read: o => o.MaximumPenetrationRatio, apply: (o, v) => o with { MaximumPenetrationRatio = v });

    private static ICalibrationParameterDescriptor<SupplyDemandPullbackOptions> MinimumConfidenceDescriptor() =>
        new CalibrationParameterDescriptor<SupplyDemandPullbackOptions>(
            "minimum-confidence", "SupplyDemandPullback.MinimumConfidence",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 55m, hardMinimum: 0m, hardMaximum: 100m,
            coarseGrid: [45m, 50m, 55m, 60m, 65m], refinementStep: 1m,
            conservativeStartingValue: 65m, permissiveStartingValue: 45m, declaredSearchOrder: 3,
            read: o => o.MinimumConfidence, apply: (o, v) => o with { MinimumConfidence = v });

    private static ICalibrationAblationDescriptor<SupplyDemandPullbackOptions> RequireTrendAlignmentAblation() =>
        new CalibrationAblationDescriptor<SupplyDemandPullbackOptions>(
            "require-trend-alignment", "SupplyDemandPullback.RequireTrendAlignment", defaultValue: true,
            read: o => o.RequireTrendAlignment, apply: (o, v) => o with { RequireTrendAlignment = v });

    private static ICalibrationAblationDescriptor<SupplyDemandPullbackOptions> AllowRangeBoundaryContextAblation() =>
        new CalibrationAblationDescriptor<SupplyDemandPullbackOptions>(
            "allow-range-boundary-context", "SupplyDemandPullback.AllowRangeBoundaryContext", defaultValue: false,
            read: o => o.AllowRangeBoundaryContext, apply: (o, v) => o with { AllowRangeBoundaryContext = v });
}
