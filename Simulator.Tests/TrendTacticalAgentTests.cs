using Agent.Abstractions;
using Agent.Configuration;
using Agent.Models;
using Agent.Strategies.TrendTactical;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Features;
using TradingClassifier.Models;

namespace Simulator.Tests;

/// <summary>
/// The trend-gated tactical agent: the 2H layer decides whether and which direction, the classifier
/// decides when. These tests pin the gate, not the edge — whether the composition makes money is
/// what the design document's §5 validation ladder is for.
/// </summary>
[TestFixture]
public sealed class TrendTacticalAgentTests
{
    private static readonly InstrumentKey Instrument = new("METAL:XAU/USD");
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly BarInterval Trigger = BarInterval.Minutes(15);
    private static readonly BarInterval Trend = BarInterval.Hours(2);

    [Test]
    public void DeclaresProtectiveStopAndStrategyExit_NotBracket()
    {
        // Load-bearing. Declaring Bracket makes PreTradeRiskManager hard-floor reward:risk at 1.5
        // and silently reject every decision below it — zero trades, no error (PROJECT_STATE §3.19).
        // This agent submits no take-profit at all, so Bracket would reject everything it produces.
        Assert.That(Agent().ExitManagementMode,
            Is.EqualTo(AgentExitManagementMode.ProtectiveStopAndStrategyExit));
    }

