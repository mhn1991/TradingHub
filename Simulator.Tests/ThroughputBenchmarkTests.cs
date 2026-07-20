using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Indicators;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// Manual candles/second diagnostics, not regression tests - deliberately [Explicit] so they
/// never run as part of the normal suite (timing-based, not correctness-based). Run with:
/// dotnet test Simulator.Tests/Simulator.Tests.csproj -c Release --filter "FullyQualifiedName~ThroughputBenchmarkTests"
/// </summary>
[TestFixture]
[Explicit("Timing benchmark, not a correctness test - run manually")]
public sealed class ThroughputBenchmarkTests
{
    [Test]
    public async Task Compare_CaptureMarketReplay_OnVsOff()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        const int candleCount = 100_000;
        Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, candleCount);

        (TimeSpan duration, long processed) replayOn = await RunAsync(
            candles, instrument, start, captureMarketReplay: true, strategy: "improved", annotationOptions: null);
        (TimeSpan duration, long processed) replayOff = await RunAsync(
            candles, instrument, start, captureMarketReplay: false, strategy: "improved", annotationOptions: null);

        decimal cpsOn = replayOn.processed / (decimal)replayOn.duration.TotalSeconds;
        decimal cpsOff = replayOff.processed / (decimal)replayOff.duration.TotalSeconds;

        TestContext.WriteLine($"CaptureMarketReplay=true : {replayOn.processed} candles in {replayOn.duration.TotalSeconds:F2}s = {cpsOn:F0} c/s");
        TestContext.WriteLine($"CaptureMarketReplay=false: {replayOff.processed} candles in {replayOff.duration.TotalSeconds:F2}s = {cpsOff:F0} c/s");
        TestContext.WriteLine($"Replay overhead: {(cpsOff / cpsOn - 1m) * 100m:F1}% slower with replay on");
    }

    [Test]
    public async Task Compare_StructuralConfluenceWithSupplyDemandLiquidity_VsPlainImproved()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        const int candleCount = 100_000;
        Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, candleCount);

        (TimeSpan duration, long processed) plain = await RunAsync(
            candles, instrument, start, captureMarketReplay: false, strategy: "improved", annotationOptions: null);
        (TimeSpan duration, long processed) structural = await RunAsync(
            candles, instrument, start, captureMarketReplay: false, strategy: "structural-confluence",
            annotationOptions: new ChartAnnotationOptions
            {
                Liquidity = new LiquidityCalculationProfile { Enabled = true },
                SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true }
            });

        decimal cpsPlain = plain.processed / (decimal)plain.duration.TotalSeconds;
        decimal cpsStructural = structural.processed / (decimal)structural.duration.TotalSeconds;

        TestContext.WriteLine($"improved (no S&D/liquidity)         : {plain.processed} candles in {plain.duration.TotalSeconds:F2}s = {cpsPlain:F0} c/s");
        TestContext.WriteLine($"structural-confluence (S&D+liquidity): {structural.processed} candles in {structural.duration.TotalSeconds:F2}s = {cpsStructural:F0} c/s");
        TestContext.WriteLine($"Structural+S&D/liquidity overhead: {(cpsPlain / cpsStructural - 1m) * 100m:F1}% slower than plain improved");
    }

    [Test]
    public async Task StructuralConfluenceWithSupplyDemandLiquidity_ScalingCheck()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var annotationOptions = new ChartAnnotationOptions
        {
            Liquidity = new LiquidityCalculationProfile { Enabled = true },
            SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true }
        };

        foreach (int count in new[] { 2_000, 4_000, 8_000, 16_000 })
        {
            Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, count);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);

            (TimeSpan duration, long processed, int trades) result = await RunAsyncWithTradeCount(
                candles, instrument, start, captureMarketReplay: false, strategy: "structural-confluence",
                annotationOptions: annotationOptions);

            long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
            int gen0After = GC.CollectionCount(0);
            int gen1After = GC.CollectionCount(1);
            int gen2After = GC.CollectionCount(2);
            long allocated = allocAfter - allocBefore;
            TestContext.WriteLine(
                $"{count,7} candles: {result.duration.TotalSeconds:F2}s = {result.processed / (decimal)result.duration.TotalSeconds:F0} c/s, trades={result.trades}, " +
                $"allocated={allocated / 1_000_000.0:F1}MB ({allocated / (double)count:F0} bytes/candle), " +
                $"gen0={gen0After - gen0Before} gen1={gen1After - gen1Before} gen2={gen2After - gen2Before}");
        }
    }

    private static async Task<(TimeSpan Duration, long Processed, int Trades)> RunAsyncWithTradeCount(
        Candle[] candles,
        InstrumentKey instrument,
        DateTimeOffset start,
        bool captureMarketReplay,
        string strategy,
        ChartAnnotationOptions? annotationOptions)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tradinghub-throughput-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await using var service = new BacktestApplicationService(
                new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
                new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });

            var request = new BacktestRequest
            {
                Instrument = instrument,
                From = start,
                To = start.AddMinutes(candles.Length),
                Strategies = [strategy],
                StartingBalance = 100_000m,
                Quantity = 1_000m,
                OutputDirectory = Path.Combine(tempRoot, "out"),
                JobsDirectory = Path.Combine(tempRoot, "jobs"),
                InlineCandles = candles,
                CaptureMarketReplay = captureMarketReplay,
                Runtime = new BacktestRuntimeOptions
                {
                    BaseInterval = BarInterval.Minutes(1),
                    AnalysisIntervals = [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)],
                    WarmupDays = 0,
                    PrefetchCapacity = 10_000,
                    PrefetchLowWatermark = 1_000,
                    SourcePageSize = 5_000,
                    StrategyExecutionMode = StrategyExecutionMode.Sequential,
                    ProgressPublishIntervalMilliseconds = 250,
                    ReplayChunkSize = 250,
                    AnnotationOptions = annotationOptions ?? new ChartAnnotationOptions()
                }
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ComparativeSimulationResult result = await service.RunToCompletionAsync(request);
            stopwatch.Stop();
            int tradeCount = result.Strategies.Sum(s => s.Result.Trades.Count);
            return (stopwatch.Elapsed, result.ProcessedBaseCandles, tradeCount);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Test]
    public void CciAnalysisState_RawUpdateCost()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        const int iterations = 500_000;

        var state = new CciAnalysisState(sampleCapacity: 2_000, pivotCapacity: 500);
        var noSwings = Array.Empty<SwingPoint>();
        var withSwing = new SwingPoint[1];

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            decimal cci = (decimal)Math.Sin(i / 11.0) * 150m;
            Candle candle = new()
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = start.AddMinutes(i),
                CloseTime = start.AddMinutes(i + 1),
                Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, 1.1m),
                Volume = new MarketVolume(100m, VolumeKind.Unknown),
                IsComplete = true
            };
            if (i % 50 == 0)
            {
                withSwing[0] = new SwingPoint
                {
                    PivotTime = start.AddMinutes(i - 5),
                    ConfirmedAt = start.AddMinutes(i),
                    Price = 1.1m + cci / 10000m,
                    Type = i % 100 == 0 ? SwingType.High : SwingType.Low,
                    Strength = 2
                };
                state.Update(candle, cci, withSwing, 0.001m);
            }
            else
            {
                state.Update(candle, cci, noSwings, 0.001m);
            }
        }
        stopwatch.Stop();

        double perCallMicroseconds = stopwatch.Elapsed.TotalMicroseconds / iterations;
        TestContext.WriteLine($"{iterations:N0} CciAnalysisState.Update calls in {stopwatch.Elapsed.TotalMilliseconds:F1}ms " +
            $"= {perCallMicroseconds:F3} us/call = {iterations / stopwatch.Elapsed.TotalSeconds:F0} calls/s");
    }

    private static async Task<(TimeSpan Duration, long Processed)> RunAsync(
        Candle[] candles,
        InstrumentKey instrument,
        DateTimeOffset start,
        bool captureMarketReplay,
        string strategy,
        ChartAnnotationOptions? annotationOptions)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tradinghub-throughput-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await using var service = new BacktestApplicationService(
                new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
                new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });

            var request = new BacktestRequest
            {
                Instrument = instrument,
                From = start,
                To = start.AddMinutes(candles.Length),
                Strategies = [strategy],
                StartingBalance = 100_000m,
                Quantity = 1_000m,
                OutputDirectory = Path.Combine(tempRoot, "out"),
                JobsDirectory = Path.Combine(tempRoot, "jobs"),
                InlineCandles = candles,
                CaptureMarketReplay = captureMarketReplay,
                Runtime = new BacktestRuntimeOptions
                {
                    BaseInterval = BarInterval.Minutes(1),
                    AnalysisIntervals = [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)],
                    WarmupDays = 0,
                    PrefetchCapacity = 10_000,
                    PrefetchLowWatermark = 1_000,
                    SourcePageSize = 5_000,
                    StrategyExecutionMode = StrategyExecutionMode.Sequential,
                    ProgressPublishIntervalMilliseconds = 250,
                    ReplayChunkSize = 250,
                    AnnotationOptions = annotationOptions ?? new ChartAnnotationOptions()
                }
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ComparativeSimulationResult result = await service.RunToCompletionAsync(request);
            stopwatch.Stop();
            return (stopwatch.Elapsed, result.ProcessedBaseCandles);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
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
            decimal close = price + wave + drift + (i % 23 == 0 ? 0.002m : 0m);
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
