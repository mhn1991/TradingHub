using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Regime;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// Proves the config wiring closed in this pass (BacktestRuntimeOptions.AnnotationOptions
/// reaching StreamingComparativeEngine, and MarketRegimeRouting reaching the strategies
/// CreateDefaultStrategies builds) actually runs end-to-end through the real application
/// service without crashing, with both regime classification and routing enabled.
/// </summary>
[TestFixture]
public sealed class RegimeEndToEndBacktestTests
{
    [Test]
    public async Task ApplicationService_CompletesWithRegimeClassificationAndRoutingEnabled()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, count: 400);

        string tempRoot = Path.Combine(Path.GetTempPath(), "tradinghub-sim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        await using var service = new BacktestApplicationService(
            new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
            new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });

        var request = new BacktestRequest
        {
            Instrument = instrument,
            From = start,
            To = start.AddHours(6),
            Strategies = ["legacy", "improved"],
            StartingBalance = 100_000m,
            Quantity = 1_000m,
            OutputDirectory = Path.Combine(tempRoot, "out"),
            JobsDirectory = Path.Combine(tempRoot, "jobs"),
            InlineCandles = candles,
            Runtime = new BacktestRuntimeOptions
            {
                BaseInterval = baseInterval,
                AnalysisIntervals =
                [
                    BarInterval.Minutes(5),
                    BarInterval.Minutes(15),
                    BarInterval.Hours(1)
                ],
                WarmupDays = 0,
                PrefetchCapacity = 10_000,
                PrefetchLowWatermark = 1_000,
                SourcePageSize = 500,
                StrategyExecutionMode = StrategyExecutionMode.Sequential,
                ProgressPublishIntervalMilliseconds = 50,
                ReplayChunkSize = 50,
                AnnotationOptions = new ChartAnnotationOptions
                {
                    AtrAnalysisMinimumSamples = 10,
                    BollingerWidthMinimumSamples = 10,
                    EfficiencyRatioAnalysisMinimumSamples = 10,
                    MarketRegime = new MarketRegimeOptions
                    {
                        Enabled = true,
                        MinimumConfirmationBars = 1,
                        MinimumPersistenceBars = 0,
                        AdxCalibrationMinimumSamples = 10
                    }
                },
                MarketRegimeRouting = new MarketRegimePolicyOptions { Enabled = true }
            }
        };

        ComparativeSimulationResult result = await service.RunToCompletionAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.ProcessedBaseCandles, Is.GreaterThan(0));
            Assert.That(result.Strategies, Has.Count.EqualTo(2));
            Assert.That(File.Exists(Path.Combine(result.OutputDirectory, "manifest.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(result.OutputDirectory, "COMPLETE")), Is.True);
        });

        SimulationJobSnapshot? snapshot = await service.GetAsync(result.SimulationId);
        Assert.That(snapshot?.Status, Is.EqualTo(SimulationJobStatus.Completed));
    }

    private static Candle[] BuildSyntheticTrend(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset start,
        int count)
    {
        var candles = new Candle[count];
        decimal price = 1.1000m;
        for (int i = 0; i < count; i++)
        {
            decimal wave = (decimal)Math.Sin(i / 17.0) * 0.0015m;
            decimal drift = i * 0.00001m;
            decimal open = price;
            decimal close = price + wave + drift + ((i % 23 == 0) ? 0.002m : 0m);
            decimal high = Math.Max(open, close) + 0.0004m;
            decimal low = Math.Min(open, close) - 0.0004m;
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
}
