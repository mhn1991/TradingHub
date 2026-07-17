using System.Globalization;
using Brokers.Models;
using ChartAnnotator.Engine;
using QuantResearch.Validation;
using QuantResearchRunner.Experiments;
using QuantResearchRunner.Models;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class WalkForwardExperimentRunnerTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval BaseInterval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RunAsync_FoldBoundaries_MatchWalkForwardPlannerExactly()
    {
        QuantResearchPlan plan = BuildPlan(TempDirectory());

        IReadOnlyList<WalkForwardFoldResult> results = await RunAsync(plan);

        IReadOnlyList<WalkForwardFold> expectedFolds = WalkForwardPlanner.Create(plan.From, plan.To, plan.WalkForward);
        Assert.That(results, Has.Count.EqualTo(expectedFolds.Count));
        for (int i = 0; i < results.Count; i++)
        {
            Assert.That(results[i].Window, Is.EqualTo(expectedFolds[i]));
            Assert.That(results[i].Instrument, Is.EqualTo(Instrument.Value));
            Assert.That(results[i].StrategyId, Is.EqualTo("improved"));
            Assert.That(results[i].Training, Is.Not.Null);
        }
    }

    [Test]
    public async Task RunAsync_TiedCandidates_SelectsFirstInSortedGridOrder()
    {
        // With no trades in the window, every parameter candidate produces identical
        // (empty) performance, so the deterministic tie-break must pick the first
        // candidate in ascending sorted order (1, not 2), regardless of grid input order.
        QuantResearchPlan plan = BuildPlan(TempDirectory()) with
        {
            ParameterGrid = new Dictionary<string, IReadOnlyList<decimal>>
            {
                ["MinimumRewardRisk"] = [2.0m, 1.0m]
            }
        };

        IReadOnlyList<WalkForwardFoldResult> results = await RunAsync(plan);

        Assert.That(results, Is.Not.Empty);
        foreach (WalkForwardFoldResult result in results)
        {
            decimal selected = decimal.Parse(
                result.SelectedParameters["MinimumRewardRisk"], CultureInfo.InvariantCulture);
            Assert.That(selected, Is.EqualTo(1.0m));
        }
    }

    [Test]
    public async Task RunAsync_Resume_RestoresSelectedParametersFromLedger()
    {
        string experimentDirectory = TempDirectory();
        QuantResearchPlan plan = BuildPlan(experimentDirectory);

        await using var service = BuildService(experimentDirectory);
        var ledger = new ExperimentLedger(experimentDirectory);

        IReadOnlyList<WalkForwardFoldResult> first = await WalkForwardExperimentRunner.RunAsync(plan, service, ledger);

        // Every training candidate for every fold must now be marked complete.
        foreach (WalkForwardFold fold in WalkForwardPlanner.Create(plan.From, plan.To, plan.WalkForward))
        {
            foreach (IReadOnlyDictionary<string, decimal> candidate in ParameterSensitivityRunner.Combinations(plan.ParameterGrid))
            {
                string key = ExperimentLedger.ComputeKey(
                    "training",
                    Instrument.Value,
                    "improved",
                    fold.Fold,
                    string.Join(',', candidate.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => $"{p.Key}={p.Value.ToString(CultureInfo.InvariantCulture)}")));
                Assert.That(await ledger.IsCompletedAsync(key), Is.True);
                Assert.That(
                    await ledger.TryGetResultAsync<WalkForwardExperimentRunner.CandidateResult>(key),
                    Is.Not.Null);
            }
        }

        IReadOnlyList<WalkForwardFoldResult> second = await WalkForwardExperimentRunner.RunAsync(plan, service, ledger);
        Assert.That(second, Has.Count.EqualTo(first.Count));
        for (int i = 0; i < first.Count; i++)
        {
            Assert.That(second[i].SelectedParameters, Is.EqualTo(first[i].SelectedParameters));
            Assert.That(second[i].Training.TradeCount, Is.EqualTo(first[i].Training.TradeCount));
            Assert.That(second[i].Validation.TradeCount, Is.EqualTo(first[i].Validation.TradeCount));
            Assert.That(second[i].Test.TradeCount, Is.EqualTo(first[i].Test.TradeCount));
        }
    }

    [Test]
    public async Task RunAsync_MultiInstrumentAndStrategy_ProducesCartesianFoldResults()
    {
        var secondInstrument = new InstrumentKey("FX:GBP/USD");
        QuantResearchPlan basePlan = BuildPlan(TempDirectory());
        Candle[] candles =
        [
            ..BuildCandles(Instrument, BaseInterval, Start, count: (int)(basePlan.To - Start).TotalMinutes),
            ..BuildCandles(secondInstrument, BaseInterval, Start, count: (int)(basePlan.To - Start).TotalMinutes)
        ];
        QuantResearchPlan plan = basePlan with
        {
            Instruments = [Instrument, secondInstrument],
            Strategies = ["improved", "legacy"],
            InlineCandles = candles
        };
        IReadOnlyList<WalkForwardFold> folds = WalkForwardPlanner.Create(plan.From, plan.To, plan.WalkForward);

        IReadOnlyList<WalkForwardFoldResult> results = await RunAsync(plan);

        Assert.That(results, Has.Count.EqualTo(folds.Count * 2 * 2));
        Assert.That(results.Select(r => r.Instrument).Distinct().Count(), Is.EqualTo(2));
        Assert.That(results.Select(r => r.StrategyId).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void SliceForWindow_IncludesWarmupBarsBeforeEvaluationFrom()
    {
        QuantResearchPlan plan = BuildPlan(TempDirectory());
        IReadOnlyList<WalkForwardFold> folds = WalkForwardPlanner.Create(plan.From, plan.To, plan.WalkForward);
        Assume.That(folds, Is.Not.Empty);

        DateTimeOffset streamFrom = plan.BaselineRuntime.ResolveWarmupFrom(folds[0].TestFrom);
        IReadOnlyList<Candle> testSlice = CandlePrefetchPlanner.SliceForWindow(
            plan.InlineCandles!, folds[0].TestFrom, folds[0].TestTo, streamFrom);

        Assert.Multiple(() =>
        {
            Assert.That(testSlice, Is.Not.Empty);
            Assert.That(testSlice.All(candle => candle.OpenTime >= streamFrom && candle.OpenTime < folds[0].TestTo), Is.True);
        });
    }

    private static async Task<IReadOnlyList<WalkForwardFoldResult>> RunAsync(QuantResearchPlan plan)
    {
        await using var service = BuildService(plan.OutputDirectory);
        return await WalkForwardExperimentRunner.RunAsync(plan, service, new ExperimentLedger(plan.OutputDirectory));
    }

    private static BacktestApplicationService BuildService(string root) => new(
        new FileSimulationJobRepository(Path.Combine(root, "jobs")),
        new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 4 });

    private static QuantResearchPlan BuildPlan(string outputDirectory)
    {
        DateTimeOffset to = Start.AddHours(20);
        Candle[] candles = BuildCandles(Instrument, BaseInterval, Start, count: (int)(to - Start).TotalMinutes);

        return new QuantResearchPlan
        {
            Instruments = [Instrument],
            Strategies = ["improved"],
            From = Start,
            To = to,
            WalkForward = new WalkForwardPlan
            {
                TrainingWindow = TimeSpan.FromHours(6),
                ValidationWindow = TimeSpan.FromHours(2),
                TestWindow = TimeSpan.FromHours(2),
                Step = TimeSpan.FromHours(10),
                PurgeGap = TimeSpan.Zero
            },
            ParameterGrid = new Dictionary<string, IReadOnlyList<decimal>>
            {
                ["MinimumRewardRisk"] = [1.5m]
            },
            FeatureSwitches = new FeatureSwitches(),
            OutputDirectory = outputDirectory,
            Seed = 17,
            InlineCandles = candles,
            BaselineRuntime = new BacktestRuntimeOptions
            {
                BaseInterval = BaseInterval,
                AnalysisIntervals = RecommendedSimulationDefaults.AnalysisIntervals,
                StrategyTimeframes = RecommendedSimulationDefaults.StrategyTimeframes,
                WarmupDays = 0,
                AnnotationOptions = new ChartAnnotationOptions
                {
                    AtrAnalysisMinimumSamples = 10,
                    BollingerWidthMinimumSamples = 10,
                    EfficiencyRatioAnalysisMinimumSamples = 10
                },
                PrefetchCapacity = 10_000,
                PrefetchLowWatermark = 1_000,
                SourcePageSize = 500,
                StrategyExecutionMode = StrategyExecutionMode.Sequential,
                ProgressPublishIntervalMilliseconds = 50,
                ReplayChunkSize = 50
            }
        };
    }

    private static Candle[] BuildCandles(InstrumentKey instrument, BarInterval interval, DateTimeOffset start, int count)
    {
        var candles = new Candle[count];
        decimal price = 1.1000m;
        for (int i = 0; i < count; i++)
        {
            decimal step = i % 60 < 50 ? 0.00035m : -0.00010m;
            decimal open = price;
            decimal close = price + step;
            decimal high = Math.Max(open, close) + 0.00015m;
            decimal low = Math.Min(open, close) - 0.00015m;
            DateTimeOffset openTime = start.AddMinutes(i);
            candles[i] = new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(open, high, low, close),
                Volume = new MarketVolume(100m, VolumeKind.Unknown),
                IsComplete = true
            };
            price = close;
        }

        return candles;
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