    [Test]
    public async Task Observes_WhileTheTrendHasNoDirection()
    {
        // A flat series never confirms a trend, so the gate must never open regardless of what the
        // classifier says.
        AgentDecision decision = await Agent(alwaysBuy: true).EvaluateAsync(Context(flat: true));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Reason, Does.Contain("trend").IgnoreCase);
        });
    }

    [Test]
    public async Task NeverSignalsAgainstTheTrend_EvenWhenTheClassifierIsCertain()
    {
        // The blueprint's §41 direction gate: a confident counter-trend prediction is DISCARDED,
        // never inverted into a trade the other way.
        TrendTacticalAgent agent = Agent(alwaysSell: true);
        AgentDecision decision = await Feed(agent, rising: true);

        Assert.That(decision.Action, Is.Not.EqualTo(AgentAction.Sell),
            "A sell signal was emitted while the higher timeframe trend was up.");
    }

    [Test]
    public async Task RequiresTheConfidenceThreshold_InTheTrendsOwnDirection()
    {
        // Below the bar: no entry even though direction agrees.
        TrendTacticalAgent shy = Agent(alwaysBuy: true, probability: 0.40, threshold: 0.55);
        AgentDecision refused = await Feed(shy, rising: true);

        Assert.That(refused.Action, Is.EqualTo(AgentAction.Observe));
    }

    [Test]
    public void OptionsRoundTripThroughTheDefinition()
    {
        TrendTacticalStrategyOptions options = new()
        {
            TriggerInterval = BarInterval.Minutes(5),
            TrendInterval = BarInterval.Hours(4),
            BaseEntryThreshold = 0.62,
            MatureThresholdStep = 0.08,
            StopAtrMultiple = 2.0m
        };

        AgentDefinition definition = AgentDefinition.FromTrendTactical(options);
        TrendTacticalStrategyOptions restored = definition.ReadTrendTacticalOptions();

        Assert.Multiple(() =>
        {
            Assert.That(definition.AgentTypeId, Is.EqualTo(TradingAgentTypeIds.TrendTactical));
            Assert.That(restored.TriggerInterval, Is.EqualTo(options.TriggerInterval));
            Assert.That(restored.TrendInterval, Is.EqualTo(options.TrendInterval));
            Assert.That(restored.BaseEntryThreshold, Is.EqualTo(options.BaseEntryThreshold));
            Assert.That(restored.StopAtrMultiple, Is.EqualTo(options.StopAtrMultiple));
        });
    }

    [Test]
    public void DefinitionRejects_ForeignOptionsBags()
    {
        // §2.11's easy-to-miss step: every kind must reject every other kind's options bag.
        TradingAgentDefinition mixed = new()
        {
            Kind = TradingAgentKind.TrendTactical,
            TrendTactical = new TrendTacticalStrategyOptions(),
            TradingClassification = new Agent.Strategies.TradingClassification.TradingClassificationStrategyOptions()
        };

        Assert.That(mixed.Validate, Throws.ArgumentException);
    }

    // ---- Rung A: the validated swing-entry rule ---------------------------------------------------

    [Test]
    public async Task SwingEntryRule_RefusesToTradeBeforeAProfileExists()
    {
        // The research strategy ranks a live trend against a profile of COMPLETED trends. With no
        // completed trends there is no profile, and a percentile computed from nothing is not a
        // conservative estimate — it is meaningless. The agent must decline, not guess.
        TrendTacticalAgent agent = new(
            new TrendTacticalStrategyOptions
            {
                TriggerInterval = Trigger,
                TrendInterval = Trend,
                RequireClassifierAgreement = false,
                UseSwingEntryRule = true,
                Classifier = new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment1 }
            },
            new StubModel(true, false, 0.9));

        AgentDecision decision = await Feed(agent, rising: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Reason, Does.Contain("profile").IgnoreCase, decision.Reason);
        });
    }

    [Test]
    public async Task WithoutTheSwingRule_TheAgentEntersOnEveryEligibleBar()
    {
        // Pins the distinction the review identified: the default is NOT the validated strategy.
        // It shares a trend detector with the research path and nothing else, so its results are
        // not comparable with PF 1.694.
        TrendTacticalAgent agent = new(
            new TrendTacticalStrategyOptions
            {
                TriggerInterval = Trigger,
                TrendInterval = Trend,
                RequireClassifierAgreement = false,
                UseSwingEntryRule = false,
                Classifier = new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment1 }
            },
            new StubModel(true, false, 0.9));

        AgentDecision decision = await Feed(agent, rising: true);

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
    }

    // ---- open-position lifecycle (P0-1 / P0-2) ---------------------------------------------------

    [Test]
    public async Task AShortIsNotClosedWhileItsBearishTrendHolds()
    {
        // The P0 bug: BrokerPosition.Quantity is ABSOLUTE with direction in Side, so a check of
        // "Quantity > 0m" reads every position as long. A short in a bearish trend was therefore
        // treated as a long fighting the trend, and closed.
        TrendTacticalAgent agent = Agent();
        AgentDecision decision = await FeedWithPosition(agent, rising: false, side: OrderSide.Sell);

        Assert.That(decision.Action, Is.Not.EqualTo(AgentAction.Close),
            $"A short was closed while the trend was still bearish: {decision.Reason}");
    }

    [Test]
    public async Task AShortIsClosedWhenTheTrendTurnsBullish()
    {
        // The mirror of the bug: because shorts were read as longs, a short could survive the
        // reversal that should have closed it.
        TrendTacticalAgent agent = Agent();
        AgentDecision decision = await FeedWithPosition(agent, rising: true, side: OrderSide.Sell);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Close), decision.Reason);
            Assert.That(decision.SuggestedQuantity, Is.GreaterThan(0m),
                "A close without a quantity throws in ExecutionCoordinator.ValidateDecision.");
        });
    }

    [Test]
    public async Task ALongIsClosedWhenTheTrendTurnsBearish()
    {
        TrendTacticalAgent agent = Agent();
        AgentDecision decision = await FeedWithPosition(agent, rising: false, side: OrderSide.Buy);

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Close), decision.Reason);
    }

    [Test]
    public async Task AReversalCloseIsAttributableAsAReversal()
    {
        // P0-2: the simulator maps a close to ReverseStrategyClose only when the reason contains
        // "Opposite trend", and everything else to a platform reason. If the agent's reversal text
        // does not carry that marker, its own exits are recorded as platform exits and every exit
        // statistic derived from them is wrong.
        TrendTacticalAgent agent = Agent();
        AgentDecision decision = await FeedWithPosition(agent, rising: true, side: OrderSide.Sell);

        Assert.That(decision.Reason, Does.Contain("Opposite trend").IgnoreCase,
            "Reversal closes must be attributable, or exit analysis silently misreports them.");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Drives the trend, then evaluates one more bar with an open position supplied.</summary>
    private static async Task<AgentDecision> FeedWithPosition(
        TrendTacticalAgent agent, bool rising, OrderSide side)
    {
        await agent.EvaluateAsync(Context(flat: true));
        for (int index = 1; index <= 40; index++)
        {
            decimal drift = rising ? index * 3m : -index * 3m;
            await agent.EvaluateAsync(Context(flat: false, step: index, drift: drift));
        }

        BrokerPosition position = new()
        {
            PositionId = "p1",
            Instrument = Instrument,
            Side = side,
            // Absolute, exactly as the simulated broker reports it — the whole point of the test.
            Quantity = 1_000m,
            AveragePrice = 2_000m
        };

        decimal finalDrift = rising ? 41 * 3m : -41 * 3m;
        return await agent.EvaluateAsync(
            Context(flat: false, step: 41, drift: finalDrift, positions: [position]));
    }


    private static TrendTacticalAgent Agent(
        bool alwaysBuy = false,
        bool alwaysSell = false,
        double probability = 0.90,
        double threshold = 0.55) =>
        new(new TrendTacticalStrategyOptions
        {
            TriggerInterval = Trigger,
            TrendInterval = Trend,
            BaseEntryThreshold = threshold,
            Classifier = new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment1 }
        },
        new StubModel(alwaysBuy, alwaysSell, probability));

    /// <summary>Drives enough 2H candles through the agent to confirm a trend, then returns the last decision.</summary>
    private static async Task<AgentDecision> Feed(TrendTacticalAgent agent, bool rising)
    {
        AgentDecision decision = await agent.EvaluateAsync(Context(flat: true));
        for (int index = 1; index <= 40; index++)
        {
            decimal drift = rising ? index * 3m : -index * 3m;
            decision = await agent.EvaluateAsync(Context(flat: false, step: index, drift: drift));
        }
        return decision;
    }

    private static AgentMarketContext Context(
        bool flat, int step = 0, decimal drift = 0m, IReadOnlyList<BrokerPosition>? positions = null)
    {
        decimal close = 2_000m + (flat ? 0m : drift);
        return new AgentMarketContext
        {
            Instrument = Instrument,
            Timestamp = Now.AddHours(2 * step),
            Analysis = new MultiTimeframeAnalysis(Instrument, Now.AddHours(2 * step),
                new Dictionary<BarInterval, AnalysisSnapshot>
                {
                    [Trigger] = Snapshot(Trigger, close, Now.AddHours(2 * step)),
                    [Trend] = Snapshot(Trend, close, Now.AddHours(2 * step))
                }),
            Account = new AccountSnapshot
            {
                AccountId = "a", Currency = "USD", Balance = 100_000m, Available = 100_000m, CanTrade = true
            },
            Positions = positions ?? [],
            OpenOrders = []
        };
    }

    private static AnalysisSnapshot Snapshot(BarInterval interval, decimal close, DateTimeOffset at) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = at,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, at, interval, close - 1m, close + 2m, close - 2m, close),
        Indicators = new IndicatorSnapshot { Atr = 5m, Rsi = 55m, Cci = 20m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 70m, Contributions = [] }
    };

    private sealed class StubModel(bool buy, bool sell, double probability) : ITradingModel
    {
        public IReadOnlyList<string> FeatureNames { get; } = [];

        public Prediction Predict(FeatureVector features) => new()
        {
            BuyProbability = buy ? probability : 0.05,
            SellProbability = sell ? probability : 0.05,
            NoTradeProbability = buy || sell ? 1 - probability : 0.90
        };
    }
}
