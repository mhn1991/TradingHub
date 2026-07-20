using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.MarketData;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 7 gate: "unapproved/unreferenced/incompatible artifacts cannot alter
/// runtime behaviour." Every negative case here proves the resolver throws rather than silently
/// falling back or partially applying - the only way to get baseline behaviour is to never pin an
/// artifact at all, not to pin an invalid one and have it quietly ignored.
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationOverlayTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly IndicatorConfluenceCalibrationManifest Manifest = new();

    private static TimeframeTopology BuildTopology() => new()
    {
        ExecutionInterval = BarInterval.Minutes(1),
        AnalysisBaseInterval = BarInterval.Minutes(1),
        SetupInterval = BarInterval.Minutes(15),
        ConfirmationIntervals = [],
        TrendIntervals = [BarInterval.Hours(1)],
        ManagementIntervals = [],
        AlignmentPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets,
        WarmupMinimumDays = 10
    };

    private static TradingAgentDefinition BuildAgentDefinition(IndicatorConfluenceOptions options) => new()
    {
        Kind = TradingAgentKind.StructuralConfluence,
        StructuralConfluence = new StructuralConfluenceStrategyOptions { IndicatorConfluence = options }
    };

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static CalibrationEvidenceSummary BuildEvidence() => new()
    {
        FoldCount = 3,
        AcceptableFoldCount = 2,
        AcceptableFoldPercent = 66.7m,
        MedianValidationExpectancyR = 0.2m,
        MedianValidationDrawdownR = 1m,
        MedianValidationTradeCount = 25,
        TrainValidationDegradation = 0.05m,
        BaselineMedianExpectancyR = 0.1m,
        ImprovementOverBaseline = 0.1m,
        ExternalHoldoutExpectancyR = 0.15m,
        ExternalHoldoutDrawdownR = 1.2m,
        ExternalHoldoutTradeCount = 30,
        ExternalHoldoutBaselineExpectancyR = 0.05m,
        TotalCandidatesEvaluated = 200
    };

    private static IndicatorCalibrationArtifact BuildArtifact(
        CalibrationCompatibilityIdentity identity,
        string baselineHash,
        string instrument = "FX:EUR/USD",
        CalibrationPromotionStatus promotionStatus = CalibrationPromotionStatus.Approved,
        CalibrationOutcome outcome = CalibrationOutcome.Improved,
        string manifestVersion = "") => new()
    {
        SchemaVersion = 1,
        CalibrationId = "overlay-test-calibration-1",
        StrategyId = identity.StrategyId,
        StrategyImplementationVersion = identity.StrategyImplementationVersion,
        OptionsSchemaVersion = identity.OptionsSchemaVersion,
        ManifestVersion = string.IsNullOrEmpty(manifestVersion) ? Manifest.ManifestVersion : manifestVersion,
        Scope = IndicatorCalibrationArtifact.InstrumentScope,
        Instrument = instrument,
        TimeframeTopologyHash = identity.TimeframeTopologyHash,
        CandleDataIdentityHash = "candle-hash",
        BaselineConfigurationHash = baselineHash,
        ResolvedCandidateConfigurationHash = "resolved-hash",
        Overrides =
        [
            new CalibratedParameterOverride
            {
                ParameterId = "minimum-adx",
                DefaultValue = 20m,
                CalibratedValue = 30m,
                FoldSupportPercent = 100m,
                PlateauWidth = 0m,
                SelectionStage = "CrossFoldAggregation"
            }
        ],
        AblationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["require-trend-strengthening"] = true
        },
        Evidence = BuildEvidence(),
        Outcome = outcome,
        ExperimentLedgerId = "ledger-1",
        ExperimentLedgerChecksum = "checksum-1",
        PromotionStatus = promotionStatus,
        CreatedAt = DateTimeOffset.UtcNow
    };

    // ---- IndicatorCalibrationOverlayApplier ----

    [Test]
    public void Applier_AppliesOverridesAndAblations()
    {
        var baseline = new IndicatorConfluenceOptions();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(BuildTopology());
        IndicatorCalibrationArtifact artifact = BuildArtifact(identity, IndicatorCalibrationHash.ComputeOfObject(baseline));

        IndicatorConfluenceOptions result = IndicatorCalibrationOverlayApplier.Apply(baseline, artifact, Manifest);

        Assert.Multiple(() =>
        {
            Assert.That(result.MinimumAdx, Is.EqualTo(30m));
            Assert.That(result.RequireTrendStrengthening, Is.True);
            // Untouched fields retain the baseline's own values.
            Assert.That(result.StopAtr, Is.EqualTo(baseline.StopAtr));
        });
    }

    [Test]
    public void Applier_UnknownParameterId_Throws()
    {
        var baseline = new IndicatorConfluenceOptions();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(BuildTopology());
        IndicatorCalibrationArtifact artifact = BuildArtifact(identity, IndicatorCalibrationHash.ComputeOfObject(baseline)) with
        {
            Overrides =
            [
                new CalibratedParameterOverride
                {
                    ParameterId = "not-a-real-parameter",
                    DefaultValue = 1m,
                    CalibratedValue = 2m,
                    FoldSupportPercent = 100m,
                    PlateauWidth = 0m,
                    SelectionStage = "Test"
                }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => IndicatorCalibrationOverlayApplier.Apply(baseline, artifact, Manifest));
    }

    // ---- IndicatorCalibrationOverlayResolver ----

    [Test]
    public async Task Resolver_NoArtifactPinned_ReturnsAssignmentUnchanged_AndNeverTouchesRepository()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var assignment = new StrategyInstrumentAssignment { StrategyType = TradingAgentTypeIds.StructuralConfluence, Instrument = Instrument };
        var baseline = new IndicatorConfluenceOptions();

        StrategyInstrumentAssignment result = await IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
            assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
            BuildTopology(), repository);

        Assert.That(result, Is.SameAs(assignment));
        Assert.That(result.AgentDefinitionOverride, Is.Null);
    }

    [Test]
    public void Resolver_ArtifactDoesNotExist_Throws()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = Guid.NewGuid()
        };
        var baseline = new IndicatorConfluenceOptions();

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
                assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
                BuildTopology(), repository));
        Assert.That(exception!.Message, Does.Contain("does not exist"));
    }

    [Test]
    public async Task Resolver_ArtifactNotApproved_Throws()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new IndicatorConfluenceOptions();
        TimeframeTopology topology = BuildTopology();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        IndicatorCalibrationArtifact artifact = BuildArtifact(
            identity, IndicatorCalibrationHash.ComputeOfObject(baseline), promotionStatus: CalibrationPromotionStatus.PendingReview);
        CalibrationArtifactMetadata metadata = await repository.StoreIndicatorParametersAsync(artifact);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = metadata.Id
        };

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
                assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
                topology, repository));
        Assert.That(exception!.Message, Does.Contain("rejected in full"));
    }

    [Test]
    public async Task Resolver_InstrumentMismatch_Throws()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new IndicatorConfluenceOptions();
        TimeframeTopology topology = BuildTopology();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        IndicatorCalibrationArtifact artifact = BuildArtifact(
            identity, IndicatorCalibrationHash.ComputeOfObject(baseline), instrument: "FX:GBP/USD");
        CalibrationArtifactMetadata metadata = await repository.StoreIndicatorParametersAsync(artifact);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument, // FX:EUR/USD - deliberately different from the artifact's FX:GBP/USD
            IndicatorCalibrationArtifactId = metadata.Id
        };

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
                assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
                topology, repository));
        Assert.That(exception!.Message, Does.Contain("Instrument mismatch"));
    }

    [Test]
    public async Task Resolver_BaselineConfigurationHashMismatch_Throws()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new IndicatorConfluenceOptions();
        TimeframeTopology topology = BuildTopology();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        // Artifact was calibrated against a different baseline hash than the current effective configuration.
        IndicatorCalibrationArtifact artifact = BuildArtifact(identity, "a-completely-different-hash");
        CalibrationArtifactMetadata metadata = await repository.StoreIndicatorParametersAsync(artifact);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = metadata.Id
        };

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
                assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
                topology, repository));
        Assert.That(exception!.Message, Does.Contain("BaselineConfigurationHash mismatch"));
    }

    [Test]
    public async Task Resolver_ManifestVersionUnsupported_Throws()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new IndicatorConfluenceOptions();
        TimeframeTopology topology = BuildTopology();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        IndicatorCalibrationArtifact artifact = BuildArtifact(
            identity, IndicatorCalibrationHash.ComputeOfObject(baseline), manifestVersion: "some-old-manifest-v0");
        CalibrationArtifactMetadata metadata = await repository.StoreIndicatorParametersAsync(artifact);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = metadata.Id
        };

        InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
                assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
                topology, repository));
        Assert.That(exception!.Message, Does.Contain("ManifestVersion"));
    }

    [Test]
    public async Task Resolver_FullyCompatibleApprovedArtifact_AppliesOverlayOntoTheAssignment()
    {
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var baseline = new IndicatorConfluenceOptions();
        TimeframeTopology topology = BuildTopology();
        CalibrationCompatibilityIdentity identity = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        IndicatorCalibrationArtifact artifact = BuildArtifact(identity, IndicatorCalibrationHash.ComputeOfObject(baseline));
        CalibrationArtifactMetadata metadata = await repository.StoreIndicatorParametersAsync(artifact);
        // Storing an artifact always seeds it as PendingReview at the metadata level regardless of
        // what the payload object claims (no auto-promotion) - approval must go through the real
        // review flow for the resolver to ever treat it as compatible.
        await repository.UpdatePromotionStatusAsync(metadata.Id, CalibrationPromotionStatus.Approved);

        var assignment = new StrategyInstrumentAssignment
        {
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            Instrument = Instrument,
            IndicatorCalibrationArtifactId = metadata.Id
        };

        StrategyInstrumentAssignment result = await IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
            assignment, baseline, BuildAgentDefinition, Manifest, IndicatorConfluenceCalibrationCompatibility.Instance,
            topology, repository);

        IndicatorConfluenceOptions overlaid = result.AgentDefinitionOverride!.StructuralConfluence!.IndicatorConfluence;
        Assert.Multiple(() =>
        {
            Assert.That(overlaid.MinimumAdx, Is.EqualTo(30m));
            Assert.That(overlaid.RequireTrendStrengthening, Is.True);
            Assert.That(overlaid.StopAtr, Is.EqualTo(baseline.StopAtr), "Untouched parameters keep their baseline value.");
        });
    }
}
