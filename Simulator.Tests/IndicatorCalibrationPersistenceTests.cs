using Brokers.Models;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 3 gate: "deterministic identity and resume repository tests pass."
/// Covers <see cref="BacktestEvaluationIdentity"/> determinism, the content-addressed candidate
/// cache, and the file-backed ledger repository's resume/never-regress behavior.
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationPersistenceTests
{
    private static BacktestEvaluationIdentity BuildIdentity(int randomSeed = 42) => new()
    {
        StrategyId = "structural.indicator-confluence",
        StrategyImplementationHash = "impl-hash",
        EffectiveAgentDefinitionHash = "agent-def-hash",
        CompleteOptionsHash = "options-hash",
        FeatureSwitchHash = "feature-switch-hash",
        Instrument = "FX:EUR/USD",
        CandleDataHash = "candle-hash",
        PriceComponent = "mid",
        WindowStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        WindowEnd = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        WarmupStart = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero),
        TimeframeTopologyHash = "topology-hash",
        ExecutionModelVersion = "execution-v1",
        BrokerCostModelHash = "cost-hash",
        DataQualityPolicyVersion = "quality-v1",
        RandomSeed = randomSeed
    };

    [Test]
    public void ComputeCacheKey_IsDeterministicAcrossCalls()
    {
        BacktestEvaluationIdentity identity = BuildIdentity();
        Assert.That(identity.ComputeCacheKey(), Is.EqualTo(identity.ComputeCacheKey()));
    }

    [Test]
    public void ComputeCacheKey_ChangesWhenAnyExecutionAffectingFieldChanges()
    {
        BacktestEvaluationIdentity baseline = BuildIdentity();
        string baselineKey = baseline.ComputeCacheKey();

        Assert.Multiple(() =>
        {
            Assert.That((baseline with { CompleteOptionsHash = "different" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { CandleDataHash = "different" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { PriceComponent = "bid" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { ExecutionModelVersion = "execution-v2" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { BrokerCostModelHash = "different" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { DataQualityPolicyVersion = "quality-v2" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { FeatureSwitchHash = "different" }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
            Assert.That((baseline with { RandomSeed = 43 }).ComputeCacheKey(), Is.Not.EqualTo(baselineKey));
        });
    }

    [Test]
    public void CandidateCache_MissThenHit_ReturnsStoredResult()
    {
        var cache = new InMemoryCalibrationCandidateCache();
        BacktestEvaluationIdentity identity = BuildIdentity();
        var result = new BacktestEvaluationResult
        {
            TradeCount = 42,
            MedianExpectancyR = 0.2m,
            MaximumDrawdownR = 3m,
            ProfitFactor = 1.4m,
            DataQualityValid = true
        };

        Assert.That(cache.TryGet(identity, out BacktestEvaluationResult? miss), Is.False);
        Assert.That(miss, Is.Null);

        cache.Set(identity, result);
        Assert.That(cache.TryGet(identity, out BacktestEvaluationResult? hit), Is.True);
        Assert.That(hit, Is.EqualTo(result));
        Assert.That(cache.Hits, Is.EqualTo(1));
        Assert.That(cache.Misses, Is.EqualTo(1));
    }

    [Test]
    public void CandidateCache_DifferentIdentity_IsAlwaysAMiss()
    {
        var cache = new InMemoryCalibrationCandidateCache();
        cache.Set(BuildIdentity(randomSeed: 1), new BacktestEvaluationResult
        {
            TradeCount = 10, MedianExpectancyR = 0m, MaximumDrawdownR = 1m, ProfitFactor = 1m, DataQualityValid = true
        });

        Assert.That(cache.TryGet(BuildIdentity(randomSeed: 2), out _), Is.False);
    }

    private static IndicatorCalibrationRequest BuildRequest() => new()
    {
        StrategyId = "structural.indicator-confluence",
        ManifestVersion = "indicator-confluence-manifest-v1",
        Instrument = new InstrumentKey("FX:EUR/USD"),
        TimeframeTopology = new TimeframeTopology
        {
            ExecutionInterval = BarInterval.Minutes(5),
            AnalysisBaseInterval = BarInterval.Minutes(1),
            SetupInterval = BarInterval.Minutes(15),
            ConfirmationIntervals = [],
            TrendIntervals = [BarInterval.Hours(1)],
            ManagementIntervals = [],
            AlignmentPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
            WarmupMinimumDays = 21
        },
        Timeline = new CalibrationTimeline
        {
            LearningFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LearningTo = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            ExternalHoldoutFrom = new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero),
            ExternalHoldoutTo = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EmbargoDays = 10,
            WarmupDays = 21
        },
        InternalFoldCount = 5,
        RandomSeed = 42,
        Budget = new CalibrationEvaluationBudget
        {
            WarningEvaluationCount = 200,
            MaximumEvaluationCount = 400,
            MaximumEvaluationsPerFold = 400,
            MaximumInteractionCombinationsPerGroup = 49,
            HardRuntimeLimit = TimeSpan.FromHours(6),
            OverflowPolicy = CalibrationBudgetOverflowPolicy.Reject
        },
        BaselineConfigurationHash = "baseline-hash"
    };

    private static CalibrationBudgetPreview BuildBudgetPreview() => new()
    {
        BaselineEvaluations = 6,
        SensitivityEvaluations = 30,
        StartingPointEvaluations = 18,
        MaximumCoordinatePasses = 4,
        InteractionEvaluations = 75,
        RefinementEvaluations = 35,
        InternalFoldMultiplier = 5,
        ExternalHoldoutEvaluations = 2,
        EstimatedCacheHits = 40,
        ExpectedUncachedBacktests = 300,
        EstimatedCandleEvaluations = 12_000_000,
        EstimatedDurationLow = TimeSpan.FromMinutes(20),
        EstimatedDurationHigh = TimeSpan.FromMinutes(90),
        HardRuntimeLimit = TimeSpan.FromHours(6),
        ExceedsBudget = false,
        AppliedOverflowPolicy = null,
        OverflowAdjustments = []
    };

    private static IndicatorCalibrationLedgerEntry BuildEntry(string stepId, int candidateIndex) => new()
    {
        StepId = stepId,
        Stage = IndicatorCalibrationStage.SensitivityScreening,
        FoldId = -1,
        StartingPointId = "default",
        ParameterId = "minimum-adx",
        CandidateIndex = candidateIndex,
        CandidateNumericValues = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["minimum-adx"] = 20m + candidateIndex },
        CandidateAblationValues = new Dictionary<string, bool>(StringComparer.Ordinal),
        WasCacheHit = false,
        IsValidCandidate = true,
        PassedEligibilityGates = true,
        Result = new BacktestEvaluationResult
        {
            TradeCount = 30, MedianExpectancyR = 0.1m, MaximumDrawdownR = 2m, ProfitFactor = 1.2m, DataQualityValid = true
        },
        Score = 0.1m,
        Accepted = true,
        RecordedAt = DateTimeOffset.UtcNow
    };

    private static IndicatorCalibrationExperimentLedger BuildLedger(
        string ledgerId, long revision, IReadOnlyList<IndicatorCalibrationLedgerEntry> entries) => new()
    {
        LedgerId = ledgerId,
        Revision = revision,
        Request = BuildRequest(),
        BudgetPreview = BuildBudgetPreview(),
        Entries = entries,
        CurrentStage = IndicatorCalibrationStage.SensitivityScreening,
        IsCancelled = false,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Test]
    public async Task LedgerRepository_SaveThenGet_RoundTrips()
    {
        var repository = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        IndicatorCalibrationExperimentLedger ledger = BuildLedger(
            "ledger-1", revision: 1, entries: [BuildEntry("step-1", 0)]);

        await repository.SaveAsync(ledger);
        IndicatorCalibrationExperimentLedger? loaded = await repository.GetAsync("ledger-1");

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Entries, Has.Count.EqualTo(1));
        Assert.That(loaded.Entries[0].StepId, Is.EqualTo("step-1"));
    }

    [Test]
    public async Task LedgerRepository_ResumeAppendsNewSteps_SkipsAlreadyCompletedOnes()
    {
        var repository = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        IndicatorCalibrationExperimentLedger first = BuildLedger(
            "ledger-resume", revision: 1, entries: [BuildEntry("step-1", 0)]);
        await repository.SaveAsync(first);

        // Simulate a resumed run: reload, confirm step-1 is already completed, append only new steps.
        IndicatorCalibrationExperimentLedger? reloaded = await repository.GetAsync("ledger-resume");
        Assert.That(reloaded, Is.Not.Null);
        IReadOnlySet<string> completed = reloaded!.CompletedStepIds();
        Assert.That(completed.Contains("step-1"), Is.True);
        Assert.That(completed.Contains("step-2"), Is.False);

        IndicatorCalibrationExperimentLedger resumed = reloaded with
        {
            Revision = reloaded.Revision + 1,
            Entries = [.. reloaded.Entries, BuildEntry("step-2", 1)],
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await repository.SaveAsync(resumed);

        IndicatorCalibrationExperimentLedger? final = await repository.GetAsync("ledger-resume");
        Assert.That(final!.Entries, Has.Count.EqualTo(2));
        Assert.That(final.Entries.Select(entry => entry.StepId), Is.EquivalentTo(new[] { "step-1", "step-2" }));
    }

    [Test]
    public async Task LedgerRepository_NeverRegressesToAnOlderRevision()
    {
        var repository = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        IndicatorCalibrationExperimentLedger newer = BuildLedger(
            "ledger-guard", revision: 5, entries: [BuildEntry("step-1", 0), BuildEntry("step-2", 1)]);
        await repository.SaveAsync(newer);

        IndicatorCalibrationExperimentLedger stale = BuildLedger(
            "ledger-guard", revision: 2, entries: [BuildEntry("step-1", 0)]);
        await repository.SaveAsync(stale);

        IndicatorCalibrationExperimentLedger? loaded = await repository.GetAsync("ledger-guard");
        Assert.That(loaded!.Revision, Is.EqualTo(5), "A lower-revision write must never overwrite a newer one.");
        Assert.That(loaded.Entries, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task LedgerRepository_DuplicateStepId_ValidateThrows()
    {
        var repository = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        IndicatorCalibrationExperimentLedger broken = BuildLedger(
            "ledger-broken", revision: 1, entries: [BuildEntry("step-1", 0), BuildEntry("step-1", 1)]);

        Assert.ThrowsAsync<ArgumentException>(async () => await repository.SaveAsync(broken));
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "tradinghub-sim-tests", "indicator-calibration-ledgers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
