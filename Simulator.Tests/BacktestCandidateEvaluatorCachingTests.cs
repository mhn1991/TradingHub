using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// Proves <see cref="BacktestCandidateEvaluator{TOptions}"/> actually consults
/// <see cref="BacktestCandidateEvaluatorSettings.Cache"/> when one is supplied - the wiring that
/// makes pause/resume skip already-completed real backtests. Uses a short flat synthetic candle
/// series purely to keep this fast; the point is the cache hit/miss counters, not the result.
/// </summary>
[TestFixture]
public sealed class BacktestCandidateEvaluatorCachingTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-evaluator-caching-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A minimal flat synthetic series - the real engine's data-quality report chokes on a truly empty candle list.</summary>
    private static IReadOnlyList<Candle> BuildFlatCandles(InstrumentKey instrument, DateTimeOffset from, DateTimeOffset to)
    {
        var interval = BarInterval.Minutes(1);
        var candles = new List<Candle>();
        for (DateTimeOffset time = from; time < to; time += TimeSpan.FromMinutes(1))
        {
            candles.Add(new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = time,
                CloseTime = time + TimeSpan.FromMinutes(1),
                Prices = new Ohlc(1.1000m, 1.1002m, 1.0998m, 1.1000m),
                Volume = new MarketVolume(10m, VolumeKind.TickCount),
                IsComplete = true
            });
        }
        return candles;
    }

    [Test]
    public async Task EvaluateAsync_SameCandidateTwice_SecondCallIsACacheHit()
    {
        var instrument = new InstrumentKey("FX:EUR/USD");
        var manifest = new IndicatorConfluenceCalibrationManifest();
        var baseline = new IndicatorConfluenceOptions();
        var cache = new InMemoryCalibrationCandidateCache();
        DateTimeOffset from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);

        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var settings = new BacktestCandidateEvaluatorSettings
        {
            Instrument = instrument,
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            RuntimeTemplate = new BacktestRuntimeOptions
            {
                SourceKind = HistoricalDataSourceKind.InlineTestData,
                ExecutionInterval = BarInterval.Minutes(1),
                AnalysisBaseInterval = BarInterval.Minutes(1),
                AnalysisIntervals = [BarInterval.Minutes(15), BarInterval.Hours(1)],
                WarmupDays = 0,
                // StructuralConfluenceStrategyOptions defaults enable LiquiditySweepReversal/
                // SupplyDemandPullback/LiquidityBreakRetest, so BacktestRequest.Validate() requires
                // these two detectors on (see ValidateStructuralAnnotationRequirements).
                AnnotationOptions = new ChartAnnotationOptions
                {
                    Liquidity = new LiquidityCalculationProfile { Enabled = true },
                    SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true }
                }
            },
            Candles = BuildFlatCandles(instrument, from.AddHours(-1), to),
            WarmupDays = 0,
            Cache = cache
        };
        var factory = new BacktestCandidateEvaluatorFactory<IndicatorConfluenceOptions>(
            backtests,
            manifest,
            baseline,
            indicatorOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = new StructuralConfluenceStrategyOptions { IndicatorConfluence = indicatorOptions }
            },
            settings);

        ICandidateEvaluator evaluator = factory.CreateEvaluator(from, to);
        CalibrationCandidate candidate = CalibrationCandidate.FromEffectiveOptions(baseline, manifest);

        BacktestEvaluationResult first = await evaluator.EvaluateAsync(candidate);
        BacktestEvaluationResult second = await evaluator.EvaluateAsync(candidate);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Misses, Is.EqualTo(1), "The first evaluation must be a cache miss (nothing cached yet).");
            Assert.That(cache.Hits, Is.EqualTo(1), "The second, identical evaluation must be served from cache.");
            Assert.That(second, Is.EqualTo(first), "A cache hit must return the exact same value a fresh computation would have.");
        });
    }

    [Test]
    public async Task EvaluateAsync_DifferentCandidates_BothAreCacheMisses()
    {
        var instrument = new InstrumentKey("FX:EUR/USD");
        var manifest = new IndicatorConfluenceCalibrationManifest();
        var baseline = new IndicatorConfluenceOptions();
        var cache = new InMemoryCalibrationCandidateCache();
        DateTimeOffset from = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset to = new(2026, 1, 1, 0, 10, 0, TimeSpan.Zero);

        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var settings = new BacktestCandidateEvaluatorSettings
        {
            Instrument = instrument,
            StrategyType = TradingAgentTypeIds.StructuralConfluence,
            RuntimeTemplate = new BacktestRuntimeOptions
            {
                SourceKind = HistoricalDataSourceKind.InlineTestData,
                ExecutionInterval = BarInterval.Minutes(1),
                AnalysisBaseInterval = BarInterval.Minutes(1),
                AnalysisIntervals = [BarInterval.Minutes(15), BarInterval.Hours(1)],
                WarmupDays = 0,
                // StructuralConfluenceStrategyOptions defaults enable LiquiditySweepReversal/
                // SupplyDemandPullback/LiquidityBreakRetest, so BacktestRequest.Validate() requires
                // these two detectors on (see ValidateStructuralAnnotationRequirements).
                AnnotationOptions = new ChartAnnotationOptions
                {
                    Liquidity = new LiquidityCalculationProfile { Enabled = true },
                    SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true }
                }
            },
            Candles = BuildFlatCandles(instrument, from.AddHours(-1), to),
            WarmupDays = 0,
            Cache = cache
        };
        var factory = new BacktestCandidateEvaluatorFactory<IndicatorConfluenceOptions>(
            backtests,
            manifest,
            baseline,
            indicatorOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = new StructuralConfluenceStrategyOptions { IndicatorConfluence = indicatorOptions }
            },
            settings);

        ICandidateEvaluator evaluator = factory.CreateEvaluator(from, to);
        CalibrationCandidate first = CalibrationCandidate.FromEffectiveOptions(baseline, manifest);
        CalibrationCandidate second = first.WithNumericValue("minimum-adx", 30m);

        await evaluator.EvaluateAsync(first);
        await evaluator.EvaluateAsync(second);

        Assert.That(cache.Misses, Is.EqualTo(2));
        Assert.That(cache.Hits, Is.EqualTo(0));
    }
}
