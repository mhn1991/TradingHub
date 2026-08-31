using Agent.Configuration;
using Agent.Abstractions;
using Agent.Factories;
using Agent.Models;
using Agent.Strategies.BreakoutDetector;
using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

/// <summary>
/// Covers the breakout-detector agent's registration surface and options contract. Detection
/// behaviour is deliberately not covered yet - <see cref="BreakoutDetectorAgent"/> observes on
/// every bar until the logic is written, and
/// <see cref="EvaluateAsync_WithAnalysisAvailable_ObservesWhileDetectionIsUnimplemented"/> pins
/// that so the scaffold's zero-trade behaviour is an asserted fact rather than a silent surprise
/// in a backtest.
/// </summary>
[TestFixture]
public sealed class BreakoutDetectorAgentTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Confirmation = BarInterval.Minutes(1);
    private static readonly BarInterval Trigger = BarInterval.Minutes(5);
    private static readonly BarInterval Context15 = BarInterval.Minutes(15);
    private static readonly DateTimeOffset Start = SimulationTestHarness.DefaultStart;

    [Test]
    public void Metadata_ReflectsOptionsAndDeclaresBracketExits()
    {
        var agent = new BreakoutDetectorAgent(new BreakoutDetectorStrategyOptions
        {
            TriggerInterval = Trigger,
            ContextInterval = Context15,
            ConfirmationInterval = Confirmation
        });

        Assert.Multiple(() =>
        {
            Assert.That(agent.Name, Is.EqualTo("Breakout detector agent"));
            // The agent wakes on the finest timeframe so the 1m confirmation is actually consulted
            // on 1m closes; detection still happens on the 5m trigger.
            Assert.That(agent.TriggerInterval, Is.EqualTo(Confirmation));
            Assert.That(agent.RequiredIntervals,
                Is.EquivalentTo(new[] { Confirmation, Trigger, Context15 }));
            Assert.That(agent.ExitManagementMode, Is.EqualTo(AgentExitManagementMode.Bracket));
        });
    }

    [Test]
    public async Task EvaluateAsync_MissingAnalysis_ObservesInsteadOfThrowing()
    {
        var agent = new BreakoutDetectorAgent(new BreakoutDetectorStrategyOptions
        {
            TriggerInterval = Trigger,
            ContextInterval = Context15,
            ConfirmationInterval = Confirmation
        });

        // Trigger present, context absent - the warm-up shape a host produces before every
        // configured interval has a snapshot.
        AgentDecision decision = await agent.EvaluateAsync(
            Context(new Dictionary<BarInterval, AnalysisSnapshot> { [Trigger] = Snapshot(Trigger) }));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Reason, Does.Contain("15m"));
        });
    }

    [Test]
    public async Task EvaluateAsync_WithBareSnapshots_ObservesOnWarmupRatherThanThrowing()
    {
        var agent = new BreakoutDetectorAgent(new BreakoutDetectorStrategyOptions
        {
            TriggerInterval = Trigger,
            ContextInterval = Context15,
            ConfirmationInterval = Confirmation
        });

        AgentDecision decision = await agent.EvaluateAsync(
            Context(new Dictionary<BarInterval, AnalysisSnapshot>
            {
                [Trigger] = Snapshot(Trigger),
                [Context15] = Snapshot(Context15),
                [Confirmation] = Snapshot(Confirmation)
            }));

        Assert.Multiple(() =>
        {
            // Snapshots exist but carry no indicators, so detection cannot run. The agent must
            // say WHICH input is missing rather than returning a bare no-signal.
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Confidence, Is.Zero);
            Assert.That(decision.Reason, Does.Contain("warming up"));
        });
    }

    [Test]
    public void Options_RejectInvertedIntervalsAndOutOfRangeValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new BreakoutDetectorStrategyOptions
                {
                    TriggerInterval = BarInterval.Hours(4),
                    ContextInterval = BarInterval.Minutes(15)
                }.Validate(),
                Throws.ArgumentException.With.Message.Contains("TriggerInterval <= ContextInterval"));
            Assert.That(
                () => new BreakoutDetectorStrategyOptions
                {
                    ContextInterval = BarInterval.Minutes(15),
                    TriggerInterval = BarInterval.Minutes(5),
                    ConfirmationInterval = BarInterval.Minutes(15)
                }.Validate(),
                Throws.ArgumentException.With.Message.Contains("ConfirmationInterval <= TriggerInterval"));
            // Collapsing two roles onto one interval would make the "confirmation" the same candle
            // that triggered - self-confirming and invisible in results.
            Assert.That(
                () => new BreakoutDetectorStrategyOptions
                {
                    ContextInterval = BarInterval.Minutes(15),
                    TriggerInterval = BarInterval.Minutes(5),
                    ConfirmationInterval = BarInterval.Minutes(5)
                }.Validate(),
                Throws.ArgumentException.With.Message.Contains("must be distinct"));
            Assert.That(
                () => new BreakoutDetectorStrategyOptions { RangeLookbackCandles = 1 }.Validate(),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new BreakoutDetectorStrategyOptions { Quantity = 0m }.Validate(),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new BreakoutDetectorStrategyOptions { StopAtrMultiple = 0m }.Validate(),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new BreakoutDetectorStrategyOptions().Validate(), Throws.Nothing);
        });
    }

    [Test]
    public void Definition_RoundTripsThroughSerializedFormAndResolvesFromTheCatalogue()
    {
        var options = new BreakoutDetectorStrategyOptions
        {
            TriggerInterval = Trigger,
            ContextInterval = Context15,
            Quantity = 2_500m,
            RangeLookbackCandles = 30,
            BreakoutBufferAtr = 0.4m,
            MinimumRewardRisk = 2m
        };

        AgentDefinition definition = AgentDefinition.FromBreakoutDetector(options);
        TradingAgentDefinition typed = TradingAgentDefinition.FromAgentDefinition(definition);

        Assert.Multiple(() =>
        {
            Assert.That(definition.AgentTypeId, Is.EqualTo(TradingAgentTypeIds.BreakoutDetector));
            Assert.That(typed.Kind, Is.EqualTo(TradingAgentKind.BreakoutDetector));
            Assert.That(typed.BreakoutDetector, Is.EqualTo(options));
            Assert.That(typed.TriggerInterval, Is.EqualTo(Confirmation));
            Assert.That(typed.RequiredIntervals,
                Is.EquivalentTo(new[] { Confirmation, Trigger, Context15 }));
            Assert.That(
                TradingAgentCatalog.CreateDefault().Create(definition),
                Is.TypeOf<BreakoutDetectorAgent>());
        });
    }

    [Test]
    public void Definition_RejectsOptionsBagsBelongingToAnotherKind()
    {
        Assert.That(
            () => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.BreakoutDetector,
                BreakoutDetector = new BreakoutDetectorStrategyOptions(),
                Progressive = new Agent.Strategies.ProgressiveStrategyOptions()
            }.Validate(),
            Throws.ArgumentException.With.Message.Contains("forbids Progressive"));
    }

    private static AgentMarketContext Context(IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots) => new()
    {
        Instrument = Instrument,
        Timestamp = Start,
        Analysis = new MultiTimeframeAnalysis(Instrument, Start, snapshots),
        Account = new AccountSnapshot { AccountId = "account", CanTrade = true },
        Positions = [],
        OpenOrders = []
    };

    private static AnalysisSnapshot Snapshot(BarInterval interval) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Start,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument, Start, interval,
            open: 1.1000m, high: 1.1010m, low: 1.0990m, close: 1.1005m),
        Indicators = new IndicatorSnapshot { Atr = 0.0010m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
