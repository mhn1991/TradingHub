using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using Simulator.Models;
using TradeManager;

namespace Simulator.Tests;

/// <summary>
/// The Set and Forget agent's contract: what it places, and - just as importantly - what it refuses
/// to do. Several of these pin rules the course states as prohibitions, which are the ones a later
/// refactor is most likely to erode without anyone noticing.
/// </summary>
[TestFixture]
public sealed class AlfonsoAgentTests
{
    private static readonly InstrumentKey Instrument = new("METAL:XAU/USD");
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly BarInterval Top = BarInterval.Hours(4);
    private static readonly BarInterval Middle = BarInterval.Hours(1);
    private static readonly BarInterval Lower = BarInterval.Minutes(15);

    [Test]
    public void DeclaresBracketExitsBecauseTheMethodForbidsManagement()
    {
        // Module 11: "Do not move the stop loss to breakeven. It's either a win or a loss." A mode
        // that hands the agent an exit path would invite exactly the management the course forbids.
        AlfonsoAgent agent = new();

        Assert.That(agent.ExitManagementMode, Is.EqualTo(AgentExitManagementMode.Bracket));
        Assert.That(agent.TriggerInterval, Is.EqualTo(Lower));
        Assert.That(agent.RequiredIntervals, Is.EquivalentTo(new[] { Top, Middle, Lower }));
    }

    [Test]
    public void SimulatorRoutesAlfonsoToBracketOnlyPositionManagement()
    {
        BacktestRuntimeOptions runtime = new();

        foreach (string strategyId in new[] { "alfonso", "alfonso:METAL:XAU/USD" })
        {
            PositionManagementOptions options = runtime.GetPositionManagement(strategyId);

            Assert.That(options, Is.SameAs(runtime.AlfonsoPositionManagement));
            Assert.Multiple(() =>
            {
                Assert.That(options.Mode, Is.EqualTo(TrailingStopMode.Disabled));
                Assert.That(options.EvaluateMechanicalProtectionOnEveryExecutionFrame, Is.False);
                Assert.That(options.PreserveBracketTarget, Is.True);
                Assert.That(options.ExitOnAdverseStructureBreak, Is.False);
                Assert.That(options.EnableNeoWaveInvalidationExit, Is.False);
                Assert.That(options.SupplyDemandManagementEnabled, Is.False);
                Assert.That(options.LiquidityManagementEnabled, Is.False);
                Assert.That(options.EnableScaleOut, Is.False);
                Assert.That(options.EnableProfitFloor, Is.False);
                Assert.That(options.EnableMaximumGiveback, Is.False);
                Assert.That(options.EnableStagnationReduction, Is.False);
                Assert.That(options.EnableStructuralDeteriorationReduction, Is.False);
                Assert.That(options.EnableMomentumDecayReduction, Is.False);
                Assert.That(options.EnableVolatilityExhaustionReduction, Is.False);
                Assert.That(options.EnableRegimeDegradationReduction, Is.False);
                Assert.That(options.EnableRiskWindowReduction, Is.False);
                Assert.That(options.EnableExecutionCostStressReduction, Is.False);
            });
        }

        Assert.DoesNotThrow(() => runtime.Validate());
    }

    [TestCase(ImbalanceKind.Demand, 100, 101, true)]
    [TestCase(ImbalanceKind.Demand, 100, 99, false)]
    [TestCase(ImbalanceKind.Supply, 100, 99, true)]
    [TestCase(ImbalanceKind.Supply, 100, 101, false)]
    public void EntryOrderIsRestedBeforePriceReachesTheZone(
        ImbalanceKind side, decimal proximal, decimal price, bool expected)
    {
        Assert.That(AlfonsoAgent.IsAheadOfPrice(side, proximal, price), Is.EqualTo(expected));
    }

    [Test]
    public void RequiredIntervalsFollowTheConfiguredSequence()
    {
        // Module 8 offers five sequences and leaves the choice open, so switching must not need a
        // code change.
        AlfonsoAgent agent = new(new AlfonsoStrategyOptions
        {
            TopInterval = BarInterval.Days(1),
            MiddleInterval = BarInterval.Hours(4),
            LowerInterval = BarInterval.Hours(1)
        });

        Assert.That(agent.TriggerInterval, Is.EqualTo(BarInterval.Hours(1)));
        Assert.That(agent.RequiredIntervals, Is.EquivalentTo(
            new[] { BarInterval.Days(1), BarInterval.Hours(4), BarInterval.Hours(1) }));
    }

