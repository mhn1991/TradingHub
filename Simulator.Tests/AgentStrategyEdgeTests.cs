using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AgentStrategyEdgeTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Five = BarInterval.Minutes(5);
    private static readonly BarInterval Fifteen = BarInterval.Minutes(15);
    private static readonly BarInterval Hour = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = SimulationTestHarness.DefaultStart.AddHours(1);

    [TestCase(0)]
    [TestCase(-1)]
    public void StrategyRejectsNonPositiveQuantity(decimal quantity)
    {
        Assert.That(
            () => new RuleBasedMultiTimeframeAgent(Five, Fifteen, Hour, quantity),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(-1)]
    [TestCase(101)]
    public void StrategyRejectsConfidenceOutsidePercentageRange(decimal confidence)
    {
        Assert.That(
            () => new RuleBasedMultiTimeframeAgent(
                Five,
                Fifteen,
                Hour,
                quantity: 1m,
                minimumConfidence: confidence),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void StrategyRejectsInvalidIntervals()
    {
        Assert.That(
            () => new RuleBasedMultiTimeframeAgent(default, Fifteen, Hour, 1m),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task MissingIndicators_ProducesObserveDecision()
    {
        var agent = Agent();
        AgentMarketContext context = Context(
            Snapshot(Five, bullish: true, ready: false),
            Snapshot(Fifteen, bullish: true),
            Snapshot(Hour, bullish: true));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Reason, Does.Contain("warming"));
        });
    }

    [Test]
    public async Task ConfidenceBelowThreshold_ProducesObserveDecision()
    {
        var agent = Agent(minimumConfidence: 80m);
        AgentMarketContext context = Context(
            Snapshot(Five, bullish: true, confidence: 70m),
            Snapshot(Fifteen, bullish: true, confidence: 70m),
            Snapshot(Hour, bullish: true, confidence: 70m));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Reason, Does.Contain("below"));
        });
    }

    [Test]
    public async Task AlignedBullishSnapshots_ProduceBuyWithAtrBasedProtection()
    {
        var agent = Agent();
        AgentMarketContext context = Context(
            Snapshot(Five, bullish: true, atr: 2m),
            Snapshot(Fifteen, bullish: true, atr: 2m),
            Snapshot(Hour, bullish: true, atr: 2m));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
            Assert.That(decision.SuggestedQuantity, Is.EqualTo(2m));
            Assert.That(decision.StopLossPrice, Is.EqualTo(107m));
            Assert.That(decision.TakeProfitPrice, Is.EqualTo(116m));
            Assert.That(decision.Confidence, Is.EqualTo(80m));
        });
    }

    [Test]
    public async Task AlignedBearishSnapshots_ProduceSellWithAtrBasedProtection()
    {
        var agent = Agent();
        AgentMarketContext context = Context(
            Snapshot(Five, bullish: false, atr: 2m),
            Snapshot(Fifteen, bullish: false, atr: 2m),
            Snapshot(Hour, bullish: false, atr: 2m));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Sell));
            Assert.That(decision.StopLossPrice, Is.EqualTo(103m));
            Assert.That(decision.TakeProfitPrice, Is.EqualTo(94m));
        });
    }

    [Test]
    public async Task ZeroAtr_UsesFallbackDistanceInsteadOfZeroWidthProtection()
    {
        var agent = Agent();
        AgentMarketContext context = Context(
            Snapshot(Five, bullish: true, atr: 0m),
            Snapshot(Fifteen, bullish: true),
            Snapshot(Hour, bullish: true));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
            Assert.That(decision.StopLossPrice, Is.LessThan(110m));
            Assert.That(decision.TakeProfitPrice, Is.GreaterThan(110m));
        });
    }

    [Test]
    public async Task MisalignedSnapshots_ProduceObserveDecision()
    {
        var agent = Agent();
        AgentMarketContext context = Context(
            Snapshot(Five, bullish: true),
            Snapshot(Fifteen, bullish: false),
            Snapshot(Hour, bullish: true));

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
    }

    [Test]
    public void EvaluationObservesCancellationAndNullContext()
    {
        var agent = Agent();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await agent.EvaluateAsync(
                    Context(
                        Snapshot(Five, true),
                        Snapshot(Fifteen, true),
                        Snapshot(Hour, true)),
                    cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(
                async () => await agent.EvaluateAsync(null!),
                Throws.TypeOf<ArgumentNullException>());
        });
    }

    [Test]
    public void MultiTimeframeAnalysis_GetAndTryGetHandleMissingIntervals()
    {
        AnalysisSnapshot snapshot = Snapshot(Five, true);
        var analysis = new MultiTimeframeAnalysis(
            Instrument,
            Now,
            new Dictionary<BarInterval, AnalysisSnapshot> { [Five] = snapshot });

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Get(Five), Is.SameAs(snapshot));
            Assert.That(analysis.TryGet(Hour, out _), Is.False);
            Assert.That(() => analysis.Get(Hour), Throws.TypeOf<KeyNotFoundException>());
        });
    }

    private static RuleBasedMultiTimeframeAgent Agent(decimal minimumConfidence = 60m) => new(
        Five,
        Fifteen,
        Hour,
        quantity: 2m,
        minimumConfidence: minimumConfidence);

    private static AgentMarketContext Context(params AnalysisSnapshot[] snapshots)
    {
        var timeframes = snapshots.ToDictionary(snapshot => snapshot.Interval);
        return new AgentMarketContext
        {
            Instrument = Instrument,
            Timestamp = Now,
            Analysis = new MultiTimeframeAnalysis(Instrument, Now, timeframes),
            Account = new AccountSnapshot { AccountId = "account", CanTrade = true },
            Positions = [],
            OpenOrders = []
        };
    }

    private static AnalysisSnapshot Snapshot(
        BarInterval interval,
        bool bullish,
        bool ready = true,
        decimal confidence = 80m,
        decimal? atr = 2m)
    {
        decimal open = bullish ? 100m : 110m;
        decimal close = bullish ? 110m : 100m;
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = Now,
            Version = 1,
            LatestCandle = TestCandles.Create(
                Instrument,
                Now.AddMinutes(-5),
                interval,
                open,
                Math.Max(open, close),
                Math.Min(open, close),
                close),
            Indicators = new IndicatorSnapshot
            {
                Atr = ready ? atr : null,
                Rsi = ready ? bullish ? 60m : 40m : null,
                BollingerMiddle = ready ? 105m : null
            },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            Confidence = new ConfidenceScore
            {
                Total = confidence,
                Contributions = []
            }
        };
    }
}
