using Simulator.Calibration;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 2 gate + §18.1 unit-test list: manifest validation, typed parameter
/// application, nested options application, cross-parameter constraints, artifact compatibility,
/// approval checks. Uses a small synthetic options type deliberately - the concrete
/// IndicatorConfluenceOptions manifest is Phase 6 scope; this phase only proves the generic
/// machinery every future manifest will build on.
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationManifestModelTests
{
    private sealed record NestedOptions
    {
        public decimal SomeThreshold { get; init; } = 50m;
    }

    private sealed record SyntheticOptions
    {
        public decimal Level { get; init; } = 20m;
        public bool RequireStrengthening { get; init; }
        public NestedOptions Nested { get; init; } = new();
    }

    private static ICalibrationParameterDescriptor<SyntheticOptions> LevelDescriptor() =>
        new CalibrationParameterDescriptor<SyntheticOptions>(
            parameterId: "level",
            diagnosticPath: "Level",
            category: CalibrationParameterCategory.Entry,
            valueKind: CalibrationValueKind.IndicatorLevel,
            spacing: CalibrationSpacing.Linear,
            defaultValue: 20m,
            hardMinimum: 10m,
            hardMaximum: 40m,
            coarseGrid: [15m, 20m, 25m, 30m, 35m],
            refinementStep: 1m,
            conservativeStartingValue: 15m,
            permissiveStartingValue: 30m,
            declaredSearchOrder: 0,
            read: options => options.Level,
            apply: (options, value) => options with { Level = value });

    private static ICalibrationParameterDescriptor<SyntheticOptions> NestedThresholdDescriptor() =>
        new CalibrationParameterDescriptor<SyntheticOptions>(
            parameterId: "nested-threshold",
            diagnosticPath: "Nested.SomeThreshold",
            category: CalibrationParameterCategory.Confirmation,
            valueKind: CalibrationValueKind.PercentageZeroToHundred,
            spacing: CalibrationSpacing.Linear,
            defaultValue: 50m,
            hardMinimum: 30m,
            hardMaximum: 70m,
            coarseGrid: [40m, 50m, 60m],
            refinementStep: 2m,
            conservativeStartingValue: 40m,
            permissiveStartingValue: 60m,
            declaredSearchOrder: 1,
            read: options => options.Nested.SomeThreshold,
            apply: (options, value) => options with { Nested = options.Nested with { SomeThreshold = value } });

    private static ICalibrationAblationDescriptor<SyntheticOptions> StrengtheningAblation() =>
        new CalibrationAblationDescriptor<SyntheticOptions>(
            parameterId: "require-strengthening",
            diagnosticPath: "RequireStrengthening",
            defaultValue: false,
            read: options => options.RequireStrengthening,
            apply: (options, value) => options with { RequireStrengthening = value });

    [Test]
    public void ParameterDescriptor_ReadApply_RoundTripsThroughCompiledDelegates()
    {
        ICalibrationParameterDescriptor<SyntheticOptions> descriptor = LevelDescriptor();
        var options = new SyntheticOptions();
        Assert.That(descriptor.Read(options), Is.EqualTo(20m));

        SyntheticOptions updated = descriptor.Apply(options, 25m);
        Assert.That(descriptor.Read(updated), Is.EqualTo(25m));
        Assert.That(options.Level, Is.EqualTo(20m), "Apply must not mutate the original immutable record.");
    }

    [Test]
    public void ParameterDescriptor_ApplyOutOfBounds_Throws()
    {
        ICalibrationParameterDescriptor<SyntheticOptions> descriptor = LevelDescriptor();
        Assert.Throws<ArgumentOutOfRangeException>(() => descriptor.Apply(new SyntheticOptions(), 5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => descriptor.Apply(new SyntheticOptions(), 45m));
    }

    [Test]
    public void ParameterDescriptor_NestedRecordApply_ProducesCorrectNestedWithExpression()
    {
        ICalibrationParameterDescriptor<SyntheticOptions> descriptor = NestedThresholdDescriptor();
        SyntheticOptions updated = descriptor.Apply(new SyntheticOptions(), 65m);
        Assert.That(updated.Nested.SomeThreshold, Is.EqualTo(65m));
        Assert.That(updated.Level, Is.EqualTo(20m), "Applying a nested parameter must not disturb sibling fields.");
    }

    [Test]
    public void ParameterDescriptorConstructor_DefaultOutsideBounds_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CalibrationParameterDescriptor<SyntheticOptions>(
            "x", "X", CalibrationParameterCategory.Entry, CalibrationValueKind.Ratio, CalibrationSpacing.Linear,
            defaultValue: 5m, hardMinimum: 10m, hardMaximum: 20m,
            coarseGrid: [12m, 15m], refinementStep: 1m,
            conservativeStartingValue: 12m, permissiveStartingValue: 18m, declaredSearchOrder: 0,
            read: o => 0m, apply: (o, v) => o));
    }

    [Test]
    public void ParameterDescriptorConstructor_CoarseGridValueOutsideBounds_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CalibrationParameterDescriptor<SyntheticOptions>(
            "x", "X", CalibrationParameterCategory.Entry, CalibrationValueKind.Ratio, CalibrationSpacing.Linear,
            defaultValue: 15m, hardMinimum: 10m, hardMaximum: 20m,
            coarseGrid: [12m, 25m], refinementStep: 1m,
            conservativeStartingValue: 12m, permissiveStartingValue: 18m, declaredSearchOrder: 0,
            read: o => 0m, apply: (o, v) => o));
    }

    [Test]
    public void AblationDescriptor_ReadApply_RoundTrips()
    {
        ICalibrationAblationDescriptor<SyntheticOptions> ablation = StrengtheningAblation();
        var options = new SyntheticOptions();
        Assert.That(ablation.Read(options), Is.False);
        SyntheticOptions updated = ablation.Apply(options, true);
        Assert.That(ablation.Read(updated), Is.True);
        Assert.That(ablation.Read(options), Is.False, "Apply must not mutate the original.");
    }

    [Test]
    public void Constraint_InvalidCandidate_ReportsReason()
    {
        var constraint = new CalibrationConstraint<SyntheticOptions>(
            "level-below-threshold",
            "Level must remain below the nested threshold.",
            options => options.Level < options.Nested.SomeThreshold
                ? CalibrationConstraintResult.Valid
                : CalibrationConstraintResult.Invalid("Level must be less than Nested.SomeThreshold."));

        CalibrationConstraintResult valid = constraint.Validate(new SyntheticOptions { Level = 20m });
        Assert.That(valid.IsValid, Is.True);

        CalibrationConstraintResult invalid = constraint.Validate(
            new SyntheticOptions { Level = 60m, Nested = new NestedOptions { SomeThreshold = 50m } });
        Assert.That(invalid.IsValid, Is.False);
        Assert.That(invalid.RejectionReason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void InteractionGroup_ThreeParametersWithoutOptIn_Throws()
    {
        var group = new CalibrationInteractionGroup
        {
            GroupId = "triple",
            ParameterIds = ["a", "b", "c"],
            ExplicitOptIn = false
        };
        Assert.Throws<ArgumentException>(group.Validate);
    }

    [Test]
    public void InteractionGroup_ThreeParametersWithOptIn_Valid()
    {
        var group = new CalibrationInteractionGroup
        {
            GroupId = "triple",
            ParameterIds = ["a", "b", "c"],
            ExplicitOptIn = true
        };
        Assert.DoesNotThrow(group.Validate);
    }

    [Test]
    public void InteractionGroup_DuplicateParameterIds_Throws()
    {
        var group = new CalibrationInteractionGroup { GroupId = "dup", ParameterIds = ["a", "a"] };
        Assert.Throws<ArgumentException>(group.Validate);
    }

    private sealed class SyntheticManifest : IIndicatorCalibrationManifest<SyntheticOptions>
    {
        public int SchemaVersion => 1;
        public string ManifestVersion => "synthetic-manifest-v1";
        public string StrategyId => "synthetic-strategy";
        public string OptionsSchemaVersion => "synthetic-options-v1";

        public IReadOnlyList<ICalibrationParameterDescriptor<SyntheticOptions>> Parameters { get; } =
            [LevelDescriptor(), NestedThresholdDescriptor()];
        public IReadOnlyList<ICalibrationAblationDescriptor<SyntheticOptions>> Ablations { get; } =
            [StrengtheningAblation()];
        public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
        [
            new CalibrationInteractionGroup { GroupId = "level-x-nested", ParameterIds = ["level", "nested-threshold"] }
        ];
        public IReadOnlyList<ICalibrationConstraint<SyntheticOptions>> Constraints { get; } = [];

        public CalibrationScoringPolicy ScoringPolicy { get; } = new()
        {
            PolicyVersion = "v1",
            ObjectiveId = "median-expectancy",
            MinimumTradesPerFold = 20,
            MaximumDrawdownR = 10m,
            MinimumMedianExpectancyR = 0m,
            MinimumProfitFactor = 1m,
            DrawdownPenaltyWeight = 0.1m,
            TurnoverPenaltyWeight = 0.05m
        };

        public CalibrationAcceptancePolicy AcceptancePolicy { get; } = new()
        {
            PolicyVersion = "v1",
            MinimumImprovementOverBaseline = 0.01m,
            MinimumAcceptableFoldPercent = 60m,
            MaximumTrainValidationDegradation = 0.5m,
            MinimumExternalHoldoutExpectancyR = 0m,
            MaximumExternalHoldoutDrawdownR = 10m,
            MinimumExternalHoldoutTrades = 20,
            MinimumPlateauSupport = 50m
        };

        public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);
    }

    [Test]
    public void Manifest_WellFormed_ValidatesWithoutThrowing()
    {
        Assert.DoesNotThrow(new SyntheticManifest().Validate);
    }

    [Test]
    public void Manifest_InteractionGroupReferencesUndeclaredParameter_Throws()
    {
        var manifest = new BrokenManifest();
        Assert.Throws<ArgumentException>(manifest.Validate);
    }

    private sealed class BrokenManifest : IIndicatorCalibrationManifest<SyntheticOptions>
    {
        public int SchemaVersion => 1;
        public string ManifestVersion => "broken-v1";
        public string StrategyId => "synthetic-strategy";
        public string OptionsSchemaVersion => "synthetic-options-v1";
        public IReadOnlyList<ICalibrationParameterDescriptor<SyntheticOptions>> Parameters { get; } = [LevelDescriptor()];
        public IReadOnlyList<ICalibrationAblationDescriptor<SyntheticOptions>> Ablations { get; } = [];
        public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
        [
            new CalibrationInteractionGroup { GroupId = "bad", ParameterIds = ["level", "does-not-exist"] }
        ];
        public IReadOnlyList<ICalibrationConstraint<SyntheticOptions>> Constraints { get; } = [];
        public CalibrationScoringPolicy ScoringPolicy { get; } = new SyntheticManifest().ScoringPolicy;
        public CalibrationAcceptancePolicy AcceptancePolicy { get; } = new SyntheticManifest().AcceptancePolicy;
        public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);
    }

    [Test]
    public void OverlayCompatibility_EverythingMatches_Compatible()
    {
        (IndicatorCalibrationArtifact artifact, CalibrationOverlayConsumptionContext context) = BuildMatchingPair();
        CalibrationOverlayCompatibilityResult result =
            IndicatorCalibrationOverlayCompatibilityValidator.Validate(artifact, context);
        Assert.That(result.IsCompatible, Is.True);
        Assert.That(result.RejectionReasons, Is.Empty);
    }

    [Test]
    public void OverlayCompatibility_UnapprovedArtifact_Rejected()
    {
        (IndicatorCalibrationArtifact artifact, CalibrationOverlayConsumptionContext context) = BuildMatchingPair();
        artifact = artifact with { PromotionStatus = CalibrationPromotionStatus.PendingReview };
        CalibrationOverlayCompatibilityResult result =
            IndicatorCalibrationOverlayCompatibilityValidator.Validate(artifact, context);
        Assert.That(result.IsCompatible, Is.False);
        Assert.That(result.RejectionReasons.Any(reason => reason.Contains("promotion status")), Is.True);
    }

    [Test]
    public void OverlayCompatibility_MismatchedInstrument_Rejected()
    {
        (IndicatorCalibrationArtifact artifact, CalibrationOverlayConsumptionContext context) = BuildMatchingPair();
        context = context with { CurrentInstrument = "FX:GBP/USD" };
        CalibrationOverlayCompatibilityResult result =
            IndicatorCalibrationOverlayCompatibilityValidator.Validate(artifact, context);
        Assert.That(result.IsCompatible, Is.False);
        Assert.That(result.RejectionReasons.Any(reason => reason.Contains("Instrument mismatch")), Is.True);
    }

    [Test]
    public void OverlayCompatibility_MismatchedBaselineHash_Rejected()
    {
        (IndicatorCalibrationArtifact artifact, CalibrationOverlayConsumptionContext context) = BuildMatchingPair();
        context = context with { CurrentBaselineConfigurationHash = "different-hash" };
        CalibrationOverlayCompatibilityResult result =
            IndicatorCalibrationOverlayCompatibilityValidator.Validate(artifact, context);
        Assert.That(result.IsCompatible, Is.False);
        Assert.That(result.RejectionReasons.Any(reason => reason.Contains("BaselineConfigurationHash")), Is.True);
    }

    [Test]
    public void OverlayCompatibility_ReportsEveryMismatchNotJustFirst()
    {
        (IndicatorCalibrationArtifact artifact, CalibrationOverlayConsumptionContext context) = BuildMatchingPair();
        artifact = artifact with
        {
            PromotionStatus = CalibrationPromotionStatus.PendingReview,
            Outcome = CalibrationOutcome.NoImprovement
        };
        context = context with { CurrentInstrument = "FX:GBP/USD" };
        CalibrationOverlayCompatibilityResult result =
            IndicatorCalibrationOverlayCompatibilityValidator.Validate(artifact, context);
        Assert.That(result.RejectionReasons.Count, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Artifact_ApprovedButNotImproved_ValidateThrows()
    {
        IndicatorCalibrationArtifact artifact = BuildMatchingPair().Artifact with
        {
            PromotionStatus = CalibrationPromotionStatus.Approved,
            Outcome = CalibrationOutcome.NoImprovement
        };
        Assert.Throws<ArgumentException>(artifact.Validate);
    }

    [Test]
    public void Artifact_GroupScope_ValidateThrows()
    {
        IndicatorCalibrationArtifact artifact = BuildMatchingPair().Artifact with
        {
            Scope = IndicatorCalibrationArtifact.GroupScope
        };
        Assert.Throws<ArgumentException>(artifact.Validate);
    }

    private static (IndicatorCalibrationArtifact Artifact, CalibrationOverlayConsumptionContext Context) BuildMatchingPair()
    {
        var identity = new CalibrationCompatibilityIdentity
        {
            StrategyId = "structural.indicator-confluence",
            StrategyImplementationVersion = "1.0",
            OptionsSchemaVersion = "indicator-confluence-options-v1",
            DefaultConfigurationHash = "default-hash",
            TimeframeTopologyHash = "topology-hash"
        };

        var evidence = new CalibrationEvidenceSummary
        {
            FoldCount = 5,
            AcceptableFoldCount = 4,
            AcceptableFoldPercent = 80m,
            MedianValidationExpectancyR = 0.1m,
            MedianValidationDrawdownR = 2m,
            MedianValidationTradeCount = 30,
            TrainValidationDegradation = 0.1m,
            BaselineMedianExpectancyR = -0.47m,
            ImprovementOverBaseline = 0.57m,
            ExternalHoldoutExpectancyR = 0.05m,
            ExternalHoldoutDrawdownR = 2.5m,
            ExternalHoldoutTradeCount = 15,
            ExternalHoldoutBaselineExpectancyR = -0.4m,
            TotalCandidatesEvaluated = 250
        };

        var artifact = new IndicatorCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = "calibration-1",
            StrategyId = identity.StrategyId,
            StrategyImplementationVersion = identity.StrategyImplementationVersion,
            OptionsSchemaVersion = identity.OptionsSchemaVersion,
            ManifestVersion = "indicator-confluence-manifest-v1",
            Scope = IndicatorCalibrationArtifact.InstrumentScope,
            Instrument = "FX:EUR/USD",
            TimeframeTopologyHash = identity.TimeframeTopologyHash,
            CandleDataIdentityHash = "candle-hash",
            BaselineConfigurationHash = "baseline-hash",
            ResolvedCandidateConfigurationHash = "candidate-hash",
            Overrides = [],
            AblationOverrides = new Dictionary<string, bool>(),
            Evidence = evidence,
            Outcome = CalibrationOutcome.Improved,
            ExperimentLedgerId = "ledger-1",
            ExperimentLedgerChecksum = "ledger-checksum",
            PromotionStatus = CalibrationPromotionStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var context = new CalibrationOverlayConsumptionContext
        {
            CurrentIdentity = identity,
            CurrentInstrument = "FX:EUR/USD",
            CurrentBaselineConfigurationHash = "baseline-hash",
            SupportedManifestVersions = new HashSet<string>(StringComparer.Ordinal)
            {
                "indicator-confluence-manifest-v1"
            }
        };

        return (artifact, context);
    }
}
