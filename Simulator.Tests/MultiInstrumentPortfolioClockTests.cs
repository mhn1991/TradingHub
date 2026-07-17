using System.Runtime.CompilerServices;
using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using RiskManager;
using RiskManager.Conditions;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// Proves the §7 multi-instrument portfolio clock's engine mechanics (D1/D3/D4/D5/D9 in
/// the corrective-implementation plan): the chronological merge across per-instrument
/// streams, routing frames only to the strategies assigned to that instrument, and
/// per-instrument end-of-stream liquidation. Both traded instruments quote in the account
/// currency (USD) deliberately, so no currency-conversion-rate source is needed - that is
/// orthogonal to what these tests are proving. Full cross-instrument risk-engagement proof
/// (correlation/margin-netting/currency-exposure activating from real streamed data,
/// rather than the synthetic-decision proofs in CorrelationWiringTests.cs and
/// SharedPortfolioIntegrationTests.cs) is Stage 9 scope, not repeated here.
/// </summary>
[TestFixture]
public sealed class MultiInstrumentPortfolioClockTests
{
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");
    private static readonly InstrumentKey GbpUsd = new("FX:GBP/USD");

    [Test]
    public async Task TwoInstruments_EachStrategyTradesOnlyItsAssignedInstrument()
    {
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 8, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start, count: 8, price: 1.30m);

