using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 4 gate: "deterministic, bounded, resumable search tests pass." Every test
/// here uses a synthetic, analytically-known objective (never a real backtest) so the assertions
/// can check that the algorithm actually converges toward a known optimum, not merely that it
/// runs without throwing.
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationSearchEngineTests
{
    private sealed record TestOptions
    {
        public decimal Level { get; init; } = 20m;
        public decimal Threshold { get; init; } = 50m;
        public bool Strengthen { get; init; }
    }

    // True optimum: Level=27 (deliberately off the coarse grid, on the refinement grid, to prove
    // refinement finds something coordinate descent alone cannot), Threshold=55 (on the coarse grid).
    private const decimal TrueOptimalLevel = 27m;
    private const decimal TrueOptimalThreshold = 55m;

    private static BacktestEvaluationResult SyntheticScore(CalibrationCandidate candidate)
    {
        decimal level = candidate.NumericValues.GetValueOrDefault("level", 20m);
        decimal threshold = candidate.NumericValues.GetValueOrDefault("threshold", 50m);
        decimal penalty = (level - TrueOptimalLevel) * (level - TrueOptimalLevel) * 0.002m +
            (threshold - TrueOptimalThreshold) * (threshold - TrueOptimalThreshold) * 0.001m;
        return new BacktestEvaluationResult
        {
            TradeCount = 50,
            MedianExpectancyR = 0.5m - penalty,
            MaximumDrawdownR = 1m,
            ProfitFactor = 2m,
            DataQualityValid = true
        };
    }

    private static ICalibrationParameterDescriptor<TestOptions> LevelDescriptor() =>
        new CalibrationParameterDescriptor<TestOptions>(
            "level", "Level", CalibrationParameterCategory.Entry, CalibrationValueKind.IndicatorLevel, CalibrationSpacing.Linear,
            defaultValue: 20m, hardMinimum: 5m, hardMaximum: 45m,
            coarseGrid: [10m, 15m, 20m, 25m, 30m], refinementStep: 1m,
            conservativeStartingValue: 15m, permissiveStartingValue: 30m, declaredSearchOrder: 0,
            read: o => o.Level, apply: (o, v) => o with { Level = v });

    private static ICalibrationParameterDescriptor<TestOptions> ThresholdDescriptor() =>
        new CalibrationParameterDescriptor<TestOptions>(
            "threshold", "Threshold", CalibrationParameterCategory.Confirmation, CalibrationValueKind.PercentageZeroToHundred, CalibrationSpacing.Linear,
            defaultValue: 50m, hardMinimum: 30m, hardMaximum: 70m,
            coarseGrid: [40m, 45m, 50m, 55m, 60m], refinementStep: 1m,
            conservativeStartingValue: 45m, permissiveStartingValue: 60m, declaredSearchOrder: 1,
            read: o => o.Threshold, apply: (o, v) => o with { Threshold = v });

    private static ICalibrationAblationDescriptor<TestOptions> StrengthenAblation() =>
        new CalibrationAblationDescriptor<TestOptions>(
            "strengthen", "Strengthen", defaultValue: false,
            read: o => o.Strengthen, apply: (o, v) => o with { Strengthen = v });

    private sealed class TestManifest : IIndicatorCalibrationManifest<TestOptions>
    {
        public int SchemaVersion => 1;
        public string ManifestVersion => "test-manifest-v1";
        public string StrategyId => "test-strategy";
        public string OptionsSchemaVersion => "test-options-v1";
        public IReadOnlyList<ICalibrationParameterDescriptor<TestOptions>> Parameters { get; } =
            [LevelDescriptor(), ThresholdDescriptor()];
        public IReadOnlyList<ICalibrationAblationDescriptor<TestOptions>> Ablations { get; } = [StrengthenAblation()];
        public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
        [
            new CalibrationInteractionGroup { GroupId = "level-x-threshold", ParameterIds = ["level", "threshold"] }
        ];
        public IReadOnlyList<ICalibrationConstraint<TestOptions>> Constraints { get; } =
        [
            new CalibrationConstraint<TestOptions>(
                "level-below-threshold",
                "Level must remain below Threshold.",
                o => o.Level < o.Threshold
                    ? CalibrationConstraintResult.Valid
                    : CalibrationConstraintResult.Invalid("Level must be less than Threshold."))
        ];
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
            MinimumAcceptableFoldPercent = 60m,
            MaximumTrainValidationDegradation = 0.5m,
            MinimumExternalHoldoutExpectancyR = -10m,
            MaximumExternalHoldoutDrawdownR = 10m,
            MinimumExternalHoldoutTrades = 20,
            MinimumPlateauSupport = 50m
        };
        public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);
    }

    private static readonly TestManifest Manifest = new();
    private static readonly TestOptions Baseline = new();
    private static readonly ICandidateEvaluator Evaluator = new SyntheticCandidateEvaluator(SyntheticScore);

    // ---- EvaluationBudgetEstimator ----

    private static IndicatorCalibrationRequest BuildRequest(
        CalibrationBudgetOverflowPolicy overflow = CalibrationBudgetOverflowPolicy.Reject,
        int maxEvaluations = 100_000,
        int warningEvaluations = 50) => new()
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
            WarningEvaluationCount = warningEvaluations,
            MaximumEvaluationCount = maxEvaluations,
            MaximumEvaluationsPerFold = maxEvaluations,
            MaximumInteractionCombinationsPerGroup = 49,
            // Generously above any realistic estimate for this fixture's small request - the
            // point of the "does not exceed budget" tests is the count/estimate math, not a tight
            // runtime ceiling (see EvaluationBudgetEstimator's real-measured duration constants).
            HardRuntimeLimit = TimeSpan.FromHours(24),
            OverflowPolicy = overflow
        },
        BaselineConfigurationHash = "baseline-hash"
    };

    [Test]
    public void EvaluationBudgetEstimator_ComputesDeterministicPreview()
    {
        IndicatorCalibrationRequest request = BuildRequest();
        CalibrationBudgetPreview first = EvaluationBudgetEstimator.Estimate(request, Manifest, maximumCoordinatePasses: 3);
        CalibrationBudgetPreview second = EvaluationBudgetEstimator.Estimate(request, Manifest, maximumCoordinatePasses: 3);
        Assert.That(second, Is.EqualTo(first));
        Assert.That(first.TotalPlannedEvaluations, Is.GreaterThan(0));
        Assert.That(first.ExceedsBudget, Is.False);
    }

    [Test]
    public void EvaluationBudgetEstimator_ExceedsBudget_RejectPolicy_FlagsWithoutRunning()
    {
        IndicatorCalibrationRequest request = BuildRequest(CalibrationBudgetOverflowPolicy.Reject, maxEvaluations: 1, warningEvaluations: 1);
        CalibrationBudgetPreview preview = EvaluationBudgetEstimator.Estimate(request, Manifest, maximumCoordinatePasses: 3);
        Assert.That(preview.ExceedsBudget, Is.True);
        Assert.That(preview.AppliedOverflowPolicy, Is.EqualTo(CalibrationBudgetOverflowPolicy.Reject));
        Assert.That(preview.OverflowAdjustments, Is.Not.Empty);
    }

    [Test]
    public void EvaluationBudgetEstimator_ReduceStartingPoints_ActuallyReducesTheCount()
    {
        IndicatorCalibrationRequest request = BuildRequest(CalibrationBudgetOverflowPolicy.ReduceStartingPoints, maxEvaluations: 1, warningEvaluations: 1);
        CalibrationBudgetPreview preview = EvaluationBudgetEstimator.Estimate(request, Manifest, maximumCoordinatePasses: 3);
        Assert.That(preview.AppliedOverflowPolicy, Is.EqualTo(CalibrationBudgetOverflowPolicy.ReduceStartingPoints));
        Assert.That(preview.StartingPointEvaluations, Is.EqualTo(1 * request.InternalFoldCount));
        Assert.That(preview.OverflowAdjustments, Is.Not.Empty);
    }

    [Test]
    public void EvaluationBudgetEstimator_ReduceRefinement_ZeroesRefinementEvaluations()
    {
        IndicatorCalibrationRequest request = BuildRequest(CalibrationBudgetOverflowPolicy.ReduceRefinement, maxEvaluations: 1, warningEvaluations: 1);
        CalibrationBudgetPreview preview = EvaluationBudgetEstimator.Estimate(request, Manifest, maximumCoordinatePasses: 3);
        Assert.That(preview.RefinementEvaluations, Is.EqualTo(0));
    }

    // ---- SensitivityScreener ----

    [Test]
    public async Task SensitivityScreener_ClassifiesInfluentialParametersCorrectly()
    {
        CalibrationCandidate defaults = CalibrationCandidate.FromDefaults(Manifest);
        IReadOnlyList<ParameterSensitivityResult> results = await SensitivityScreener.ScreenAsync(
            defaults, Baseline, Manifest, Evaluator, Manifest.ScoringPolicy);

        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(r => r.Classification == ParameterSensitivityClassification.Influential), Is.True,
            "Both parameters have a real quadratic effect on score and should be classified influential.");
    }

    [Test]
    public async Task SensitivityScreener_FlatObjective_ClassifiesNegligible()
    {
        var flatEvaluator = new SyntheticCandidateEvaluator(_ => new BacktestEvaluationResult
        {
            TradeCount = 50, MedianExpectancyR = 0.3m, MaximumDrawdownR = 1m, ProfitFactor = 2m, DataQualityValid = true
        });
        CalibrationCandidate defaults = CalibrationCandidate.FromDefaults(Manifest);
        IReadOnlyList<ParameterSensitivityResult> results = await SensitivityScreener.ScreenAsync(
            defaults, Baseline, Manifest, flatEvaluator, Manifest.ScoringPolicy);

        Assert.That(results.All(r => r.Classification == ParameterSensitivityClassification.Negligible), Is.True);
    }

    [Test]
    public async Task SensitivityScreener_ResultsAreDeterministic()
    {
        CalibrationCandidate defaults = CalibrationCandidate.FromDefaults(Manifest);
        IReadOnlyList<ParameterSensitivityResult> first = await SensitivityScreener.ScreenAsync(
            defaults, Baseline, Manifest, Evaluator, Manifest.ScoringPolicy);
        IReadOnlyList<ParameterSensitivityResult> second = await SensitivityScreener.ScreenAsync(
            defaults, Baseline, Manifest, Evaluator, Manifest.ScoringPolicy);
        Assert.That(second.Select(r => r.ParameterId), Is.EqualTo(first.Select(r => r.ParameterId)));
        Assert.That(second.Select(r => r.Classification), Is.EqualTo(first.Select(r => r.Classification)));
    }

    // ---- CoordinateDescentEngine ----

    [Test]
    public async Task CoordinateDescentEngine_ConvergesTowardTheCoarseGridOptimum()
    {
        CalibrationCandidate starting = CalibrationCandidate.FromDefaults(Manifest);
        CoordinateDescentResult result = await CoordinateDescentEngine.RunAsync(
            starting, ["level", "threshold"], Baseline, Manifest, Evaluator, Manifest.ScoringPolicy,
            minimumMaterialImprovement: 0.0001m, maximumPasses: 4);

        // 25 is the closest coarse-grid point to the true optimum 27; 55 is exactly the true optimum.
        Assert.That(result.BestCandidate.RequireNumericValue("level"), Is.EqualTo(25m));
        Assert.That(result.BestCandidate.RequireNumericValue("threshold"), Is.EqualTo(55m));
        Assert.That(result.Converged, Is.True);
        Assert.That(result.PassesRun, Is.LessThanOrEqualTo(4));
    }

    [Test]
    public async Task CoordinateDescentEngine_StopsEarlyWhenNoChangeAccepted()
    {
        // Starting already at the coarse-grid optimum - the first pass should find nothing better and converge immediately.
        CalibrationCandidate starting = CalibrationCandidate.FromDefaults(Manifest)
            .WithNumericValue("level", 25m).WithNumericValue("threshold", 55m);
        CoordinateDescentResult result = await CoordinateDescentEngine.RunAsync(
            starting, ["level", "threshold"], Baseline, Manifest, Evaluator, Manifest.ScoringPolicy,
            minimumMaterialImprovement: 0.0001m, maximumPasses: 4);

        Assert.That(result.PassesRun, Is.EqualTo(1));
        Assert.That(result.Converged, Is.True);
    }

    [Test]
    public async Task CoordinateDescentEngine_RejectsCandidatesViolatingConstraints()
    {
        // A manifest where the constraint (Level < Threshold) is violated by part of the grid:
        // Level's coarse grid is 10,15,20,25,30 - fixing Threshold=22 makes 25 and 30 invalid.
        var tightManifest = new TightConstraintManifest();
        CalibrationCandidate starting = CalibrationCandidate.FromDefaults(tightManifest);
        CoordinateDescentResult result = await CoordinateDescentEngine.RunAsync(
            starting, ["level"], new TestOptions { Threshold = 22m }, tightManifest, Evaluator, tightManifest.ScoringPolicy,
            minimumMaterialImprovement: 0.0001m, maximumPasses: 2);

        Assert.That(result.Steps.Any(step => !step.IsValidCandidate), Is.True,
            "Level values 25 and 30 (>= the fixed Threshold 22) should be rejected by the Level < Threshold constraint.");
        Assert.That(result.BestCandidate.RequireNumericValue("level"), Is.LessThan(22m));
    }

    private sealed class TightConstraintManifest : IIndicatorCalibrationManifest<TestOptions>
    {
        public int SchemaVersion => 1;
        public string ManifestVersion => "tight-v1";
        public string StrategyId => "test-strategy";
        public string OptionsSchemaVersion => "test-options-v1";
        public IReadOnlyList<ICalibrationParameterDescriptor<TestOptions>> Parameters { get; } = [LevelDescriptor()];
        public IReadOnlyList<ICalibrationAblationDescriptor<TestOptions>> Ablations { get; } = [];
        public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } = [];
        public IReadOnlyList<ICalibrationConstraint<TestOptions>> Constraints { get; } =
        [
            new CalibrationConstraint<TestOptions>(
                "level-below-threshold", "Level must remain below Threshold.",
                o => o.Level < o.Threshold ? CalibrationConstraintResult.Valid : CalibrationConstraintResult.Invalid("blocked"))
        ];
        public CalibrationScoringPolicy ScoringPolicy { get; } = new TestManifest().ScoringPolicy;
        public CalibrationAcceptancePolicy AcceptancePolicy { get; } = new TestManifest().AcceptancePolicy;
        public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);
    }

    // ---- InteractionSearchEngine ----

    [Test]
    public async Task InteractionSearchEngine_ExploresOnlyDeclaredGroups_AndFindsJointOptimum()
    {
        CalibrationCandidate seed = CalibrationCandidate.FromDefaults(Manifest);
        InteractionSearchResult result = await InteractionSearchEngine.RunAsync(
            seed, Baseline, Manifest, Evaluator, Manifest.ScoringPolicy,
            minimumMaterialImprovement: 0.0001m, maximumCombinationsPerGroup: 49);

        Assert.That(result.GroupResults, Has.Count.EqualTo(1));
        Assert.That(result.GroupResults[0].GroupId, Is.EqualTo("level-x-threshold"));
        Assert.That(result.GroupResults[0].WasCapped, Is.False, "5x5=25 combinations is within the 49 cap.");
        Assert.That(result.BestCandidate.RequireNumericValue("level"), Is.EqualTo(25m));
        Assert.That(result.BestCandidate.RequireNumericValue("threshold"), Is.EqualTo(55m));
    }

    [Test]
    public async Task InteractionSearchEngine_CapsCombinationsDeterministically_AndReportsTruncation()
    {
        CalibrationCandidate seed = CalibrationCandidate.FromDefaults(Manifest);
        InteractionSearchResult result = await InteractionSearchEngine.RunAsync(
            seed, Baseline, Manifest, Evaluator, Manifest.ScoringPolicy,
            minimumMaterialImprovement: 0.0001m, maximumCombinationsPerGroup: 4);

        Assert.That(result.GroupResults[0].WasCapped, Is.True);
        Assert.That(result.GroupResults[0].TotalCombinations, Is.EqualTo(25));
        Assert.That(result.GroupResults[0].EvaluatedCombinations, Is.EqualTo(4));
    }

    // ---- LocalRefiner ----

    [Test]
    public async Task LocalRefiner_FindsTheOffGridOptimum()
    {
        // Seed at the coarse-grid winner (25) - the true optimum (27) is only reachable via refinement.
        CalibrationCandidate seed = CalibrationCandidate.FromDefaults(Manifest)
            .WithNumericValue("level", 25m).WithNumericValue("threshold", 55m);
        LocalRefinementResult result = await LocalRefiner.RunAsync(
            seed, ["level"], Baseline, Manifest, Evaluator, Manifest.ScoringPolicy, minimumMaterialImprovement: 0.0001m);

        Assert.That(result.BestCandidate.RequireNumericValue("level"), Is.EqualTo(27m),
            "Refinement grid around 25 with step 1 and offsets -2..2 covers 23,24,25,26,27 - 27 is the true optimum.");
    }

    [Test]
    public async Task LocalRefiner_DropsOutOfBoundsCandidates()
    {
        // Level's hard minimum is 5; seeding at 6 with refinement step 1 means offset -2 => 4, out of bounds.
        CalibrationCandidate seed = CalibrationCandidate.FromDefaults(Manifest).WithNumericValue("level", 6m);
        LocalRefinementResult result = await LocalRefiner.RunAsync(
            seed, ["level"], Baseline, Manifest, Evaluator, Manifest.ScoringPolicy, minimumMaterialImprovement: 100m);

        Assert.That(result.Steps.Any(step => !step.IsValidCandidate && step.AttemptedValue == 4m), Is.True);
    }

    // ---- FoldCandidateSelector ----

    [Test]
    public void FoldCandidateSelector_SelectAmongStartingPoints_PicksHighestTrainingScore()
    {
        var outcomes = new[]
        {
            new StartingPointOutcome { StartingPointId = StartingPointIds.Conservative, Candidate = CalibrationCandidate.FromDefaults(Manifest), TrainingScore = 0.1m },
            new StartingPointOutcome { StartingPointId = StartingPointIds.Default, Candidate = CalibrationCandidate.FromDefaults(Manifest), TrainingScore = 0.5m },
            new StartingPointOutcome { StartingPointId = StartingPointIds.Permissive, Candidate = CalibrationCandidate.FromDefaults(Manifest), TrainingScore = 0.3m }
        };
        StartingPointOutcome selected = FoldCandidateSelector.SelectAmongStartingPoints(outcomes);
        Assert.That(selected.StartingPointId, Is.EqualTo(StartingPointIds.Default));
    }

    [Test]
    public void FoldCandidateSelector_TieOnTrainingScore_PrefersDefaultStartingPoint()
    {
        var outcomes = new[]
        {
            new StartingPointOutcome { StartingPointId = StartingPointIds.Permissive, Candidate = CalibrationCandidate.FromDefaults(Manifest), TrainingScore = 0.5m },
            new StartingPointOutcome { StartingPointId = StartingPointIds.Default, Candidate = CalibrationCandidate.FromDefaults(Manifest), TrainingScore = 0.5m }
        };
        StartingPointOutcome selected = FoldCandidateSelector.SelectAmongStartingPoints(outcomes);
        Assert.That(selected.StartingPointId, Is.EqualTo(StartingPointIds.Default));
    }

    [Test]
    public async Task FoldCandidateSelector_ImprovedCandidate_ClassifiesImproved()
    {
        var selected = new StartingPointOutcome
        {
            StartingPointId = StartingPointIds.Default,
            Candidate = CalibrationCandidate.FromDefaults(Manifest).WithNumericValue("level", 25m).WithNumericValue("threshold", 55m),
            TrainingScore = 0.5m
        };
        CalibrationCandidate baselineCandidate = CalibrationCandidate.FromDefaults(Manifest);
        FoldCandidateSelectionResult result = await FoldCandidateSelector.EvaluateOnHeldOutFoldAsync(
            foldId: 0, selected, baselineCandidate, Evaluator, Manifest.ScoringPolicy, Manifest.AcceptancePolicy);

        Assert.That(result.State, Is.EqualTo(FoldResultState.Improved));
        Assert.That(result.ValidationScore, Is.Not.Null);
        Assert.That(result.BaselineValidationScore, Is.Not.Null);
        Assert.That(result.ValidationScore!.Value, Is.GreaterThan(result.BaselineValidationScore!.Value));
    }

    [Test]
    public async Task FoldCandidateSelector_NoBetterThanBaseline_ClassifiesNoImprovement()
    {
        var selected = new StartingPointOutcome
        {
            StartingPointId = StartingPointIds.Default,
            Candidate = CalibrationCandidate.FromDefaults(Manifest), // identical to baseline
            TrainingScore = 0.4m
        };
        CalibrationCandidate baselineCandidate = CalibrationCandidate.FromDefaults(Manifest);
        FoldCandidateSelectionResult result = await FoldCandidateSelector.EvaluateOnHeldOutFoldAsync(
            foldId: 0, selected, baselineCandidate, Evaluator, Manifest.ScoringPolicy, Manifest.AcceptancePolicy);

        Assert.That(result.State, Is.EqualTo(FoldResultState.NoImprovement));
    }

    [Test]
    public async Task FoldCandidateSelector_InsufficientTrades_ClassifiesInsufficientEvidence()
    {
        var sparseEvaluator = new SyntheticCandidateEvaluator(_ => new BacktestEvaluationResult
        {
            TradeCount = 5, MedianExpectancyR = 1m, MaximumDrawdownR = 1m, ProfitFactor = 2m, DataQualityValid = true
        });
        var selected = new StartingPointOutcome
        {
            StartingPointId = StartingPointIds.Default,
            Candidate = CalibrationCandidate.FromDefaults(Manifest),
            TrainingScore = 0.5m
        };
        FoldCandidateSelectionResult result = await FoldCandidateSelector.EvaluateOnHeldOutFoldAsync(
            foldId: 0, selected, CalibrationCandidate.FromDefaults(Manifest), sparseEvaluator, Manifest.ScoringPolicy, Manifest.AcceptancePolicy);

        Assert.That(result.State, Is.EqualTo(FoldResultState.InsufficientEvidence));
    }

    // ---- CrossFoldAggregator ----

    [Test]
    public void CrossFoldAggregator_ConsistentFolds_ProducesStableAggregate()
    {
        FoldCandidateSelectionResult[] folds = Enumerable.Range(0, 5).Select(foldId => new FoldCandidateSelectionResult
        {
            FoldId = foldId,
            Candidate = CalibrationCandidate.FromDefaults(Manifest).WithNumericValue("level", 25m).WithNumericValue("threshold", 55m),
            SelectedStartingPointId = StartingPointIds.Default,
            State = FoldResultState.Improved,
            Reason = "test",
            TrainingScore = 0.5m,
            ValidationResult = null,
            ValidationScore = 0.5m,
            BaselineValidationResult = null,
            BaselineValidationScore = 0.4m
        }).ToArray();

        CrossFoldAggregationResult result = CrossFoldAggregator.Aggregate(folds, Manifest, Manifest.AcceptancePolicy);

        Assert.That(result.IsStable, Is.True);
        Assert.That(result.AggregatedCandidate.RequireNumericValue("level"), Is.EqualTo(25m));
        Assert.That(result.AggregatedCandidate.RequireNumericValue("threshold"), Is.EqualTo(55m));
        Assert.That(result.AcceptableFoldPercent, Is.EqualTo(100m));
    }

    [Test]
    public void CrossFoldAggregator_DisagreeingFolds_FallsBackToDefault()
    {
        // Three genuinely disagreeing clusters (2 folds want Level=15, 2 want Level=35, 1 wants
        // Level=10 - all more than 2x the refinement step apart) - the best cluster only reaches
        // 2/5 = 40% support, below the manifest's 50% minimum plateau support.
        FoldCandidateSelectionResult[] threeWaySplit =
        [
            BuildFold(0, 15m),
            BuildFold(1, 15m),
            BuildFold(2, 35m),
            BuildFold(3, 35m),
            BuildFold(4, 10m)
        ];

        CrossFoldAggregationResult result = CrossFoldAggregator.Aggregate(threeWaySplit, Manifest, Manifest.AcceptancePolicy);

        ParameterAggregationResult levelResult = result.ParameterResults.Single(item => item.ParameterId == "level");
        Assert.That(levelResult.UsedExistingValueFallback, Is.True,
            "No cluster reaches the 50% minimum plateau support (best is 2/5 = 40%) - fallback to default keeps this conservative.");
        Assert.That(result.IsStable, Is.False);
    }

    [Test]
    public void CrossFoldAggregator_NeverJustTakesTheLastFoldsWinner()
    {
        // Folds 0-3 agree on Level=25; fold 4 (the "last" one) is an outlier at Level=10.
        FoldCandidateSelectionResult[] folds =
        [
            BuildFold(0, 25m), BuildFold(1, 25m), BuildFold(2, 25m), BuildFold(3, 25m), BuildFold(4, 10m)
        ];

        CrossFoldAggregationResult result = CrossFoldAggregator.Aggregate(folds, Manifest, Manifest.AcceptancePolicy);

        ParameterAggregationResult levelResult = result.ParameterResults.Single(item => item.ParameterId == "level");
        Assert.That(levelResult.AggregatedValue, Is.EqualTo(25m), "The 4/5 majority plateau must win, not fold 4's outlier value.");
    }

    private static FoldCandidateSelectionResult BuildFold(int foldId, decimal level) => new()
    {
        FoldId = foldId,
        Candidate = CalibrationCandidate.FromDefaults(Manifest).WithNumericValue("level", level).WithNumericValue("threshold", 55m),
        SelectedStartingPointId = StartingPointIds.Default,
        State = FoldResultState.Improved,
        Reason = "test",
        TrainingScore = 0.5m,
        ValidationResult = null,
        ValidationScore = 0.5m,
        BaselineValidationResult = null,
        BaselineValidationScore = 0.4m
    };
}
