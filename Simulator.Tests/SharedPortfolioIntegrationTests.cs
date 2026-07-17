using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Conditions;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SharedPortfolioIntegrationTests
{
    [TestCase(StrategyExecutionMode.Sequential)]
    [TestCase(StrategyExecutionMode.ParallelWorkers)]
    public async Task SimultaneousStrategies_AreRankedAndResizedUnderOneHeatBudget(
        StrategyExecutionMode executionMode)
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = Enumerable.Range(0, 8)
            .Select(index => TestCandles.Create(
                instrument,
                start.AddMinutes(index),
                interval,
                1m,
                1.005m,
                0.995m,
                1m))
            .ToArray();
        string output = Path.Combine(
            Path.GetTempPath(),
            "tradinghub-shared-portfolio",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);

        try
        {
            var runtime = new BacktestRuntimeOptions
            {
                AccountMode = SimulationAccountMode.SharedPortfolioAccount,
                BaseInterval = interval,
                AnalysisBaseInterval = interval,
                AnalysisIntervals = [interval],
                WarmupDays = 0,
                // AGENT-03: TradingConditionOptions now defaults to Enabled=true; this test
                // exercises portfolio margin-netting, not session/spread gating, so opt out
                // explicitly to keep its original tested conditions.
                TradingConditions = new TradingConditionOptions { Enabled = false },
                StrategyExecutionMode = executionMode,
                PositionSizing = new PositionSizingOptions
                {
                    Mode = PositionSizingMode.FixedQuantity,
                    FixedQuantity = 50_000m,
                    MinimumQuantity = 1m,
                    QuantityStep = 1m,
                    Leverage = 20m
                },
                PortfolioRisk = new PortfolioRiskOptions
                {
                    MaximumTotalOpenRiskPercent = 0.75m,
                    MaximumPendingRiskPercent = 0.75m,
                    MaximumStrategyRiskPercent = 0.75m,
                    MaximumInstrumentRiskPercent = 0.75m,
                    MaximumCurrencyStopRiskPercent = 0.75m,
                    MaximumMarginUsagePercent = 30m,
                    MaximumSinglePositionMarginPercent = 10m,
                    MinimumUnallocatedMarginReservePercent = 30m,
                    MaximumOpenPositions = 3
                },
                ReplayChunkSize = 20,
                ProgressPublishIntervalMilliseconds = 50
            };
            var engine = new StreamingComparativeEngine(
                new EnumerableMarketCandleStream(candles),
                [
                    ("strategy-a", (ITradingAgent)new OneShotEntryAgent(interval), instrument),
                    ("strategy-b", (ITradingAgent)new OneShotEntryAgent(interval), instrument)
                ]);
            ComparativeSimulationResult result = await engine.RunAsync(
                new StreamingComparativeEngineOptions
                {
                    SimulationId = Guid.NewGuid(),
                    Instrument = instrument,
                    EvaluationFrom = start,
                    EvaluationTo = start.AddMinutes(candles.Length),
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
                    InputStreamId = "shared-test",
                    AnnotationOptions = new ChartAnnotationOptions
                    {
                        AtrPeriod = 3,
                        RsiPeriod = 3,
                        BollingerPeriod = 3,
                        HeavyAnalysisEveryCandles = 1
                    }
                },
                new HistoricalCandleRequest(
                    instrument,
                    interval,
                    start,
                    start.AddMinutes(candles.Length)));

            decimal[] quantities = result.Strategies
                .OrderBy(item => item.StrategyId, StringComparer.Ordinal)
                .Select(item => item.Result.Trades.Single().InitialQuantity)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(quantities, Is.EqualTo(new[] { 50_000m, 25_000m }));
                Assert.That(quantities.Sum() * 0.01m, Is.EqualTo(750m));
                Assert.That(quantities.Sum() * 0.01m / 100_000m * 100m,
                    Is.LessThanOrEqualTo(runtime.PortfolioRisk.MaximumTotalOpenRiskPercent));
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task OppositeDirectionStrategies_OnSameInstrument_NetMarginToApproximatelyZero()
    {
        (ComparativeSimulationResult result, decimal[] quantities, OrderSide[] sides) =
            await RunDirectedSharedPortfolioAsync(OrderSide.Buy, OrderSide.Sell);

        decimal expectedMargin = Math.Abs(
            quantities[0] * (sides[0] == OrderSide.Buy ? 1m : -1m) +
            quantities[1] * (sides[1] == OrderSide.Buy ? 1m : -1m)) / 20m;
        Assert.Multiple(() =>
        {
            Assert.That(quantities, Is.EqualTo(new[] { 50_000m, 50_000m }));
            Assert.That(expectedMargin, Is.EqualTo(0m));
            Assert.That(result.PortfolioPerformance!.PeakMarginUsed, Is.EqualTo(expectedMargin).Within(0.01m));
        });
    }

    [Test]
    public async Task SameDirectionStrategies_OnSameInstrument_MarginMatchesUnnettedSum()
    {
        (ComparativeSimulationResult result, decimal[] quantities, OrderSide[] sides) =
            await RunDirectedSharedPortfolioAsync(OrderSide.Buy, OrderSide.Buy);

        decimal expectedMargin = Math.Abs(
            quantities[0] * (sides[0] == OrderSide.Buy ? 1m : -1m) +
            quantities[1] * (sides[1] == OrderSide.Buy ? 1m : -1m)) / 20m;
        decimal naiveSummedMargin = quantities.Sum() / 20m;
        Assert.Multiple(() =>
        {
            Assert.That(quantities, Is.EqualTo(new[] { 50_000m, 50_000m }));
            Assert.That(expectedMargin, Is.EqualTo(naiveSummedMargin));
            Assert.That(result.PortfolioPerformance!.PeakMarginUsed, Is.EqualTo(expectedMargin).Within(0.01m));
        });
    }

    [Test]
    public async Task SingleStrategy_SharedPortfolioMargin_MatchesBaselineUnnettedValue()
    {
        (ComparativeSimulationResult result, decimal[] quantities, OrderSide[] _) =
            await RunDirectedSharedPortfolioAsync(OrderSide.Buy);

        decimal expectedMargin = quantities[0] / 20m;
        Assert.That(result.PortfolioPerformance!.PeakMarginUsed, Is.EqualTo(expectedMargin).Within(0.01m));
    }

    /// <summary>
    /// Runs one shared-portfolio-account simulation with one directed one-shot entry per
    /// requested side, flat pricing (so margin math is exact, not approximate), and portfolio
    /// risk caps loosened enough that no strategy's requested quantity gets resized down -
    /// isolating the margin-netting behaviour under test from the heat-budget resizing already
    /// covered by <see cref="SimultaneousStrategies_AreRankedAndResizedUnderOneHeatBudget"/>.
    /// </summary>
    private static async Task<(ComparativeSimulationResult Result, decimal[] Quantities, OrderSide[] Sides)>
        RunDirectedSharedPortfolioAsync(params OrderSide[] sides)
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = Enumerable.Range(0, 8)
            .Select(index => TestCandles.Create(instrument, start.AddMinutes(index), interval, 1m, 1m, 1m, 1m))
            .ToArray();
        string output = Path.Combine(
            Path.GetTempPath(),
            "tradinghub-shared-portfolio-margin",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);

        try
        {
            var runtime = new BacktestRuntimeOptions
            {
                AccountMode = SimulationAccountMode.SharedPortfolioAccount,
                BaseInterval = interval,
                AnalysisBaseInterval = interval,
                AnalysisIntervals = [interval],
                WarmupDays = 0,
                // AGENT-03: see comment on the other BacktestRuntimeOptions block above.
                TradingConditions = new TradingConditionOptions { Enabled = false },
                StrategyExecutionMode = StrategyExecutionMode.Sequential,
                PositionSizing = new PositionSizingOptions
                {
                    Mode = PositionSizingMode.FixedQuantity,
                    FixedQuantity = 50_000m,
                    MinimumQuantity = 1m,
                    QuantityStep = 1m,
                    Leverage = 20m
                },
                PortfolioRisk = new PortfolioRiskOptions
                {
                    MaximumTotalOpenRiskPercent = 100m,
                    MaximumPendingRiskPercent = 100m,
                    MaximumStrategyRiskPercent = 100m,
                    MaximumInstrumentRiskPercent = 100m,
                    MaximumCurrencyStopRiskPercent = 100m,
                    MaximumNetCurrencyExposurePercent = 1000m,
                    MaximumGrossCurrencyExposurePercent = 1000m,
                    MaximumMarginUsagePercent = 90m,
                    MaximumSinglePositionMarginPercent = 90m,
                    MinimumUnallocatedMarginReservePercent = 10m,
                    MaximumOpenPositions = 5
                },
                ReplayChunkSize = 20,
                ProgressPublishIntervalMilliseconds = 50
            };
            var engine = new StreamingComparativeEngine(
                new EnumerableMarketCandleStream(candles),
                sides.Select((side, index) =>
                        ($"strategy-{index}", (ITradingAgent)new DirectedEntryAgent(interval, side), instrument))
                    .ToArray());
            ComparativeSimulationResult result = await engine.RunAsync(
                new StreamingComparativeEngineOptions
                {
                    SimulationId = Guid.NewGuid(),
                    Instrument = instrument,
                    EvaluationFrom = start,
                    EvaluationTo = start.AddMinutes(candles.Length),
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
                    InputStreamId = "shared-margin-test",
                    AnnotationOptions = new ChartAnnotationOptions
                    {
                        AtrPeriod = 3,
                        RsiPeriod = 3,
                        BollingerPeriod = 3,
                        HeavyAnalysisEveryCandles = 1
                    }
                },
                new HistoricalCandleRequest(
                    instrument,
                    interval,
                    start,
                    start.AddMinutes(candles.Length)));

            decimal[] quantities = result.Strategies
                .OrderBy(item => item.StrategyId, StringComparer.Ordinal)
                .Select(item => item.Result.Trades.Single().InitialQuantity)
                .ToArray();
            return (result, quantities, sides);
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private sealed class DirectedEntryAgent(BarInterval interval, OrderSide side) : ITradingAgent
    {
        private bool _submitted;

        public string Name => "Directed shared-portfolio entry";
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
            bool isBuy = side == OrderSide.Buy;
            return Task.FromResult(new AgentDecision
            {
                DecisionId = $"entry:{context.StrategyId}",
                SetupId = $"setup:{context.StrategyId}",
                Action = isBuy ? AgentAction.Buy : AgentAction.Sell,
                Instrument = context.Instrument,
                SuggestedQuantity = 50_000m,
                ReferencePrice = 1m,
                StopLossPrice = isBuy ? 0.99m : 1.01m,
                TakeProfitPrice = isBuy ? 1.02m : 0.98m,
                ExpectedRewardRisk = 2m,
                Confidence = 70m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic directed shared-account margin-netting test entry"
            });
        }
    }

    private sealed class OneShotEntryAgent(BarInterval interval) : ITradingAgent
    {
        private bool _submitted;

        public string Name => "One-shot shared entry";
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
                SuggestedQuantity = 50_000m,
                ReferencePrice = 1m,
                StopLossPrice = 0.99m,
                TakeProfitPrice = 1.02m,
                ExpectedRewardRisk = 2m,
                Confidence = 70m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic shared-account test entry"
            });
        }
    }
}
