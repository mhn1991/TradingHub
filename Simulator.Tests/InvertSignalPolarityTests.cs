using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Covers <see cref="ProgressiveStrategyOptions.InvertSignalPolarity"/>, the research switch for
/// trading against the detected direction.
/// <para>
/// The assertion that actually matters is the geometry one. Flipping only the emitted
/// <c>AgentAction</c> would leave the bracket on the wrong side of entry, and
/// <c>PreTradeRiskManager</c> (<c>Buy =&gt; stopLoss &lt; reference</c>) would then reject every
/// order - yielding an empty backtest that reads as "the strategy found nothing" rather than
/// "the inversion is broken". These tests pin that the flip happens at side detection, so stop
/// and target follow it.
/// </para>
/// </summary>
[TestFixture]
public sealed class InvertSignalPolarityTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Five = BarInterval.Minutes(5);
    private static readonly BarInterval Fifteen = BarInterval.Minutes(15);
    private static readonly BarInterval Hour = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static ProgressiveStrategyOptions Options(bool invert) => new()
    {
        TrendInterval = Hour,
        ConfirmationInterval = Fifteen,
        EntryInterval = Five,
        MinimumRewardRisk = 1.0m,
        InvertSignalPolarity = invert
    };

    private static async Task<AgentDecision> RunAsync(bool invert, bool bullish)
    {
        var agent = new ImprovedProgressiveAgent(Options(invert));
        return await agent.EvaluateAsync(Context(
            Aligned(Five, bullish, confidence: 72m),
            Aligned(Fifteen, bullish, confidence: 70m),
            Aligned(Hour, bullish, confidence: 70m)));
    }

    [Test]
    public async Task Default_IsUnchanged()
    {
        // Guards the switch being off by default: every existing backtest must be unaffected.
        Assert.That(new ProgressiveStrategyOptions().InvertSignalPolarity, Is.False);

        AgentDecision normal = await RunAsync(invert: false, bullish: true);
        Assert.That(normal.Action, Is.Not.EqualTo(AgentAction.Sell), normal.Reason);
    }

    [Test]
    public async Task Inverted_FlipsTheSideForABullishSetup()
    {
        AgentDecision normal = await RunAsync(invert: false, bullish: true);
        AgentDecision inverted = await RunAsync(invert: true, bullish: true);

        if (normal.Action != AgentAction.Buy)
            Assert.Ignore($"Fixture did not reach a Buy under normal polarity: {normal.Reason}");

        Assert.That(inverted.Action, Is.EqualTo(AgentAction.Sell), inverted.Reason);
    }

    [Test]
    public async Task Inverted_FlipsTheSideForABearishSetup()
    {
        AgentDecision normal = await RunAsync(invert: false, bullish: false);
        AgentDecision inverted = await RunAsync(invert: true, bullish: false);

        if (normal.Action != AgentAction.Sell)
            Assert.Ignore($"Fixture did not reach a Sell under normal polarity: {normal.Reason}");

        Assert.That(inverted.Action, Is.EqualTo(AgentAction.Buy), inverted.Reason);
    }

    [Test]
    public async Task Inverted_KeepsBracketGeometryValidForTheFlippedSide()
    {
        // The regression that would otherwise silently empty a 7-month run.
        foreach (bool bullish in new[] { true, false })
        {
            AgentDecision decision = await RunAsync(invert: true, bullish: bullish);
            if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
                continue;

            Assert.That(decision.ReferencePrice, Is.Not.Null, decision.Reason);
            Assert.That(decision.StopLossPrice, Is.Not.Null, decision.Reason);
            Assert.That(decision.TakeProfitPrice, Is.Not.Null, decision.Reason);

            decimal entry = decision.ReferencePrice!.Value;
            decimal stop = decision.StopLossPrice!.Value;
            decimal target = decision.TakeProfitPrice!.Value;

            if (decision.Action == AgentAction.Buy)
            {
                Assert.That(stop, Is.LessThan(entry), $"Buy stop must sit below entry. {decision.Reason}");
                Assert.That(target, Is.GreaterThan(entry), $"Buy target must sit above entry. {decision.Reason}");
            }
            else
            {
                Assert.That(stop, Is.GreaterThan(entry), $"Sell stop must sit above entry. {decision.Reason}");
                Assert.That(target, Is.LessThan(entry), $"Sell target must sit below entry. {decision.Reason}");
            }
        }
    }

    [Test]
    public async Task Inverted_PreservesRiskAndRewardRisk()
    {
        // The property that makes the two arms comparable: same risk per trade and same R:R,
        // only the direction differs. Without it a difference in net R could just be a
        // difference in position geometry rather than in directional edge.
        AgentDecision normal = await RunAsync(invert: false, bullish: true);
        AgentDecision inverted = await RunAsync(invert: true, bullish: true);
        if (normal.Action is not (AgentAction.Buy or AgentAction.Sell))
            Assert.Ignore($"Fixture did not reach a trade: {normal.Reason}");

        decimal NormalRisk(AgentDecision decision) =>
            Math.Abs(decision.ReferencePrice!.Value - decision.StopLossPrice!.Value);
        decimal Reward(AgentDecision decision) =>
            Math.Abs(decision.TakeProfitPrice!.Value - decision.ReferencePrice!.Value);

        Assert.Multiple(() =>
        {
            Assert.That(NormalRisk(inverted), Is.EqualTo(NormalRisk(normal)), "risk distance must match");
            Assert.That(Reward(inverted), Is.EqualTo(Reward(normal)), "reward distance must match");
            Assert.That(inverted.ExpectedRewardRisk, Is.EqualTo(normal.ExpectedRewardRisk));
        });
    }

    [Test]
    public async Task Inverted_DoesNotSelfInvalidateAnOpenPosition()
    {
        // Regression for the defect that silently voided the first 7-month inverted run: the
        // position is deliberately against the evidence, so judging invalidation by the HELD side
        // closed every trade one bar after entry (589/589 exits were StructuralInvalidation,
        // average holding exactly 60s). Invalidation must be judged by the evidence side.
        var agent = new ImprovedProgressiveAgent(Options(invert: true));
        AgentMarketContext context = Context(
            Aligned(Five, bullish: true, confidence: 72m),
            Aligned(Fifteen, bullish: true, confidence: 70m),
            Aligned(Hour, bullish: true, confidence: 70m));

        // Bullish evidence under inversion means the agent holds a SHORT.
        var held = new BrokerPosition
        {
            PositionId = "p1",
            Instrument = Instrument,
            Side = OrderSide.Sell,
            Quantity = 1000m,
            AveragePrice = 110m
        };
        AgentMarketContext withPosition = context with { Positions = [held] };

        AgentDecision decision = await agent.EvaluateAsync(withPosition);

        Assert.That(decision.Action, Is.Not.EqualTo(AgentAction.Close),
            $"an inverted position must not invalidate against the evidence it inverts: {decision.Reason}");
    }

    [Test]
    public void Option_RoundTripsThroughAgentDefinition()
    {
        // The backtester configures agents through serialized definitions; a property that does
        // not survive the round trip would silently run the control arm twice.
        var options = Options(invert: true);
        Agent.Configuration.AgentDefinition definition =
            Agent.Configuration.AgentDefinition.FromProgressive(
                ProgressiveAgentKind.Improved, options);

        ProgressiveStrategyOptions restored = definition.ReadProgressiveOptions();

        Assert.That(restored.InvertSignalPolarity, Is.True);
    }

    private static AnalysisSnapshot Aligned(
        BarInterval interval,
        bool bullish,
        decimal confidence = 70m,
        decimal close = 110m,
        decimal atr = 2m)
    {
        decimal open = bullish ? close - 1m : close + 1m;
        return new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = interval,
            AvailableAt = Now,
            Version = 1,
            LatestCandle = TestCandles.Create(
                Instrument,
                Now.AddSeconds(-BarIntervalParser.ApproximateSeconds(interval)),
                interval,
                open,
                Math.Max(open, close) + 0.5m,
                Math.Min(open, close) - 0.5m,
                close),
            Indicators = new IndicatorSnapshot
            {
                Atr = atr,
                Rsi = bullish ? 58m : 42m,
                BollingerMiddle = close
            },
            Swings =
            [
                new SwingPoint
                {
                    Type = bullish ? SwingType.Low : SwingType.High,
                    Price = bullish ? close - 3m : close + 3m,
                    PivotTime = Now.AddMinutes(-40),
                    ConfirmedAt = Now.AddMinutes(-30),
                    Strength = 2
                }
            ],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot
            {
                Direction = bullish ? MarketStructureDirection.Rising : MarketStructureDirection.Falling,
                Strength = 75m
            },
            PriceAction = new PriceActionSnapshot
            {
                Bias = bullish ? PriceActionDirection.Bullish : PriceActionDirection.Bearish,
                BullishScore = bullish ? 60m : 20m,
                BearishScore = bullish ? 20m : 60m
            },
            Confidence = new ConfidenceScore { Total = confidence, Contributions = [] }
        };
    }

    private static AgentMarketContext Context(params AnalysisSnapshot[] snapshots) => new()
    {
        Instrument = Instrument,
        Timestamp = Now,
        Analysis = new MultiTimeframeAnalysis(
            Instrument,
            Now,
            snapshots.ToDictionary(snapshot => snapshot.Interval)),
        Account = new AccountSnapshot
        {
            AccountId = "a",
            Currency = "USD",
            Balance = 100_000m,
            Available = 100_000m,
            CanTrade = true
        },
        Positions = [],
        OpenOrders = []
    };
}
