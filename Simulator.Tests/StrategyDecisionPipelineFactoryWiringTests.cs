using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Regression proof that <see cref="StreamingComparativeEngine"/>'s production call site
/// actually constructs and passes a non-null, fully-populated <c>RuntimeFeaturePolicy</c>
/// through the new <c>IStrategyDecisionPipelineFactory</c> - not left dead like the
/// <c>MetaLabelModel</c> wiring bug fixed earlier this session (see
/// <see cref="MetaModelWiringEndToEndTests"/>).
/// </summary>
[TestFixture]
public sealed class StrategyDecisionPipelineFactoryWiringTests
{
    [Test]
    public async Task ProductionCallSite_PopulatesFeaturePolicyHash_AndReflectsRuntimeChanges()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildSyntheticTrend(instrument, interval, start, count: 60 * 4);

        ComparativeSimulationResult withDmi = await RunAsync(
            candles, instrument, start, start.AddHours(4), dmiConfirmationEnabled: true);
        ComparativeSimulationResult withoutDmi = await RunAsync(
            candles, instrument, start, start.AddHours(4), dmiConfirmationEnabled: false);

        Assert.That(withDmi.Strategies, Is.Not.Empty);
        Assert.That(withoutDmi.Strategies, Is.Not.Empty);

        foreach (StrategySimulationResult strategy in withDmi.Strategies)
        {
            Assert.That(strategy.FeaturePolicyHash, Is.Not.Null.And.Not.Empty,
                "The production call site did not populate a RuntimeFeaturePolicy - the pipeline factory is unused.");
        }

        string hashWithDmi = withDmi.Strategies[0].FeaturePolicyHash!;
        string hashWithoutDmi = withoutDmi.Strategies
            .Single(s => s.StrategyId == withDmi.Strategies[0].StrategyId)
            .FeaturePolicyHash!;

        Assert.That(hashWithDmi, Is.Not.EqualTo(hashWithoutDmi),
            "Changing Runtime.DmiConfirmationEnabled did not change the hashed feature policy reaching the pipeline factory.");
    }

    private static async Task<ComparativeSimulationResult> RunAsync(
        IReadOnlyList<Candle> candles,
        InstrumentKey instrument,
        DateTimeOffset from,
        DateTimeOffset to,
        bool dmiConfirmationEnabled)
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
            ("legacy", new LegacyProgressiveAgent(strategyOptions), instrument)
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
            StrategyExecutionMode = StrategyExecutionMode.Sequential,
            ProgressPublishIntervalMilliseconds = 100,
            ReplayChunkSize = 100,
            DmiConfirmationEnabled = dmiConfirmationEnabled
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
            InputStreamId = "wiring-test-stream",
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
