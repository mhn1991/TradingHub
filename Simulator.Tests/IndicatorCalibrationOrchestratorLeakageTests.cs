using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 5 gate: "leakage tests pass" (§18.2). Every test here uses a synthetic
/// evaluator whose score depends on which date window it was called with, so a test can give the
/// training window, each fold's validation window, and the external holdout window deliberately
/// different known optima. That makes it possible to prove - not merely assert - that a later
/// window's data can never influence an earlier decision: the fold-level candidate is picked using
/// only the training window's scores, cross-fold aggregation is picked using only fold-selected
/// candidates (never touching the holdout), and the holdout window is evaluated only after
/// everything else has already been frozen.
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationOrchestratorLeakageTests
{
    private sealed record TestOptions
    {
        public decimal Level { get; init; } = 20m;
    }

    private static ICalibrationParameterDescriptor<TestOptions> LevelDescriptor() =>
        new CalibrationParameterDescriptor<TestOptions>(
            "level", "Level", CalibrationParameterCategory.Entry, CalibrationValueKind.IndicatorLevel, CalibrationSpacing.Linear,
            defaultValue: 20m, hardMinimum: 5m, hardMaximum: 60m,
            coarseGrid: [10m, 20m, 30m, 40m, 50m], refinementStep: 2m,
            conservativeStartingValue: 10m, permissiveStartingValue: 50m, declaredSearchOrder: 0,
            read: o => o.Level, apply: (o, v) => o with { Level = v });

    private sealed class TestManifest : IIndicatorCalibrationManifest<TestOptions>
    {
        public int SchemaVersion => 1;
        public string ManifestVersion => "leakage-test-manifest-v1";
        public string StrategyId => "test-strategy";
        public string OptionsSchemaVersion => "test-options-v1";
        public IReadOnlyList<ICalibrationParameterDescriptor<TestOptions>> Parameters { get; } = [LevelDescriptor()];
        public IReadOnlyList<ICalibrationAblationDescriptor<TestOptions>> Ablations { get; } = [];
        public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } = [];
        public IReadOnlyList<ICalibrationConstraint<TestOptions>> Constraints { get; } = [];
        public CalibrationScoringPolicy ScoringPolicy { get; } = new()
        {
            PolicyVersion = "v1",
            ObjectiveId = CandidateScorer.MedianExpectancyDrawdownPenalizedObjective,
            MinimumTradesPerFold = 20,
            MaximumDrawdownR = 10m,
            MinimumMedianExpectancyR = -10m,
            MinimumProfitFactor = 0m,
            DrawdownPenaltyWeight = 0.1m,
            TurnoverPenaltyWeight = 0.05m
        };
        public CalibrationAcceptancePolicy AcceptancePolicy { get; } = new()
        {
            PolicyVersion = "v1",
            MinimumImprovementOverBaseline = 0.001m,
            MinimumAcceptableFoldPercent = 0m,
            MaximumTrainValidationDegradation = 10m,
            MinimumExternalHoldoutExpectancyR = -10m,
            MaximumExternalHoldoutDrawdownR = 10m,
            MinimumExternalHoldoutTrades = 20,
            MinimumPlateauSupport = 50m
        };
        public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);
    }

    private static readonly TestManifest Manifest = new();
    private static readonly TestOptions Baseline = new();

    private const decimal TrainingOptimum = 30m;

    private static readonly CalibrationTimeline Timeline = new()
    {
        LearningFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        LearningTo = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
        ExternalHoldoutFrom = new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero),
        ExternalHoldoutTo = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        EmbargoDays = 10,
        WarmupDays = 21
    };

    private const int InternalFoldCount = 3;

    private static readonly IReadOnlyList<CalibrationInternalFold> Folds =
        IndicatorCalibrationFoldPlanner.BuildFolds(Timeline, InternalFoldCount, Timeline.EmbargoDays);

    private static IndicatorCalibrationRequest BuildRequest() => new()
    {
        StrategyId = Manifest.StrategyId,
        ManifestVersion = Manifest.ManifestVersion,
        Instrument = new Brokers.Models.InstrumentKey("FX:EUR/USD"),
        TimeframeTopology = new TimeframeTopology
        {
            ExecutionInterval = Brokers.Models.BarInterval.Minutes(5),
            AnalysisBaseInterval = Brokers.Models.BarInterval.Minutes(1),
            SetupInterval = Brokers.Models.BarInterval.Minutes(15),
            ConfirmationIntervals = [],
            TrendIntervals = [Brokers.Models.BarInterval.Hours(1)],
            ManagementIntervals = [],
            AlignmentPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
            WarmupMinimumDays = 21
        },
        Timeline = Timeline,
        InternalFoldCount = InternalFoldCount,
        RandomSeed = 42,
        Budget = new CalibrationEvaluationBudget
        {
            WarningEvaluationCount = 100_000,
            MaximumEvaluationCount = 100_000,
            MaximumEvaluationsPerFold = 100_000,
            MaximumInteractionCombinationsPerGroup = 49,
            HardRuntimeLimit = TimeSpan.FromHours(6),
            OverflowPolicy = CalibrationBudgetOverflowPolicy.Reject
        },
        BaselineConfigurationHash = "baseline-hash"
    };

    /// <summary>Score curve peaking at <paramref name="optimum"/>, constant on every other backtest dimension so only <c>Level</c> drives the score.</summary>
    private static BacktestEvaluationResult ScoreAt(CalibrationCandidate candidate, decimal optimum)
    {
        decimal level = candidate.RequireNumericValue("level");
        decimal penalty = (level - optimum) * (level - optimum) * 0.001m;
        return new BacktestEvaluationResult
        {
            TradeCount = 50,
            MedianExpectancyR = 0.5m - penalty,
            MaximumDrawdownR = 1m,
            ProfitFactor = 2m,
            DataQualityValid = true
        };
    }

    private static bool IsHoldoutWindow(DateTimeOffset from, DateTimeOffset to) =>
        from == Timeline.ExternalHoldoutFrom && to == Timeline.ExternalHoldoutTo;

    private static bool IsValidationWindow(DateTimeOffset from, DateTimeOffset to) =>
        Folds.Any(fold => fold.ValidationFrom == from && fold.ValidationTo == to);

    /// <summary>Builds a factory whose training windows always score toward <see cref="TrainingOptimum"/>, and whose validation/holdout windows score toward the given, deliberately different, optima.</summary>
    private static SyntheticCandidateEvaluatorFactory BuildFactory(
        decimal validationOptimum, decimal holdoutOptimum, Action<string>? recordCallKind = null)
    {
        return new SyntheticCandidateEvaluatorFactory((from, to, candidate) =>
        {
            if (IsHoldoutWindow(from, to))
            {
                recordCallKind?.Invoke("holdout");
                return ScoreAt(candidate, holdoutOptimum);
            }
            if (IsValidationWindow(from, to))
            {
                recordCallKind?.Invoke("validation");
                return ScoreAt(candidate, validationOptimum);
            }
            recordCallKind?.Invoke("training");
            return ScoreAt(candidate, TrainingOptimum);
        });
    }

    [Test]
    public async Task ValidationWindowScoring_DoesNotInfluenceTheTrainingSelectedCandidate()
    {
        // Validation windows peak far away (level=10) from the training optimum (level=30). If the
        // validation window leaked into fold-candidate selection, the selected level would drift
        // toward 10; it must not.
        SyntheticCandidateEvaluatorFactory factory = BuildFactory(validationOptimum: 10m, holdoutOptimum: 50m);
        IndicatorCalibrationOrchestrationResult result = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, factory, maximumCoordinatePasses: 4);

        Assert.That(result.FoldResults, Has.Count.EqualTo(InternalFoldCount));
        foreach (FoldCandidateSelectionResult foldResult in result.FoldResults)
        {
            Assert.That(foldResult.Candidate.RequireNumericValue("level"), Is.EqualTo(TrainingOptimum),
                $"Fold {foldResult.FoldId} selected {foldResult.Candidate.RequireNumericValue("level")} " +
                "instead of the training optimum - the validation window must not influence fold-candidate selection.");
        }
    }

    [Test]
    public async Task ChangingAFoldsHeldOutScoring_DoesNotChangeThatFoldsTrainingSelectedCandidate()
    {
        SyntheticCandidateEvaluatorFactory lowValidation = BuildFactory(validationOptimum: 10m, holdoutOptimum: 50m);
        SyntheticCandidateEvaluatorFactory highValidation = BuildFactory(validationOptimum: 55m, holdoutOptimum: 50m);

        IndicatorCalibrationOrchestrationResult lowResult = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, lowValidation, maximumCoordinatePasses: 4);
        IndicatorCalibrationOrchestrationResult highResult = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, highValidation, maximumCoordinatePasses: 4);

        for (int i = 0; i < InternalFoldCount; i++)
        {
            Assert.That(
                lowResult.FoldResults[i].Candidate.RequireNumericValue("level"),
                Is.EqualTo(highResult.FoldResults[i].Candidate.RequireNumericValue("level")),
                $"Fold {i}'s training-selected candidate changed when only its held-out validation scoring changed.");
        }
        // Sanity check that the validation scoring difference actually took effect somewhere observable,
        // proving this isn't a vacuously-passing test.
        Assert.That(
            lowResult.FoldResults.Select(f => f.ValidationScore),
            Is.Not.EqualTo(highResult.FoldResults.Select(f => f.ValidationScore)));
    }

    [Test]
    public async Task ExternalHoldoutScoring_NeverInfluencesTheAggregatedCandidate()
    {
        SyntheticCandidateEvaluatorFactory lowHoldout = BuildFactory(validationOptimum: 30m, holdoutOptimum: 5m);
        SyntheticCandidateEvaluatorFactory highHoldout = BuildFactory(validationOptimum: 30m, holdoutOptimum: 40m);

        IndicatorCalibrationOrchestrationResult lowResult = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, lowHoldout, maximumCoordinatePasses: 4);
        IndicatorCalibrationOrchestrationResult highResult = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, highHoldout, maximumCoordinatePasses: 4);

        Assert.That(
            lowResult.Aggregation.AggregatedCandidate.RequireNumericValue("level"),
            Is.EqualTo(highResult.Aggregation.AggregatedCandidate.RequireNumericValue("level")),
            "The aggregated candidate changed when only the external holdout's scoring changed - the holdout must never feed back into aggregation.");
        Assert.That(lowResult.ExternalHoldoutCandidateScore, Is.Not.EqualTo(highResult.ExternalHoldoutCandidateScore));
    }

    [Test]
    public async Task HoldoutWindow_IsNeverEvaluatedBeforeEveryFoldAndAggregationHaveCompleted()
    {
        var callOrder = new List<string>();
        SyntheticCandidateEvaluatorFactory factory = BuildFactory(
            validationOptimum: 10m, holdoutOptimum: 50m, recordCallKind: callOrder.Add);

        await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, factory, maximumCoordinatePasses: 4);

        int firstHoldoutIndex = callOrder.IndexOf("holdout");
        Assert.That(firstHoldoutIndex, Is.GreaterThan(-1), "The holdout window should have been evaluated exactly twice.");
        Assert.That(callOrder.Skip(firstHoldoutIndex).All(kind => kind == "holdout"), Is.True,
            "Once the holdout window is first evaluated, no further training or validation calls should occur - " +
            "the orchestrator must not go back to the search after viewing the holdout.");
        Assert.That(callOrder.Take(firstHoldoutIndex).Any(kind => kind == "training"), Is.True);
        Assert.That(callOrder.Take(firstHoldoutIndex).Any(kind => kind == "validation"), Is.True);
        Assert.That(callOrder.Count(kind => kind == "holdout"), Is.EqualTo(2), "Exactly baseline + candidate should be evaluated on the holdout window.");
    }

    [Test]
    public async Task IdenticalRequestAndScoring_ProducesIdenticalOrchestrationResult()
    {
        SyntheticCandidateEvaluatorFactory factory = BuildFactory(validationOptimum: 25m, holdoutOptimum: 45m);

        IndicatorCalibrationOrchestrationResult first = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, factory, maximumCoordinatePasses: 4);
        IndicatorCalibrationOrchestrationResult second = await IndicatorCalibrationOrchestrator.RunAsync(
            BuildRequest(), Baseline, Manifest, factory, maximumCoordinatePasses: 4);

        Assert.That(second.Outcome, Is.EqualTo(first.Outcome));
        Assert.That(second.Reason, Is.EqualTo(first.Reason));
        Assert.That(
            second.Aggregation.AggregatedCandidate.RequireNumericValue("level"),
            Is.EqualTo(first.Aggregation.AggregatedCandidate.RequireNumericValue("level")));
        Assert.That(
            second.FoldResults.Select(f => f.Candidate.RequireNumericValue("level")),
            Is.EqualTo(first.FoldResults.Select(f => f.Candidate.RequireNumericValue("level"))));
        Assert.That(second.ExternalHoldoutCandidateScore, Is.EqualTo(first.ExternalHoldoutCandidateScore));
        Assert.That(second.ExternalHoldoutBaselineScore, Is.EqualTo(first.ExternalHoldoutBaselineScore));
    }
}
