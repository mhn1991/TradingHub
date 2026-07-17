using System.Collections;
using System.Reflection;
using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Tests;

/// <summary>
/// AGENT-07 regression: locks in the documented, intentionally asymmetric state-retention
/// behaviour when confirmation-timeframe evidence stops being satisfied — retained while
/// WaitingForConfirmation, cleared while WaitingForEntry — so a future edit cannot silently
/// re-diverge the two branches without failing a test.
/// </summary>
[TestFixture]
public sealed class ProgressiveStrategyStateRetentionTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Entry = BarInterval.Minutes(5);
    private static readonly BarInterval Confirmation = BarInterval.Minutes(15);
    private static readonly BarInterval Trend = BarInterval.Hours(1);
    private static readonly DateTimeOffset Now = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task WaitingForConfirmation_RetainsScopeState_WhenConfirmationNotYetSatisfied()
    {
        (AgentDecision decision, bool stateRetainedAfterward) = await EvaluateWithInjectedState(
            waitingForEntry: false);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.ReasonCode, Is.EqualTo("ConfirmationConsensusNotReady"));
            Assert.That(stateRetainedAfterward, Is.True,
                "A setup still waiting for its first confirmation must not be discarded just " +
                "because this candle did not (yet) reach consensus.");
        });
    }

    [Test]
    public async Task WaitingForEntry_ClearsScopeState_WhenConfirmationNoLongerSatisfied()
    {
        (AgentDecision decision, bool stateRetainedAfterward) = await EvaluateWithInjectedState(
            waitingForEntry: true);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.ReasonCode, Is.EqualTo("ConfirmationConsensusLost"));
            Assert.That(stateRetainedAfterward, Is.False,
                "A setup that already reached the entry window on a satisfied confirmation must " +
                "be invalidated, not retained, once that confirmation is lost.");
        });
    }

    private static async Task<(AgentDecision Decision, bool StateRetainedAfterward)> EvaluateWithInjectedState(
        bool waitingForEntry)
    {
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = Trend,
            ConfirmationInterval = Confirmation,
            EntryInterval = Entry,
            MinimumTrendConfidence = 0m,
            MinimumConfirmationConfidence = 0m,
            MinimumEntryConfidence = 0m
        };
        var agent = new ImprovedProgressiveAgent(options);

        AnalysisSnapshot trend = BullishSnapshot(Trend);
        // Rsi = null forces DetectSide to return null for the confirmation timeframe, i.e. the
        // confirmation evidence is genuinely "not satisfied" (0/1 aligned) without also tripping
        // the separate StrongOpposition veto path — exactly the condition both branches share.
        AnalysisSnapshot confirmation = BullishSnapshot(Confirmation) with
        {
            Indicators = new IndicatorSnapshot { Atr = 2m, Rsi = null, BollingerMiddle = 105m }
        };
        AnalysisSnapshot entrySnapshot = BullishSnapshot(Entry);

        AgentMarketContext context = new()
        {
            Instrument = Instrument,
            Timestamp = Now,
            Analysis = new MultiTimeframeAnalysis(
                Instrument,
                Now,
                new Dictionary<BarInterval, AnalysisSnapshot>
                {
                    [Trend] = trend,
                    [Confirmation] = confirmation,
                    [Entry] = entrySnapshot
                }),
            Account = new AccountSnapshot { AccountId = "test", CanTrade = true },
            Positions = [],
            OpenOrders = []
        };

        InjectScopeState(agent, waitingForEntry, trend.AvailableAt);

        AgentDecision decision = await agent.EvaluateAsync(context);

        bool stateRetainedAfterward = ReadStates(agent).Contains(Instrument);
        return (decision, stateRetainedAfterward);
    }

    private static AnalysisSnapshot BullishSnapshot(BarInterval interval) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, Now.AddMinutes(-5), interval, 100m, 111m, 99m, 110m),
        Indicators = new IndicatorSnapshot { Atr = 2m, Rsi = 60m, BollingerMiddle = 105m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 80m, Contributions = [] }
    };

    private static void InjectScopeState(
        ImprovedProgressiveAgent agent,
        bool waitingForEntry,
        DateTimeOffset trendAvailableAt)
    {
        Type baseType = typeof(ImprovedProgressiveAgent).BaseType!;
        Type scopeStateType = baseType.GetNestedType("ScopeState", BindingFlags.NonPublic)!;
        Type setupSideType = baseType.GetNestedType("SetupSide", BindingFlags.NonPublic)!;
        Type setupStageType = baseType.GetNestedType("SetupStage", BindingFlags.NonPublic)!;
        object buySide = Enum.Parse(setupSideType, "Buy");
        object stage = Enum.Parse(setupStageType, waitingForEntry ? "WaitingForEntry" : "WaitingForConfirmation");
        object state = Activator.CreateInstance(
            scopeStateType,
            "test-setup",
            buySide,
            stage,
            Now.AddHours(-1),
            Now.AddHours(1),
            trendAvailableAt,
            waitingForEntry ? (DateTimeOffset?)Now.AddMinutes(-15) : null)!;

        FieldInfo statesField = baseType.GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var states = (IDictionary)statesField.GetValue(agent)!;
        states[Instrument] = state;
    }

    private static IDictionary ReadStates(ImprovedProgressiveAgent agent)
    {
        Type baseType = typeof(ImprovedProgressiveAgent).BaseType!;
        FieldInfo statesField = baseType.GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IDictionary)statesField.GetValue(agent)!;
    }
}
