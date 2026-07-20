using Brokers.Models;
using Simulator.Calibration;

namespace Simulator.Tests;

/// <summary>
/// Exercises <see cref="BestApprovedCalibrationArtifactResolver"/> - the tooling-layer helper
/// behind the auto-apply-for-tests flow. Only Approved+Improved artifacts matching strategy,
/// instrument and the standard topology hash for the requested execution interval should ever be
/// returned, and among several matches the most recently created one should win.
/// </summary>
[TestFixture]
public sealed class BestApprovedCalibrationArtifactResolverTests
{
    private const string StrategyId = "test-strategy-for-auto-apply-resolver";

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-auto-apply-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task ResolveAsync_ReturnsNull_WhenNoArtifactIsApproved()
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        await artifacts.StoreIndicatorParametersAsync(BuildArtifact(CalibrationOutcome.Improved, DateTimeOffset.UtcNow));

        Guid? resolved = await BestApprovedCalibrationArtifactResolver.ResolveAsync(
            artifacts, StrategyId, new InstrumentKey("FX:EUR/USD"), BarInterval.Minutes(1));

        Assert.That(resolved, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_IgnoresApprovedArtifactsForADifferentInstrumentOrTimeframe()
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());

        CalibrationArtifactMetadata wrongInstrument = await artifacts.StoreIndicatorParametersAsync(
            BuildArtifact(CalibrationOutcome.Improved, DateTimeOffset.UtcNow, instrument: "FX:GBP/USD"));
        await artifacts.UpdatePromotionStatusAsync(wrongInstrument.Id, CalibrationPromotionStatus.Approved);

        CalibrationArtifactMetadata wrongTimeframe = await artifacts.StoreIndicatorParametersAsync(
            BuildArtifact(CalibrationOutcome.Improved, DateTimeOffset.UtcNow, executionInterval: BarInterval.Minutes(5)));
        await artifacts.UpdatePromotionStatusAsync(wrongTimeframe.Id, CalibrationPromotionStatus.Approved);

        Guid? resolved = await BestApprovedCalibrationArtifactResolver.ResolveAsync(
            artifacts, StrategyId, new InstrumentKey("FX:EUR/USD"), BarInterval.Minutes(1));

        Assert.That(resolved, Is.Null);
    }

    [Test]
    public async Task ResolveAsync_PicksTheMostRecentlyCreatedMatchingApprovedImprovedArtifact()
    {
        // CalibrationArtifactMetadata.CreatedAt is stamped by the repository at store time (not
        // read from the artifact payload), so "most recent" here means store order - the small
        // delays make that ordering deterministic instead of relying on incidental call latency.
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CalibrationArtifactMetadata older = await artifacts.StoreIndicatorParametersAsync(
            BuildArtifact(CalibrationOutcome.Improved, now));
        await artifacts.UpdatePromotionStatusAsync(older.Id, CalibrationPromotionStatus.Approved);
        await Task.Delay(20);

        CalibrationArtifactMetadata newer = await artifacts.StoreIndicatorParametersAsync(
            BuildArtifact(CalibrationOutcome.Improved, now));
        await artifacts.UpdatePromotionStatusAsync(newer.Id, CalibrationPromotionStatus.Approved);
        await Task.Delay(20);

        // A PendingReview artifact stored last still must not win - only Approved+Improved is eligible.
        await artifacts.StoreIndicatorParametersAsync(BuildArtifact(CalibrationOutcome.Improved, now));

        Guid? resolved = await BestApprovedCalibrationArtifactResolver.ResolveAsync(
            artifacts, StrategyId, new InstrumentKey("FX:EUR/USD"), BarInterval.Minutes(1));

        Assert.That(resolved, Is.EqualTo(newer.Id));
    }

    private static IndicatorCalibrationArtifact BuildArtifact(
        CalibrationOutcome outcome,
        DateTimeOffset createdAt,
        string instrument = "FX:EUR/USD",
        BarInterval? executionInterval = null) => new()
    {
        SchemaVersion = 1,
        CalibrationId = $"standalone-{Guid.NewGuid():N}",
        StrategyId = StrategyId,
        StrategyImplementationVersion = "1.0",
        OptionsSchemaVersion = "fake-options-v1",
        ManifestVersion = "fake-manifest-v1",
        Scope = IndicatorCalibrationArtifact.InstrumentScope,
        Instrument = instrument,
        TimeframeTopologyHash = StandardTimeframeTopologyFactory.Build(executionInterval ?? BarInterval.Minutes(1)).ComputeHash(),
        CandleDataIdentityHash = "candle-hash",
        BaselineConfigurationHash = "baseline-hash",
        ResolvedCandidateConfigurationHash = "resolved-hash",
        Overrides = [],
        AblationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal),
        Evidence = new CalibrationEvidenceSummary
        {
            FoldCount = 1,
            AcceptableFoldCount = 1,
            AcceptableFoldPercent = 100m,
            MedianValidationExpectancyR = 0.2m,
            MedianValidationDrawdownR = 1m,
            MedianValidationTradeCount = 30,
            TrainValidationDegradation = 0m,
            BaselineMedianExpectancyR = 0.1m,
            ImprovementOverBaseline = 0.1m,
            ExternalHoldoutExpectancyR = 0.2m,
            ExternalHoldoutDrawdownR = 1m,
            ExternalHoldoutTradeCount = 30,
            ExternalHoldoutBaselineExpectancyR = 0.1m,
            TotalCandidatesEvaluated = 10
        },
        Outcome = outcome,
        ExperimentLedgerId = "ledger-1",
        ExperimentLedgerChecksum = "checksum-1",
        PromotionStatus = CalibrationPromotionStatus.PendingReview,
        CreatedAt = createdAt
    };
}
