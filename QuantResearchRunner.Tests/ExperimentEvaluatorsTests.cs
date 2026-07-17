using Brokers.Models;
using ChartAnnotator.Engine;
using QuantResearch.Models;
using QuantResearch.Validation;
using QuantResearchRunner.Experiments;
using QuantResearchRunner.Models;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace QuantResearchRunner.Tests;

/// <summary>
/// Proves the ablation/sensitivity "evaluate" closures actually drive real backtests through
/// BacktestApplicationService (writing a real COMPLETE marker per run) rather than being
/// stubbed math - the exact gap the audit's Section 14 flagged (FeatureAblationRunner/
/// ParameterSensitivityRunner are fully built but no caller anywhere supplied a real closure).
/// </summary>
[TestFixture]
public sealed class ExperimentEvaluatorsTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval BaseInterval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task AblationEvaluator_RunsARealBacktest_PerVariant()
    {
        QuantResearchPlan plan = BuildPlan(TempDirectory());
        await using var service = BuildService(plan.OutputDirectory);
        var evaluate = ExperimentEvaluators.BuildAblationEvaluator(plan, service);

        IReadOnlyList<FeatureAblationResult> results = await FeatureAblationRunner.RunAsync(plan.FeatureSwitches, evaluate);

        // 14 switches -> 14 ablation variants, each a real, independently-completed run.
        Assert.That(results, Has.Count.EqualTo(14));
        string runsRoot = Path.Combine(plan.OutputDirectory, "runs");
        Assert.That(Directory.Exists(runsRoot), Is.True);
        string[] completeMarkers = Directory.GetFiles(runsRoot, "COMPLETE", SearchOption.AllDirectories);
        // Baseline + 14 variants = 15 real runs.
        Assert.That(completeMarkers, Has.Length.EqualTo(15));
    }

    [Test]
    public async Task SensitivityEvaluator_RunsARealBacktest_PerGridPoint()
    {
        QuantResearchPlan plan = BuildPlan(TempDirectory()) with
        {
            ParameterGrid = new Dictionary<string, IReadOnlyList<decimal>>
            {
                ["MinimumRewardRisk"] = [1.0m, 1.5m]
            }
        };
        await using var service = BuildService(plan.OutputDirectory);
        var evaluate = ExperimentEvaluators.BuildSensitivityEvaluator(plan, service);

        IReadOnlyList<ParameterSensitivityPoint> results = await ParameterSensitivityRunner.RunAsync(plan.ParameterGrid, evaluate);

        Assert.That(results, Has.Count.EqualTo(2));
        string runsRoot = Path.Combine(plan.OutputDirectory, "runs");
        string[] completeMarkers = Directory.GetFiles(runsRoot, "COMPLETE", SearchOption.AllDirectories);
        Assert.That(completeMarkers, Has.Length.EqualTo(2));
    }

    private static BacktestApplicationService BuildService(string root) => new(
        new FileSimulationJobRepository(Path.Combine(root, "jobs")),
        new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 4 });

    private static QuantResearchPlan BuildPlan(string outputDirectory)
    {
        DateTimeOffset to = Start.AddHours(4);
        Candle[] candles = BuildCandles(Instrument, BaseInterval, Start, count: (int)(to - Start).TotalMinutes);

        return new QuantResearchPlan
        {
            Instruments = [Instrument],
            Strategies = ["improved"],
            From = Start,
            To = to,
            WalkForward = new WalkForwardPlan
            {
                TrainingWindow = TimeSpan.FromHours(1),
                ValidationWindow = TimeSpan.FromHours(1),
                TestWindow = TimeSpan.FromHours(1),
                Step = TimeSpan.FromHours(1),
                PurgeGap = TimeSpan.Zero
            },
            ParameterGrid = new Dictionary<string, IReadOnlyList<decimal>>(),
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
                Volume = new MarketVolume(100 + i, VolumeKind.Unknown),
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
