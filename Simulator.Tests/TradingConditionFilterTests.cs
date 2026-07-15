using Brokers.Models;
using ChartAnnotator.Regime;
using RiskManager.Conditions;

namespace Simulator.Tests;

[TestFixture]
public sealed class TradingConditionFilterTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");

    [Test]
    public void SpreadAtr_SoftReduces_AndHardRejects()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        TradingConditionDecision soft = filter.Evaluate(Context(timestamp, spread: 0.0015m, atr: 0.01m));
        TradingConditionDecision hard = filter.Evaluate(Context(timestamp, spread: 0.003m, atr: 0.01m));

        Assert.Multiple(() =>
        {
            Assert.That(soft.Action, Is.EqualTo(TradingConditionAction.AllowWithReducedRisk));
            Assert.That(soft.RiskMultiplier, Is.EqualTo(0.5m));
            Assert.That(soft.ReasonCode, Is.EqualTo("SpreadAtrSoftLimit"));
            Assert.That(hard.Action, Is.EqualTo(TradingConditionAction.RejectEntry));
            Assert.That(hard.ReasonCode, Is.EqualTo("SpreadAtrHardLimit"));
        });
    }

    [Test]
    public void BrokerRollover_UsesIanaDstRules()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });

        Assert.Multiple(() =>
        {
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 7, 15, 21, 0, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.BrokerRollover));
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 1, 14, 22, 0, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.BrokerRollover));
        });
    }

    [Test]
    public void EventBlackout_UsesOnlyEventsKnownAtDecisionTime()
    {
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var provider = new FixedEventsProvider([
            new EconomicEvent
            {
                EventId = "known",
                ScheduledAt = timestamp.AddMinutes(10),
                KnownAt = timestamp.AddDays(-1),
                Currency = "GBP",
                Importance = EconomicEventImportance.High,
                Name = "Known event"
            },
            new EconomicEvent
            {
                EventId = "future-publication",
                ScheduledAt = timestamp.AddMinutes(5),
                KnownAt = timestamp.AddMinutes(1),
                Currency = "USD",
                Importance = EconomicEventImportance.High,
                Name = "Not known yet"
            }
        ]);
        var filter = new TradingConditionFilter(new TradingConditionOptions
        {
            Enabled = true,
            EconomicEventFilterEnabled = true
        }, provider);

        TradingConditionDecision result = filter.Evaluate(Context(timestamp, 0.0001m, 0.01m));

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradingConditionAction.DelayEntry));
            Assert.That(result.EventIds, Is.EqualTo(new[] { "known" }));
        });
    }

    [Test]
    public void MissingProvider_IsUnavailable_NotClear()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions
        {
            Enabled = true,
            EconomicEventFilterEnabled = true
        });
        TradingConditionDecision result = filter.Evaluate(Context(
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), 0.0001m, 0.01m));

        Assert.That(result.EconomicEventState, Is.EqualTo(EconomicEventFilterState.Unavailable));
    }

    private static TradingConditionContext Context(DateTimeOffset timestamp, decimal? spread, decimal? atr) => new()
    {
        Timestamp = timestamp,
        Instrument = Instrument,
        CurrentSpread = spread,
        Atr = atr,
        MarketDataAvailableAt = timestamp,
        Regime = MarketRegime.Range
    };

    private sealed class FixedEventsProvider(IReadOnlyList<EconomicEvent> events) : IEconomicEventProvider
    {
        public IReadOnlyList<EconomicEvent> GetKnownEvents(
            DateTimeOffset from,
            DateTimeOffset to,
            IReadOnlySet<string> currencies) => events
            .Where(item => item.ScheduledAt >= from && item.ScheduledAt <= to)
            .ToArray();
    }
}