        var stream = new TwoInstrumentCandleStream(EurUsd, eurUsdCandles, GbpUsd, gbpUsdCandles);
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new OneShotBuyAgent(interval, 1.30m), GbpUsd)
            ]);

        string output = Path.Combine(
            Path.GetTempPath(), "tradinghub-multi-instrument", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            ComparativeSimulationResult result = await RunAsync(engine, output, EurUsd, interval, start, count: 8);

            StrategySimulationResult eurUsdStrategy = result.Strategies.Single(s => s.StrategyId == "strategy-eurusd");
            StrategySimulationResult gbpUsdStrategy = result.Strategies.Single(s => s.StrategyId == "strategy-gbpusd");

            Assert.Multiple(() =>
            {
                Assert.That(eurUsdStrategy.Result.Trades, Has.Count.EqualTo(1));
                Assert.That(gbpUsdStrategy.Result.Trades, Has.Count.EqualTo(1));
                Assert.That(eurUsdStrategy.Result.Trades[0].Instrument, Is.EqualTo(EurUsd),
                    "The EUR/USD strategy must never receive a GBP/USD frame.");
                Assert.That(gbpUsdStrategy.Result.Trades[0].Instrument, Is.EqualTo(GbpUsd),
                    "The GBP/USD strategy must never receive a EUR/USD frame.");
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task TwoInstruments_DifferentStreamLengths_EachLiquidatesOnItsOwnLastCandle()
    {
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        // GBP/USD's stream is deliberately shorter - its own last candle must still
        // trigger CloseOpenPositionsAtEnd liquidation for it, independent of EUR/USD
        // (which keeps streaming for longer) still being mid-run (D5). Both still get
        // enough bars for indicator warm-up (MarketDataQualityGate.RequireIndicatorsReady
        // defaults to true) - a too-short stream would mask a real D5 bug behind a
        // "never became ready to trade" false negative instead.
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 16, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start, count: 8, price: 1.30m);

        var stream = new TwoInstrumentCandleStream(EurUsd, eurUsdCandles, GbpUsd, gbpUsdCandles);
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new OneShotBuyAgent(interval, 1.30m), GbpUsd)
            ]);

        string output = Path.Combine(
            Path.GetTempPath(), "tradinghub-multi-instrument", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            ComparativeSimulationResult result = await RunAsync(engine, output, EurUsd, interval, start, count: 16);

            StrategySimulationResult eurUsdStrategy = result.Strategies.Single(s => s.StrategyId == "strategy-eurusd");
            StrategySimulationResult gbpUsdStrategy = result.Strategies.Single(s => s.StrategyId == "strategy-gbpusd");

            Assert.Multiple(() =>
            {
                Assert.That(eurUsdStrategy.Result.OpenPositions, Is.Empty,
                    "EUR/USD's own last candle must liquidate its open position.");
                Assert.That(gbpUsdStrategy.Result.OpenPositions, Is.Empty,
                    "GBP/USD's own (earlier) last candle must liquidate its open position too, " +
                    "not wait for EUR/USD's longer stream to finish.");
                Assert.That(eurUsdStrategy.Result.Trades, Has.Count.EqualTo(1));
                Assert.That(gbpUsdStrategy.Result.Trades, Has.Count.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task TwoInstruments_StaggeredWarmup_BothCompleteWithoutFaultingCalibration()
    {
        // GBP/USD's first candle only arrives well after EUR/USD has already crossed
        // EvaluationFrom - ChartAnnotationEngine creates chart state lazily per
        // ChartKey(instrument, interval), so a single global FreezeCalibration call
        // (the pre-§7 behaviour) would never re-fire for GBP/USD's later-created state.
        // This proves the per-instrument-first-crossing trigger added in Stage 2 covers
        // that case: the run must complete cleanly for both instruments.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 16, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start.AddMinutes(6), count: 8, price: 1.30m);

        var stream = new TwoInstrumentCandleStream(EurUsd, eurUsdCandles, GbpUsd, gbpUsdCandles);
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new OneShotBuyAgent(interval, 1.30m), GbpUsd)
            ]);

        string output = Path.Combine(
            Path.GetTempPath(), "tradinghub-multi-instrument", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            ComparativeSimulationResult result = await RunAsync(engine, output, EurUsd, interval, start, count: 16);

            Assert.Multiple(() =>
            {
                Assert.That(result.Strategies.Single(s => s.StrategyId == "strategy-eurusd").IsComplete, Is.True);
                Assert.That(result.Strategies.Single(s => s.StrategyId == "strategy-gbpusd").IsComplete, Is.True);
                Assert.That(
                    result.Strategies.Single(s => s.StrategyId == "strategy-gbpusd").Result.Trades,
                    Has.Count.EqualTo(1),
                    "GBP/USD must still be able to enter/manage a trade despite its late, staggered start.");
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task ApplicationService_CompletesWithStrategyAssignmentsAcrossTwoInstruments()
    {
        // Proves the §7 request schema (BacktestRequest.StrategyAssignments) actually
        // reaches the real public BacktestApplicationService end-to-end - not just the
        // engine constructed directly, as the other tests in this file do - the same
        // "class exists but isn't wired to the real request path" failure mode the audit
        // targets applies to this session's own new code too.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildFlatSeries(EurUsd, interval, start, count: 16, price: 1.10m)
            .Concat(BuildFlatSeries(GbpUsd, interval, start, count: 16, price: 1.30m))
            .ToArray();

        string tempRoot = Path.Combine(Path.GetTempPath(), "tradinghub-multi-instrument-app-service", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        await using var service = new BacktestApplicationService(
            new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
            new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });

        var request = new BacktestRequest
        {
            Instrument = EurUsd,
            From = start,
            To = start.AddMinutes(16),
            BaseCurrency = "USD",
            StartingBalance = 100_000m,
            Quantity = 1_000m,
            OutputDirectory = Path.Combine(tempRoot, "out"),
            JobsDirectory = Path.Combine(tempRoot, "jobs"),
            InlineCandles = candles,
            StrategyAssignments =
            [
                new StrategyInstrumentAssignment { StrategyType = "legacy", Instrument = EurUsd },
                new StrategyInstrumentAssignment { StrategyType = "improved", Instrument = GbpUsd }
            ],
            Runtime = new BacktestRuntimeOptions
            {
                BaseInterval = interval,
                AnalysisBaseInterval = interval,
                AnalysisIntervals = [interval],
                WarmupDays = 0,
                // AGENT-03: TradingConditionOptions now defaults to Enabled=true; this test
                // exercises multi-instrument routing, not session/spread gating, so opt out
                // explicitly to keep its original tested conditions.
                TradingConditions = new TradingConditionOptions { Enabled = false },
                PrefetchCapacity = 10_000,
                PrefetchLowWatermark = 1_000,
                SourcePageSize = 500,
                StrategyExecutionMode = StrategyExecutionMode.Sequential,
                ProgressPublishIntervalMilliseconds = 50,
                ReplayChunkSize = 50,
                PositionSizing = new PositionSizingOptions
                {
                    Mode = PositionSizingMode.FixedQuantity,
                    FixedQuantity = 1_000m,
                    MinimumQuantity = 1m,
                    QuantityStep = 1m,
                    Leverage = 20m
                }
            }
        };

        ComparativeSimulationResult result = await service.RunToCompletionAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.ProcessedBaseCandles, Is.GreaterThan(0));
            Assert.That(result.Strategies, Has.Count.EqualTo(2));
            StrategySimulationResult legacyStrategy = result.Strategies.Single(s => s.StrategyId == "legacy:FX:EUR/USD");
            StrategySimulationResult improvedStrategy = result.Strategies.Single(s => s.StrategyId == "improved:FX:GBP/USD");
            Assert.That(legacyStrategy.Result.Trades.All(trade => trade.Instrument == EurUsd), Is.True,
                "The legacy:FX:EUR/USD strategy must only ever trade EUR/USD.");
            Assert.That(improvedStrategy.Result.Trades.All(trade => trade.Instrument == GbpUsd), Is.True,
                "The improved:FX:GBP/USD strategy must only ever trade GBP/USD.");
        });
    }

    private static async Task<ComparativeSimulationResult> RunAsync(
        StreamingComparativeEngine engine,
        string output,
        InstrumentKey templateInstrument,
        BarInterval interval,
        DateTimeOffset start,
        int count)
    {
        var runtime = new BacktestRuntimeOptions
        {
            BaseInterval = interval,
            AnalysisBaseInterval = interval,
            AnalysisIntervals = [interval],
            WarmupDays = 0,
            // AGENT-03: see comment on the other BacktestRuntimeOptions block above.
            TradingConditions = new TradingConditionOptions { Enabled = false },
            ReplayChunkSize = 20,
            ProgressPublishIntervalMilliseconds = 50,
            PositionSizing = new PositionSizingOptions
            {
                Mode = PositionSizingMode.FixedQuantity,
                FixedQuantity = 1_000m,
                MinimumQuantity = 1m,
                QuantityStep = 1m,
                Leverage = 20m
            }
        };
        return await engine.RunAsync(
            new StreamingComparativeEngineOptions
            {
                SimulationId = Guid.NewGuid(),
                Instrument = templateInstrument,
                EvaluationFrom = start,
                EvaluationTo = start.AddMinutes(count),
                StreamFrom = start,
                AnalysisIntervals = runtime.AnalysisIntervals,
                Runtime = runtime,
                SimulationOptions = new SimulationOptions
                {
                    StartingBalance = 100_000m,
                    BaseCurrency = "USD",
                    Leverage = 20m,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m,
                    CloseOpenPositionsAtEnd = true,
                    BaseCandleGapPolicy = BaseCandleGapPolicy.Throw
                },
                OutputDirectory = output,
                InputStreamId = "multi-instrument-test",
                AnnotationOptions = new ChartAnnotationOptions
                {
                    AtrPeriod = 3,
                    RsiPeriod = 3,
                    BollingerPeriod = 3,
                    HeavyAnalysisEveryCandles = 1
                }
            },
            // The template request's own Instrument is irrelevant beyond this point -
            // the engine derives one HistoricalCandleRequest per traded instrument from it.
            new HistoricalCandleRequest(templateInstrument, interval, start, start.AddMinutes(count)));
    }

    private static Candle[] BuildFlatSeries(
        InstrumentKey instrument, BarInterval interval, DateTimeOffset start, int count, decimal price) =>
        Enumerable.Range(0, count)
            .Select(index => TestCandles.Create(
                instrument, start.AddMinutes(index), interval, price, price, price, price))
            .ToArray();

    private sealed class OneShotBuyAgent(BarInterval interval, decimal referencePrice) : ITradingAgent
    {
        private bool _submitted;

        public string Name => "Multi-instrument one-shot entry";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_submitted)
            {
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "Already submitted"
                });
            }
            _submitted = true;
            return Task.FromResult(new AgentDecision
            {
                DecisionId = $"entry:{context.StrategyId}",
                SetupId = $"setup:{context.StrategyId}",
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = 1_000m,
                ReferencePrice = referencePrice,
                StopLossPrice = referencePrice * 0.99m,
                TakeProfitPrice = referencePrice * 1.05m,
                ExpectedRewardRisk = 5m,
                Confidence = 70m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic multi-instrument test entry"
            });
        }
    }

    private sealed class TwoInstrumentCandleStream(
        InstrumentKey firstInstrument,
        Candle[] firstCandles,
        InstrumentKey secondInstrument,
        Candle[] secondCandles) : IHistoricalCandleStream
    {
        public async IAsyncEnumerable<MarketCandle> StreamAsync(
            HistoricalCandleRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Candle[] source = request.Instrument == firstInstrument
                ? firstCandles
                : request.Instrument == secondInstrument
                    ? secondCandles
                    : [];
            foreach (Candle candle in source
                         .Where(item => item.OpenTime >= request.From && item.OpenTime < request.To)
                         .OrderBy(item => item.OpenTime))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MarketCandle.FromMid(candle);
                await Task.Yield();
            }
        }
    }
}