    [Test]
    public void ASequenceThatIsNotLargestToSmallestIsRejectedAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new AlfonsoAgent(new AlfonsoStrategyOptions
        {
            TopInterval = BarInterval.Minutes(15),
            MiddleInterval = BarInterval.Hours(1),
            LowerInterval = BarInterval.Hours(4)
        }));
    }

    [Test]
    public async Task ObservesWhileAnyTimeframeIsMissing()
    {
        AlfonsoAgent agent = new();
        AgentDecision decision = await agent.EvaluateAsync(Context(Snapshot(Lower, 100m)));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
        Assert.That(decision.Reason, Does.Contain("analysis"));
    }

    [Test]
    public async Task ObservesWithoutTradingWhileNoSequenceAlignmentExists()
    {
        // A fresh sequence has no trend anywhere, so module 10's waiting game applies. This is the
        // normal state, not an error.
        AlfonsoAgent agent = new();
        AgentDecision decision = await agent.EvaluateAsync(
            Context(Snapshot(Top, 2000m), Snapshot(Middle, 2000m), Snapshot(Lower, 2000m)));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
    }

    [Test]
    public async Task NeverActsWhileAPositionIsOpen()
    {
        // There is no management path by design: "you just set the trade up and then forget about
        // it until the trade is triggered, either for a win or a loss."
        AlfonsoAgent agent = new();
        AgentMarketContext context = Context(
            [Snapshot(Top, 2000m), Snapshot(Middle, 2000m), Snapshot(Lower, 2000m)],
            positions:
            [
                new BrokerPosition
                {
                    PositionId = "position-1",
                    Instrument = Instrument,
                    Quantity = 1_000m,
                    Side = OrderSide.Buy,
                    AveragePrice = 2000m
                }
            ]);

        AgentDecision decision = await agent.EvaluateAsync(context);

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
        Assert.That(decision.Reason, Does.Contain("bracket owns it"));
    }

    /// <summary>
    /// The geometry the agent must emit, checked directly off a zone so the arithmetic is pinned
    /// independently of whether a sequence happens to align in a synthetic fixture.
    /// </summary>
    [Test]
    public void OrderGeometryIsAProximalEntryAPaddedStopAndAFixedThreeToOneTarget()
    {
        Imbalance zone = new()
        {
            Interval = TimeSpan.FromMinutes(15),
            Kind = ImbalanceKind.Demand,
            Proximal = 4238.87m,
            Distal = 4232.24m,
            BaseStart = Now,
            BaseEnd = Now,
            DistalAt = Now,
            ConfirmedAt = Now,
            BaseCandleCount = 2,
            Strength = ImpulseStrength.Strong,
            Accomplished = Accomplishment.TrendlineBreak,
            ImpulseToBaseRatio = 3m,
            ImpulseDisplacement = 20m,
            ImpulseBarsTracked = 2,
            IsContinuationPattern = false,
            MeetsTradeabilityCriteria = true
        };

        ImbalanceOptions options = new();
        decimal stop = zone.StopPrice(options.StopPaddingFraction);
        decimal target = zone.TargetPrice(options.StopPaddingFraction, options.RewardMultiple);

        // Module 10 pads the stop by 25% of the zone width beyond the distal line.
        Assert.That(stop, Is.EqualTo(zone.Distal - (zone.Width * 0.25m)));
        Assert.That(stop, Is.LessThan(zone.Distal), "protection sits beyond the level, not on it");

        // Module 11: "Exit at a fixed target of 3:1, three times the width of the imbalance
        // including the padding."
        decimal risk = zone.Proximal - stop;
        Assert.That(target - zone.Proximal, Is.EqualTo(risk * 3m));
    }

    [Test]
    public void SupplyGeometryMirrorsDemand()
    {
        Imbalance zone = new()
        {
            Interval = TimeSpan.FromMinutes(15),
            Kind = ImbalanceKind.Supply,
            Proximal = 2000m,
            Distal = 2010m,
            BaseStart = Now,
            BaseEnd = Now,
            DistalAt = Now,
            ConfirmedAt = Now,
            BaseCandleCount = 2,
            Strength = ImpulseStrength.Gap,
            Accomplished = Accomplishment.ExtremeBroken,
            ImpulseToBaseRatio = 4m,
            ImpulseDisplacement = 40m,
            ImpulseBarsTracked = 1,
            IsContinuationPattern = false,
            MeetsTradeabilityCriteria = true
        };

        decimal stop = zone.StopPrice(0.25m);
        decimal target = zone.TargetPrice(0.25m, 3m);

        Assert.That(stop, Is.EqualTo(2012.5m));
        Assert.That(stop, Is.GreaterThan(zone.Distal));
        Assert.That(target, Is.EqualTo(2000m - (12.5m * 3m)));
        Assert.That(target, Is.LessThan(zone.Proximal));
    }

    // ---- fixtures -----------------------------------------------------------------------------

    [Test]
    public void OrdersAreNotRestedBeyondThreeAtrByDefault()
    {
        // Default set 2026-09-02. A far order costs twice: it squats the single order slot, and it
        // loses more when it fills (inside 3 ATR avgR -0.1810, beyond -0.4366 over 127 fills).
        // Six instruments: baseline 127 trades at -0.2394, cap 3 gives 142 at -0.1681. The count
        // RISING as the cap tightens is what a filter cannot do, and is how the defect was found.
        // Fails if the default is changed without a decision. 0 restores the old behaviour.
        Assert.That(new AlfonsoStrategyOptions().MaximumPlacementDistanceAtr, Is.EqualTo(3m));
        Assert.That(
            new AlfonsoStrategyOptions { MaximumPlacementDistanceAtr = 0m }.MaximumPlacementDistanceAtr,
            Is.Zero,
            "zero must remain available as the opt-out, since every result before this default was measured under it");
    }

    [Test]
    public void SetAndForgetEntryIsTheDefaultAndConfirmationIsOptIn()
    {
        // The book's premise is a resting limit at the proximal. Confirmation entry departs from it
        // deliberately, to address fill selection (six instruments: 1,212 buy limits and 1,379 sell
        // limits placed, but 2.89% vs 6.67% filled), so it must stay opt-in. This fails if the
        // default is flipped without a decision.
        Assert.That(new AlfonsoStrategyOptions().RequireReversalConfirmation, Is.False);
    }

    private static AgentMarketContext Context(params AnalysisSnapshot[] snapshots) =>
        Context(snapshots, []);

    [Test]
    public async Task TrendAuditObservesNewTimeframeBarsOnceEvenWhilePositionIsOpen()
    {
        List<(DateTimeOffset AvailableAt, TimeSpan Interval)> observed = [];
        AlfonsoAgent agent = new(trendSink: (_, at, _, _, analyzer) => observed.Add((at, analyzer.Interval)));
        AgentMarketContext context = Context(
            [Snapshot(Top, 2000m), Snapshot(Middle, 2000m), Snapshot(Lower, 2000m)],
            [new BrokerPosition { PositionId = "open", Instrument = Instrument, Quantity = 1m,
                Side = OrderSide.Buy, AveragePrice = 2000m }]);
        AgentDecision first = await agent.EvaluateAsync(context);
        AgentDecision repeated = await agent.EvaluateAsync(context);
        Assert.Multiple(() =>
        {
            Assert.That(first.Reason, Does.Contain("bracket owns it"));
            Assert.That(repeated.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(observed.Select(item => item.Interval), Is.EquivalentTo(new[]
                { TimeSpan.FromHours(4), TimeSpan.FromHours(1), TimeSpan.FromMinutes(15) }));
            Assert.That(observed.All(item => item.AvailableAt == Now), Is.True);
        });
    }

    private static AgentMarketContext Context(
        AnalysisSnapshot[] snapshots, IReadOnlyList<BrokerPosition> positions) =>
        new()
        {
            Instrument = Instrument,
            Timestamp = Now,
            Analysis = new MultiTimeframeAnalysis(
                Instrument, Now, snapshots.ToDictionary(snapshot => snapshot.Interval)),
            Account = new AccountSnapshot { AccountId = "account", CanTrade = true },
            Positions = positions,
            OpenOrders = []
        };

    private static AnalysisSnapshot Snapshot(BarInterval interval, decimal price) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument, Now.AddMinutes(-15), interval,
            price, price + 1m, price - 1m, price + 0.5m),
        Indicators = new IndicatorSnapshot { Atr = 2m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
