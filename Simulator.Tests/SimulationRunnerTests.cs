using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using Simulator.Engine;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulationRunnerTests
{
    [Test]
    public async Task AgentOrder_FillsOnNextCandle_NotOnSignalCandle()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Candle[] candles =
        [
            TestCandles.Create(instrument, start, five, 100m, 101m, 99m, 100m),
            TestCandles.Create(instrument, start.AddMinutes(5), five, 110m, 112m, 109m, 111m),
            TestCandles.Create(instrument, start.AddMinutes(10), five, 120m, 121m, 119m, 120m)
        ];

        var agent = new BuyOnceAgent(five, quantity: 1m);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            instrument,
            candles,
            [five],
            agent,
            new SimulationOptions
            {
                StartingBalance = 10_000m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            },
            new ChartAnnotator.Engine.ChartAnnotationOptions
            {
                AtrPeriod = 2,
                RsiPeriod = 2,
                BollingerPeriod = 2,
                HeavyAnalysisEveryCandles = 1
            });

        SimulationResult result = await session.Runner.RunAsync();

        BrokerPosition position = result.OpenPositions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.SubmittedOrders, Is.EqualTo(1));
            Assert.That(result.FilledOrders, Is.EqualTo(1));
            Assert.That(position.AveragePrice, Is.EqualTo(110m));
            Assert.That(position.Quantity, Is.EqualTo(1m));
            Assert.That(agent.DecisionTime, Is.EqualTo(start.AddMinutes(5)));
        });
    }

    [Test]
    public async Task Runner_ProvidesSynchronizedFiveFifteenAndOneHourAnalysis()
    {
        InstrumentKey instrument = new("CRYPTO:BTC/USDT");
        BarInterval five = BarInterval.Minutes(5);
        BarInterval fifteen = BarInterval.Minutes(15);
        BarInterval hour = BarInterval.Hours(1);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Candle[] candles = Enumerable.Range(0, 12)
            .Select(index => TestCandles.Create(
                instrument,
                start.AddMinutes(index * 5),
                five,
                100m + index,
                102m + index,
                99m + index,
                101m + index))
            .ToArray();

        var agent = new RecordingAgent([five, fifteen, hour], hour);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            instrument,
            candles,
            [five, fifteen, hour],
            agent,
            new SimulationOptions
            {
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            },
            new ChartAnnotator.Engine.ChartAnnotationOptions
            {
                AtrPeriod = 2,
                RsiPeriod = 2,
                BollingerPeriod = 2,
                HeavyAnalysisEveryCandles = 1
            });

        await session.Runner.RunAsync();

        Assert.That(agent.LastContext, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(agent.LastContext!.Timestamp, Is.EqualTo(start.AddHours(1)));
            Assert.That(agent.LastContext.Analysis.Timeframes.Keys, Is.EquivalentTo(new[] { five, fifteen, hour }));
            Assert.That(agent.LastContext.Analysis.Get(five).AvailableAt, Is.LessThanOrEqualTo(agent.LastContext.Timestamp));
            Assert.That(agent.LastContext.Analysis.Get(fifteen).AvailableAt, Is.LessThanOrEqualTo(agent.LastContext.Timestamp));
            Assert.That(agent.LastContext.Analysis.Get(hour).AvailableAt, Is.EqualTo(agent.LastContext.Timestamp));
        });
    }

    private sealed class BuyOnceAgent(BarInterval interval, decimal quantity) : ITradingAgent
    {
        private bool _sent;
        public string Name => "Buy once";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;
        public DateTimeOffset? DecisionTime { get; private set; }

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            if (_sent)
            {
                return Task.FromResult(Observe(context));
            }

            _sent = true;
            DecisionTime = context.Timestamp;
            return Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = quantity,
                OrderType = StandardOrderType.Market,
                Confidence = 100m,
                CreatedAt = context.Timestamp,
                Reason = "Test entry"
            });
        }
    }

    private sealed class RecordingAgent(
        IEnumerable<BarInterval> required,
        BarInterval trigger) : ITradingAgent
    {
        public string Name => "Recording agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = required.ToHashSet();
        public BarInterval TriggerInterval { get; } = trigger;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;
        public AgentMarketContext? LastContext { get; private set; }

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(Observe(context));
        }
    }

    private static AgentDecision Observe(AgentMarketContext context) => new()
    {
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = "Test observation"
    };
}
