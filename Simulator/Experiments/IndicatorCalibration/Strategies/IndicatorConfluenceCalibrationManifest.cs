using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// The concrete, hand-authored manifest for <c>IndicatorConfluencePlaybook</c> (blueprint §6, §19
/// Phase 6). <c>TOptions</c> is <see cref="IndicatorConfluenceOptions"/> itself - not the enclosing
/// <see cref="StructuralConfluenceStrategyOptions"/> - matching the compatibility identity already
/// frozen in Phase 1 (<see cref="IndicatorConfluenceCalibrationCompatibility"/>). Every other
/// playbook's settings, every risk/execution/broker/safety/operational setting, and every field of
/// <see cref="StructuralConfluenceStrategyOptions"/> outside <c>IndicatorConfluence</c> are never
/// touched by this manifest - they simply never appear in <see cref="Parameters"/> or
/// <see cref="Ablations"/>, so <see cref="CalibrationCandidate.ApplyTo{TOptions}"/> can never
/// modify them.
/// </summary>
public sealed class IndicatorConfluenceCalibrationManifest : IIndicatorCalibrationManifest<IndicatorConfluenceOptions>
{
    /// <summary>
    /// Mirrors <c>StructuralConfluenceStrategyOptions.MinimumRewardRisk</c>'s default (1.5) for the
    /// reward-to-risk constraint below. This manifest deliberately does not calibrate
    /// <c>MinimumRewardRisk</c> itself (out of the blueprint §6 parameter table), so the constraint
    /// is evaluated against the strategy's configured default rather than a per-request value.
    /// Review before use against a request whose strategy configuration sets a different value.
    /// </summary>
    public const decimal MinimumRewardRisk = 1.5m;

    public int SchemaVersion => 1;
    public string ManifestVersion => "indicator-confluence-manifest-v1";
    public string StrategyId => IndicatorConfluenceCalibrationCompatibility.Instance.StrategyId;
    public string OptionsSchemaVersion => IndicatorConfluenceCalibrationCompatibility.Instance.OptionsSchemaVersion;

    public IReadOnlyList<ICalibrationParameterDescriptor<IndicatorConfluenceOptions>> Parameters { get; } =
    [
        MinimumAdxDescriptor(),
        MaximumRsiForBuyDescriptor(),
        MinimumRsiForSellDescriptor(),
        MinimumConfidenceDescriptor(),
        StopAtrDescriptor(),
        TargetAtrDescriptor()
    ];

    public IReadOnlyList<ICalibrationAblationDescriptor<IndicatorConfluenceOptions>> Ablations { get; } =
    [
        RequireTrendStrengtheningAblation(),
        RequireSqueezeBreakoutAblation()
    ];

    public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
    [
        new CalibrationInteractionGroup { GroupId = "stop-atr-x-target-atr", ParameterIds = ["stop-atr", "target-atr"] },
        new CalibrationInteractionGroup { GroupId = "rsi-buy-x-rsi-sell", ParameterIds = ["maximum-rsi-for-buy", "minimum-rsi-for-sell"] },
        new CalibrationInteractionGroup { GroupId = "adx-x-trend-strengthening", ParameterIds = ["minimum-adx", "require-trend-strengthening"] }
    ];

    public IReadOnlyList<ICalibrationConstraint<IndicatorConfluenceOptions>> Constraints { get; } =
    [
        new CalibrationConstraint<IndicatorConfluenceOptions>(
            "rsi-sell-below-rsi-buy",
            "MinimumRsiForSell must remain strictly below MaximumRsiForBuy (blueprint §6.1).",
            options => options.MinimumRsiForSell < options.MaximumRsiForBuy
                ? CalibrationConstraintResult.Valid
                : CalibrationConstraintResult.Invalid(
                    $"MinimumRsiForSell ({options.MinimumRsiForSell}) must be less than MaximumRsiForBuy ({options.MaximumRsiForBuy}).")),
        new CalibrationConstraint<IndicatorConfluenceOptions>(
            "minimum-reward-risk",
            "TargetAtr/StopAtr must meet the strategy's minimum permitted reward-to-risk ratio (blueprint §6.1).",
            options => options.TargetAtr / options.StopAtr >= MinimumRewardRisk
                ? CalibrationConstraintResult.Valid
                : CalibrationConstraintResult.Invalid(
                    $"TargetAtr/StopAtr ({options.TargetAtr / options.StopAtr:F2}) is below the minimum reward-risk ratio {MinimumRewardRisk:F2}."))
    ];

    /// <summary>
    /// Placeholder-but-workable defaults - review and tune before any production calibration run.
    /// Absolute-quality thresholds (minimum trades, maximum drawdown, minimum expectancy) are a
    /// risk/business decision this manifest cannot make on its own behalf.
    /// </summary>
    public CalibrationScoringPolicy ScoringPolicy { get; } = new()
    {
        PolicyVersion = "indicator-confluence-scoring-v1",
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
        PolicyVersion = "indicator-confluence-acceptance-v1",
        MinimumImprovementOverBaseline = 0.01m,
        MinimumAcceptableFoldPercent = 60m,
        MaximumTrainValidationDegradation = 0.5m,
        MinimumExternalHoldoutExpectancyR = -5m,
        MaximumExternalHoldoutDrawdownR = 15m,
        MinimumExternalHoldoutTrades = 20,
        MinimumPlateauSupport = 50m
    };

