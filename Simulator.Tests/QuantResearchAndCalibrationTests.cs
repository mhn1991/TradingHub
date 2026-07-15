using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using QuantResearch.Calibration;
using QuantResearch.Models;
using QuantResearch.Validation;
using RiskManager.Calibration;

namespace Simulator.Tests;

[TestFixture]
public sealed class QuantResearchAndCalibrationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void SetupCalibration_RejectsIncompatibleSchemaAndNeverRaisesBaseRisk()
    {
        SetupCalibrationArtifact artifact = Artifact([
            Bucket(50m, 60m, 40, -0.10m),
            Bucket(60m, 70m, 40, 0.10m),
            Bucket(70m, 80m, 40, 0.50m)
        ]);
        Assert.That(
            () => new SetupCalibrationPolicy(
                artifact,
                new SetupCalibrationPolicyOptions { Enabled = true },
                requiredFeatureSchemaHash: "other-schema"),
            Throws.TypeOf<ArgumentException>());

        var policy = new SetupCalibrationPolicy(artifact, new SetupCalibrationPolicyOptions
        {
            Enabled = true,
            MinimumSamples = 30,
            WeakPositiveExpectedRThreshold = 0.20m,
            WeakPositiveRiskMultiplier = 0.50m
        }, "schema-v1");
        SetupCalibrationDecision negative = policy.Evaluate("improved", "FX", "Range", 55m);
        SetupCalibrationDecision weak = policy.Evaluate("improved", "FX", "Range", 65m);
        SetupCalibrationDecision strong = policy.Evaluate("improved", "FX", "Range", 75m);

        Assert.Multiple(() =>
        {
            Assert.That(negative.Trade, Is.False);
            Assert.That(negative.RiskMultiplier, Is.Zero);
            Assert.That(weak.Trade, Is.True);
            Assert.That(weak.RiskMultiplier, Is.EqualTo(0.50m));
            Assert.That(strong.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(new[] { negative, weak, strong }.Max(item => item.RiskMultiplier),
                Is.LessThanOrEqualTo(1m));
        });
    }

    [Test]
    public void MetaLabel_FeaturesRejectFutureSnapshotsAndRiskIncrease()
    {
        AgentDecision decision = new()
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Confidence = 70m,
            CreatedAt = Start,
            Reason = "candidate"
        };
        AnalysisSnapshot future = Snapshot(Start.AddMinutes(1));
        var analysis = new MultiTimeframeAnalysis(
            decision.Instrument,
            Start,
            new Dictionary<BarInterval, AnalysisSnapshot> { [future.Interval] = future });

        Assert.Multiple(() =>
        {
            Assert.That(
                () => MetaLabelFeatureFactory.Create(decision, analysis),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                () => new MetaLabelDecision
                {
                    Trade = true,
                    Probability = 0.8m,
                    ModelVersion = "model-v1",
                    ReasonCode = "accept",
                    RiskMultiplier = 1.01m
                }.Validate(),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void WalkForwardAndPurgedCrossValidation_KeepTestLabelsOutOfTraining()
    {
        IReadOnlyList<WalkForwardFold> folds = WalkForwardPlanner.Create(
            Start,
            Start.AddDays(20),
            new WalkForwardPlan
            {
                TrainingWindow = TimeSpan.FromDays(5),
                ValidationWindow = TimeSpan.FromDays(2),
                TestWindow = TimeSpan.FromDays(2),
                Step = TimeSpan.FromDays(2),
                PurgeGap = TimeSpan.FromDays(1)
            });
        LabelledSample[] samples = Enumerable.Range(0, 12)
            .Select(index => new LabelledSample(
                index,
                Start.AddDays(index),
                Start.AddDays(index + 2)))
            .ToArray();
        IReadOnlyList<PurgedTimeSeriesFold<LabelledSample>> purged =
            PurgedTimeSeriesCrossValidator.Split(
                samples,
                item => item.From,
                item => item.To,
                folds: 3,
                embargo: TimeSpan.FromDays(1));

        Assert.Multiple(() =>
        {
            Assert.That(folds, Is.Not.Empty);
            Assert.That(folds.All(fold => fold.TrainingTo < fold.ValidationFrom &&
                fold.ValidationTo < fold.TestFrom), Is.True);
            Assert.That(purged.All(fold => fold.Training.All(training =>
            {
                DateTimeOffset testFrom = fold.Test.Min(item => item.From);
                DateTimeOffset testTo = fold.Test.Max(item => item.To);
                return training.To < testFrom - TimeSpan.FromDays(1) ||
                    training.From > testTo + TimeSpan.FromDays(1);
            })), Is.True);
        });
    }

    [Test]
    public void MonteCarlo_IsSeedDeterministic()
    {
        ResearchTrade[] trades = Enumerable.Range(0, 20)
            .Select(index => Trade(index, index % 3 == 0 ? -1m : 0.75m))
            .ToArray();
        var options = new MonteCarloOptions
        {
            Iterations = 40,
            Seed = 1234,
            Method = MonteCarloMethod.BlockBootstrap,
            BlockSize = 4
        };

        MonteCarloReport first = MonteCarloSimulator.Run(trades, options);
        MonteCarloReport second = MonteCarloSimulator.Run(trades, options);

        Assert.Multiple(() =>
        {
            Assert.That(second.Iterations, Is.EqualTo(first.Iterations));
            Assert.That(second.SafetyBreachProbability, Is.EqualTo(first.SafetyBreachProbability));
        });
    }

    [Test]
    public async Task AblationRunner_DisablesEveryRequiredFeatureExactlyOnce()
    {
        IReadOnlyList<FeatureAblationResult> result = await FeatureAblationRunner.RunAsync(
            new FeatureSwitches(),
            (features, _) => Task.FromResult(new ResearchPerformance
            {
                NetProfit = EnabledCount(features),
                AverageR = 0m,
                ProfitFactor = 0m,
                MaximumDrawdown = 0m,
                TradeCount = 0
            }));

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(14));
            Assert.That(result.Select(item => item.Feature).Distinct().Count(), Is.EqualTo(14));
            Assert.That(result.All(item => item.NetProfitDelta == 1m), Is.True);
        });
    }

    [Test]
    public void ConfidenceAndManagementCalibration_ProduceVersionedCohorts()
    {
        SetupOutcome[] outcomes =
        [
            new() { StrategyId = "improved", InstrumentGroup = "FX", Regime = "Range", Confidence = 65m, Won = true, RMultiple = 1m },
            new() { StrategyId = "improved", InstrumentGroup = "FX", Regime = "Range", Confidence = 68m, Won = false, RMultiple = -1m }
        ];
        IReadOnlyList<ConfidenceReliabilityPoint> reliability =
            ConfidenceCalibrator.Reliability(outcomes, 10m);
        ResearchTrade trade = Trade(1, 1m);
        var path = new TradePathObservation
        {
            Trade = trade,
            Path =
            [
                new TradePathPoint { BarsAfterEntry = 1, MfeR = 0.25m, MaeR = -0.20m },
                new TradePathPoint { BarsAfterEntry = 3, MfeR = 0.75m, MaeR = -0.20m },
                new TradePathPoint { BarsAfterEntry = 5, MfeR = 1.25m, MaeR = -0.20m }
            ]
        };
        TradeManager.TradeManagementCalibration management =
            TradeManagementCohortAnalyzer.Analyze([path], "management-v1", "data-hash", Start);

        Assert.Multiple(() =>
        {
            Assert.That(reliability.Single().Samples, Is.EqualTo(2));
            Assert.That(reliability.Single().ActualWinRate, Is.EqualTo(0.5m));
            Assert.That(management.SchemaVersion, Is.EqualTo(1));
            Assert.That(management.Cohorts.Single().WinnerMfe80PercentileByBar[5], Is.EqualTo(1.25m));
            Assert.That(management.Cohorts.Single().MedianBarsToMfeThreshold[0.5m], Is.EqualTo(3m));
        });
    }

    private static SetupCalibrationArtifact Artifact(IReadOnlyList<SetupCalibrationBucket> buckets) => new()
    {
        SchemaVersion = 1,
        CalibrationId = "setup-v1",
        TrainingFrom = Start.AddYears(-1),
        TrainingTo = Start.AddDays(-1),
        Instruments = ["FX:GBP/USD"],
        StrategyVersion = "strategy-v1",
        FeatureSchemaHash = "schema-v1",
        Parameters = new Dictionary<string, string>(),
        TotalSamples = buckets.Sum(item => item.Samples),
        CreatedAt = Start,
        DataHash = "data-hash",
        Buckets = buckets
    };

    private static SetupCalibrationBucket Bucket(
        decimal from,
        decimal to,
        int samples,
        decimal expectedR) => new()
    {
        StrategyId = "improved",
        InstrumentGroup = "FX",
        Regime = "Range",
        ConfidenceFrom = from,
        ConfidenceTo = to,
        Samples = samples,
        WinRate = 0.5m,
        AverageR = expectedR,
        ExpectedR = expectedR,
        BrierScore = 0.25m
    };

    private static AnalysisSnapshot Snapshot(DateTimeOffset availableAt) => new()
    {
        Instrument = new InstrumentKey("FX:GBP/USD"),
        Interval = BarInterval.Minutes(5),
        AvailableAt = availableAt,
        Version = 1,
        LatestCandle = TestCandles.Create(
            new InstrumentKey("FX:GBP/USD"),
            availableAt.AddMinutes(-5),
            BarInterval.Minutes(5),
            1m,
            1.01m,
            0.99m,
            1m),
        Indicators = new IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        MarketRegime = MarketRegimeSnapshot.Unknown,
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };

    private static ResearchTrade Trade(int index, decimal r) => new()
    {
        TradeId = $"trade-{index}",
        StrategyId = "improved",
        Instrument = "FX:GBP/USD",
        InstrumentGroup = "FX",
        Regime = "Range",
        SetupType = "RangeRejection",
        Direction = "Buy",
        Session = "London",
        VolatilityBucket = "Normal",
        Confidence = 65m,
        OpenedAt = Start.AddHours(index),
        ClosedAt = Start.AddHours(index + 1),
        RMultiple = r,
        MaximumFavourableExcursionR = Math.Max(0m, r + 0.25m),
        MaximumAdverseExcursionR = Math.Min(0m, r - 0.25m),
        StopDistance = 0.01m
    };

    private static int EnabledCount(FeatureSwitches value) => new[]
    {
        value.PriceAction,
        value.AdxDmi,
        value.RsiRelationship,
        value.BollingerContext,
        value.RegimeRouting,
        value.SecondaryTrend,
        value.SetupIntervals,
        value.CurrencyStrength,
        value.SessionFilter,
        value.ScaleOut,
        value.ProfitFloor,
        value.MfeGiveback,
        value.StructuralTrailing,
        value.AdaptiveSizing
    }.Count(item => item);

    private sealed record LabelledSample(int Id, DateTimeOffset From, DateTimeOffset To);
}
