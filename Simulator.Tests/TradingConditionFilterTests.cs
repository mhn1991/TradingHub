using Brokers.Models;
using ChartAnnotator.Regime;
using RiskManager.Conditions;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class TradingConditionFilterTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");

    [Test]
    public void Options_BareDefault_IsEnabled()
    {
        // AGENT-03 regression: the bare record default previously left every trading-condition
        // safety mechanism (session gating, rollover blackout, pre-weekend protection,
        // spread/ATR gates, stale-data rejection) silently off for any caller that did not
        // explicitly opt in.
        var options = new TradingConditionOptions();

        Assert.That(options.Enabled, Is.True);
    }

    [Test]
    public void SpreadAtr_SoftReduces_AndHardRejects()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        TradingConditionDecision soft = filter.Evaluate(Context(timestamp, spread: 0.003m, atr: 0.01m));
        TradingConditionDecision hard = filter.Evaluate(Context(timestamp, spread: 0.005m, atr: 0.01m));

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
    public void SpreadAtr_BelowSoftThreshold_RemainsAllowed()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        // spread/atr = 0.24, just under the 0.25 default soft limit.
        TradingConditionDecision result = filter.Evaluate(Context(timestamp, spread: 0.0024m, atr: 0.01m));

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradingConditionAction.Allow));
            Assert.That(result.RiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void SpreadAtr_AtSoftThresholdExactly_IsInclusive()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        // spread/atr = 0.25 exactly, matching the default soft limit - the comparison is >=,
        // so the threshold value itself must already trigger the soft-limit action.
        TradingConditionDecision result = filter.Evaluate(Context(timestamp, spread: 0.0025m, atr: 0.01m));

        Assert.That(result.ReasonCode, Is.EqualTo("SpreadAtrSoftLimit"));
    }

    [Test]
    public void SpreadAtr_SoftLimit_ConfiguredToReject_RejectsEntry()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions
        {
            Enabled = true,
            SoftSpreadLimitAction = SoftSpreadLimitAction.RejectEntry
        });
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        TradingConditionDecision soft = filter.Evaluate(Context(timestamp, spread: 0.003m, atr: 0.01m));

        Assert.Multiple(() =>
        {
            Assert.That(soft.Action, Is.EqualTo(TradingConditionAction.RejectEntry));
            Assert.That(soft.RiskMultiplier, Is.Zero);
            Assert.That(soft.ReasonCode, Is.EqualTo("SpreadAtrSoftRejected"));
        });
    }

    [Test]
    public void SpreadAtr_HardLimit_RejectsRegardlessOfSoftLimitAction()
    {
        var filter = new TradingConditionFilter(new TradingConditionOptions
        {
            Enabled = true,
            SoftSpreadLimitAction = SoftSpreadLimitAction.RejectEntry
        });
        DateTimeOffset timestamp = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        TradingConditionDecision hard = filter.Evaluate(Context(timestamp, spread: 0.005m, atr: 0.01m));

        Assert.Multiple(() =>
        {
            Assert.That(hard.Action, Is.EqualTo(TradingConditionAction.RejectEntry));
            // Telemetry must still distinguish a hard rejection from a soft-limit rejection
            // even when both configurations reject the entry.
            Assert.That(hard.ReasonCode, Is.EqualTo("SpreadAtrHardLimit"));
            Assert.That(hard.ReasonCode, Is.Not.EqualTo("SpreadAtrSoftRejected"));
        });
    }

    [Test]
    public void Validate_RequiresHardLimitAboveSoftLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradingConditionOptions
        {
            Enabled = true,
            SoftMaximumSpreadAtr = 0.5m,
            HardMaximumSpreadAtr = 0.5m
        }.Validate());
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
    public void LondonSession_UsesIanaDstRules_AcrossUkSpringAndAutumnTransitions()
    {
        // UK BST 2026: starts 2026-03-29, ends 2026-10-25. The old fixed 07:00-16:00 UTC
        // window was only correct during GMT (winter); it silently drifted an hour wrong
        // for the entire BST half of the year.
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });

        Assert.Multiple(() =>
        {
            // Day after UK springs forward: 06:30 UTC = 07:30 BST local -> session open,
            // but the old fixed-UTC code (hour < 7) would have missed it.
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 3, 30, 6, 30, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.London));
            // Two days before UK falls back (still BST): 06:30 UTC = 07:30 BST local ->
            // session open, but the old fixed-UTC code would have missed it too.
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 10, 23, 6, 30, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.London));
            // After UK falls back to GMT: 06:30 UTC = 06:30 GMT local -> session not open yet.
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 10, 26, 6, 30, 0, TimeSpan.Zero)),
                Is.Not.EqualTo(TradingSession.London).And.Not.EqualTo(TradingSession.LondonNewYorkOverlap));
        });
    }

    [Test]
    public void NewYorkSession_UsesIanaDstRules_AcrossUsSpringAndAutumnTransitions()
    {
        // US DST 2026: starts 2026-03-08, ends 2026-11-01. The old fixed 12:00-21:00 UTC
        // window was only correct during EDT (summer); it silently drifted an hour wrong
        // for the entire EST half of the year.
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });

        Assert.Multiple(() =>
        {
            // Deep winter (EST, well before US DST starts): 12:30 UTC = 07:30 EST local ->
            // New York not open yet, so only London is active (London local = 12:30 GMT).
            // The old fixed-UTC code (12:00-21:00) would have wrongly reported an overlap.
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.London));
            // Day after US falls back to EST: 12:30 UTC = 07:30 EST local -> New York not
            // open yet, so only London is active. The old fixed-UTC code (12:00-21:00,
            // calibrated for EDT) would have wrongly reported an overlap here too.
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 11, 2, 12, 30, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.London));
        });
    }

    [Test]
    public void MismatchWeek_BetweenUsAndUkDstTransitions_ClassifiesEachSessionByItsOwnLocalClock()
    {
        // Between 2026-03-08 (US springs forward) and 2026-03-29 (UK springs forward),
        // the US is on EDT while the UK is still on GMT - a three-week mismatch window.
        var filter = new TradingConditionFilter(new TradingConditionOptions { Enabled = true });

        Assert.Multiple(() =>
        {
            // 07:30 UTC: London local 07:30 GMT (open), New York local 03:30 EDT (closed).
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 3, 18, 7, 30, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.London));
            // 12:30 UTC: London local 12:30 GMT (open), New York local 08:30 EDT (open).
            Assert.That(filter.ClassifySession(new DateTimeOffset(2026, 3, 18, 12, 30, 0, TimeSpan.Zero)),
                Is.EqualTo(TradingSession.LondonNewYorkOverlap));
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
    public void EnabledWithoutProvider_FailsFastInsteadOfSilentlyUnavailable()
    {
        // A missing provider must never silently report "Unavailable" and continue -
        // that lets the Dashboard claim event protection is active when it never
        // actually blocks an entry (audit §10). Construction must fail loudly instead.
        Assert.Throws<ArgumentException>(() => new TradingConditionFilter(new TradingConditionOptions
        {
            Enabled = true,
            EconomicEventFilterEnabled = true
        }));
    }

    [Test]
    public void RequestValidate_RejectsEconomicEventFilterEnabled_NoProductionProviderExists()
    {
        var request = new BacktestRequest
        {
            Instrument = Instrument,
            From = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            Runtime = new BacktestRuntimeOptions
            {
                TradingConditions = new TradingConditionOptions
                {
                    Enabled = true,
                    EconomicEventFilterEnabled = true
                }
            }
        };

        Assert.Throws<ArgumentException>(() => request.Validate());
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
    [Test]
    public void EnabledWithNoAllowedSessions_FailsFastInsteadOfStarvingEveryCandidate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradingConditionOptions
        {
            Enabled = true,
            AllowedSessions = []
        }.Validate());
    }

    [Test]
    public void EmptyInstrumentSessionOverride_FailsFast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradingConditionOptions
        {
            Enabled = true,
            InstrumentAllowedSessions = new Dictionary<string, IReadOnlyList<TradingSession>>
            {
                [Instrument.Value] = []
            }
        }.Validate());
    }

}