    public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);

    private static ICalibrationParameterDescriptor<IndicatorConfluenceOptions> MinimumAdxDescriptor() =>
        new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
            "minimum-adx", "IndicatorConfluence.MinimumAdx",
            CalibrationParameterCategory.Entry, CalibrationValueKind.IndicatorLevel, CalibrationSpacing.Linear,
            defaultValue: 25m, hardMinimum: 5m, hardMaximum: 60m,
            coarseGrid: [15m, 20m, 25m, 30m, 35m], refinementStep: 1m,
            conservativeStartingValue: 30m, permissiveStartingValue: 15m, declaredSearchOrder: 0,
            read: o => o.MinimumAdx, apply: (o, v) => o with { MinimumAdx = v });

    private static ICalibrationParameterDescriptor<IndicatorConfluenceOptions> MaximumRsiForBuyDescriptor() =>
        new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
            "maximum-rsi-for-buy", "IndicatorConfluence.MaximumRsiForBuy",
            CalibrationParameterCategory.Confirmation, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 58m, hardMinimum: 50m, hardMaximum: 90m,
            coarseGrid: [55m, 58m, 60m, 65m, 70m], refinementStep: 1m,
            conservativeStartingValue: 55m, permissiveStartingValue: 70m, declaredSearchOrder: 1,
            read: o => o.MaximumRsiForBuy, apply: (o, v) => o with { MaximumRsiForBuy = v });

    private static ICalibrationParameterDescriptor<IndicatorConfluenceOptions> MinimumRsiForSellDescriptor() =>
        new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
            "minimum-rsi-for-sell", "IndicatorConfluence.MinimumRsiForSell",
            CalibrationParameterCategory.Confirmation, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 42m, hardMinimum: 10m, hardMaximum: 50m,
            coarseGrid: [30m, 35m, 40m, 42m, 45m], refinementStep: 1m,
            conservativeStartingValue: 45m, permissiveStartingValue: 30m, declaredSearchOrder: 2,
            read: o => o.MinimumRsiForSell, apply: (o, v) => o with { MinimumRsiForSell = v });

    private static ICalibrationParameterDescriptor<IndicatorConfluenceOptions> MinimumConfidenceDescriptor() =>
        new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
            "minimum-confidence", "IndicatorConfluence.MinimumConfidence",
            CalibrationParameterCategory.Entry, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 62m, hardMinimum: 0m, hardMaximum: 100m,
            coarseGrid: [55m, 58m, 62m, 65m, 70m], refinementStep: 1m,
            conservativeStartingValue: 70m, permissiveStartingValue: 55m, declaredSearchOrder: 3,
            read: o => o.MinimumConfidence, apply: (o, v) => o with { MinimumConfidence = v });

    private static ICalibrationParameterDescriptor<IndicatorConfluenceOptions> StopAtrDescriptor() =>
        new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
            "stop-atr", "IndicatorConfluence.StopAtr",
            CalibrationParameterCategory.Stop, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 2.0m, hardMinimum: 0.5m, hardMaximum: 4m,
            coarseGrid: [1.5m, 1.75m, 2.0m, 2.25m, 2.5m], refinementStep: 0.1m,
            conservativeStartingValue: 2.0m, permissiveStartingValue: 1.5m, declaredSearchOrder: 4,
            read: o => o.StopAtr, apply: (o, v) => o with { StopAtr = v });

    private static ICalibrationParameterDescriptor<IndicatorConfluenceOptions> TargetAtrDescriptor() =>
        new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
            "target-atr", "IndicatorConfluence.TargetAtr",
            CalibrationParameterCategory.Target, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
            defaultValue: 2.5m, hardMinimum: 1m, hardMaximum: 8m,
            coarseGrid: [2.0m, 2.25m, 2.5m, 3.0m, 3.5m], refinementStep: 0.25m,
            conservativeStartingValue: 2.5m, permissiveStartingValue: 3.5m, declaredSearchOrder: 5,
            read: o => o.TargetAtr, apply: (o, v) => o with { TargetAtr = v });

    private static ICalibrationAblationDescriptor<IndicatorConfluenceOptions> RequireTrendStrengtheningAblation() =>
        new CalibrationAblationDescriptor<IndicatorConfluenceOptions>(
            "require-trend-strengthening", "IndicatorConfluence.RequireTrendStrengthening", defaultValue: true,
            read: o => o.RequireTrendStrengthening, apply: (o, v) => o with { RequireTrendStrengthening = v });

    private static ICalibrationAblationDescriptor<IndicatorConfluenceOptions> RequireSqueezeBreakoutAblation() =>
        new CalibrationAblationDescriptor<IndicatorConfluenceOptions>(
            "require-squeeze-breakout", "IndicatorConfluence.RequireSqueezeBreakout", defaultValue: true,
            read: o => o.RequireSqueezeBreakout, apply: (o, v) => o with { RequireSqueezeBreakout = v });
}
