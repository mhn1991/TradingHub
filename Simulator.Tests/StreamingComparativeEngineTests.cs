using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;
using Simulator.Jobs;

namespace Simulator.Tests;

[TestFixture]
public sealed class StreamingComparativeEngineTests
{
    [Test]
    public async Task Sequential_And_Parallel_Produce_Identical_Trade_Counts_On_Synthetic_Stream()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero); // Monday
        Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, count: 60 * 8); // 8 hours

        ComparativeSimulationResult sequential = await RunAsync(
            candles,
            instrument,
            start,
            start.AddHours(8),
            StrategyExecutionMode.Sequential);
        ComparativeSimulationResult parallel = await RunAsync(
            candles,
            instrument,
            start,
            start.AddHours(8),
            StrategyExecutionMode.ParallelWorkers);

        Assert.That(parallel.InputHash, Is.EqualTo(sequential.InputHash));
        Assert.That(parallel.ProcessedBaseCandles, Is.EqualTo(sequential.ProcessedBaseCandles));
        Assert.That(parallel.Strategies.Count, Is.EqualTo(sequential.Strategies.Count));

        foreach (StrategySimulationResult left in sequential.Strategies)
        {
            StrategySimulationResult right = parallel.Strategies.Single(item => item.StrategyId == left.StrategyId);
            Assert.That(right.Result.Trades.Count, Is.EqualTo(left.Result.Trades.Count), left.StrategyName);
            Assert.That(right.Result.NetProfit, Is.EqualTo(left.Result.NetProfit), left.StrategyName);
            Assert.That(right.Result.FinalBalance, Is.EqualTo(left.Result.FinalBalance), left.StrategyName);
        }
    }

    [Test]
    public async Task Application_Service_Starts_Job_And_Completes_With_Inline_Candles()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, count: 120);

        string tempRoot = Path.Combine(Path.GetTempPath(), "tradinghub-sim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        await using var service = new BacktestApplicationService(
            new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
            new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });

        var request = new BacktestRequest
        {
            Instrument = instrument,
            From = start,
            To = start.AddHours(2),
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
                ReplayChunkSize = 50
            }
        };

        ComparativeSimulationResult result = await service.RunToCompletionAsync(request);
        Assert.That(result.ProcessedBaseCandles, Is.GreaterThan(0));
        Assert.That(result.Strategies, Has.Count.EqualTo(2));
        Assert.That(File.Exists(Path.Combine(result.OutputDirectory, "manifest.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(result.OutputDirectory, "COMPLETE")), Is.True);

        SimulationJobSnapshot? snapshot = await service.GetAsync(result.SimulationId);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.Status, Is.EqualTo(SimulationJobStatus.Completed));
        Assert.That(snapshot.IsComplete, Is.True);
    }

    [Test]
    public async Task Prefetch_Stream_Preserves_Order_And_Count()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildSyntheticTrend(instrument, interval, start, 250);
        var inner = new EnumerableMarketCandleStream(candles);
        var prefetch = new PrefetchingCandleStream(inner, capacity: 32, lowWatermark: 8);
        var request = new HistoricalCandleRequest(instrument, interval, start, start.AddMinutes(250));

        var received = new List<MarketCandle>();
        await foreach (MarketCandle candle in prefetch.StreamAsync(request))
            received.Add(candle);

        Assert.That(received, Has.Count.EqualTo(250));
        for (int i = 1; i < received.Count; i++)
            Assert.That(received[i].OpenTime, Is.GreaterThan(received[i - 1].OpenTime));
    }

    [Test]
    public void Runtime_Options_Validate_Prefetch_Bounds()
    {
        var options = new BacktestRuntimeOptions
        {
            SourcePageSize = 5_000,
            PrefetchCapacity = 4_000,
            PrefetchLowWatermark = 1_000
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate(1));
    }

    [Test]
    public async Task No_Lookahead_Orders_Do_Not_Fill_On_Signal_Candle()
    {
        // Reuse the existing SimulationRunner guarantees via a single-strategy streaming session
        // with a tiny synthetic series where an entry on candle N must fill on N+1.
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildSyntheticTrend(instrument, baseInterval, start, count: 200);

        ComparativeSimulationResult result = await RunAsync(
            candles,
            instrument,
            start,
            start.AddMinutes(200),
            StrategyExecutionMode.Sequential);

        foreach (StrategySimulationResult strategy in result.Strategies)
        {
            foreach (SimulatedTradeRecord trade in strategy.Result.Trades)
            {
                if (trade.SignalCreatedAt != default && trade.OpenedAt is DateTimeOffset opened)
                {
                    Assert.That(opened, Is.GreaterThanOrEqualTo(trade.SignalCreatedAt),
                        "Fill must not precede the signal timestamp.");
                }
            }
        }
    }

    private static async Task<ComparativeSimulationResult> RunAsync(
        IReadOnlyList<Candle> candles,
        InstrumentKey instrument,
        DateTimeOffset from,
        DateTimeOffset to,
        StrategyExecutionMode mode)
    {
        string output = Path.Combine(Path.GetTempPath(), "tradinghub-engine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);

        var strategyOptions = new ProgressiveStrategyOptions
        {
            Quantity = 1_000m,
            MinimumRewardRisk = 1.2m
        };
        (string Id, Agent.Abstractions.ITradingAgent Agent, InstrumentKey Instrument)[] strategies =
        [
            ("legacy", new LegacyProgressiveAgent(strategyOptions), instrument),
            ("improved", new ImprovedProgressiveAgent(strategyOptions), instrument)
        ];

        var engine = new StreamingComparativeEngine(
            new EnumerableMarketCandleStream(candles),
            strategies);

        var runtime = new BacktestRuntimeOptions
        {
            BaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals =
            [
                BarInterval.Minutes(5),
                BarInterval.Minutes(15),
                BarInterval.Hours(1)
            ],
            WarmupDays = 0,
            PrefetchCapacity = 8_000,
            PrefetchLowWatermark = 1_000,
            SourcePageSize = 500,
            StrategyExecutionMode = mode,
            ProgressPublishIntervalMilliseconds = 100,
            ReplayChunkSize = 100
        };

        var options = new StreamingComparativeEngineOptions
        {
            SimulationId = Guid.NewGuid(),
            Instrument = instrument,
            EvaluationFrom = from,
            EvaluationTo = to,
            StreamFrom = from,
            AnalysisIntervals = runtime.AnalysisIntervals,
            Runtime = runtime,
            SimulationOptions = new SimulationOptions
            {
                BaseCurrency = "USD",
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0.00002m,
                SpreadBasisPoints = 1m,
                SlippageBasisPoints = 0.5m,
                CloseOpenPositionsAtEnd = true,
                BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets
            },
            OutputDirectory = output,
            InputStreamId = "test-stream",
            AnnotationOptions = new ChartAnnotationOptions
            {
                AtrPeriod = 5,
                RsiPeriod = 5,
                BollingerPeriod = 5,
                HeavyAnalysisEveryCandles = 12
            }
        };

        return await engine.RunAsync(
            options,
            new HistoricalCandleRequest(instrument, BarInterval.Minutes(1), from, to),
            CancellationToken.None);
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
            // Mild oscillating trend with occasional impulses so structure can form.
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
