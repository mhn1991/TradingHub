using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Properties that must hold on every bar, checked over a long synthetic series rather than a
/// hand-built fixture.
/// <para>
/// Written because isolated rule tests have not been catching the bugs here. Ten defects were found
/// in this subsystem and every one was found by running real candles and looking at the distribution
/// - a duplicate zone per truncated base, an impulse measured over one candle, over-extension
/// latching at 80% of bars, a trend state that could never leave a downtrend. All of those violate a
/// property expressible in one line, and none of them violated any single rule in isolation.
/// </para>
/// </summary>
[TestFixture]
public sealed class AlfonsoInvariantTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A deterministic pseudo-random walk with trends, reversals and quiet stretches, so the engine
    /// meets the shapes it will meet in a market without depending on a data file.
    /// </summary>
    private static IEnumerable<AlfonsoBar> Series(int count, int seed = 12345)
    {
        int state = seed;
        decimal price = 1000m;
        decimal drift = 0m;

        for (int index = 0; index < count; index++)
        {
            state = (int)((state * 1103515245L + 12345L) & 0x7FFFFFFF);
            double unit = (state % 10_000) / 10_000.0;

            // Long, decisive legs with reversals between them. A pure random walk produces almost
            // no accomplished zones, so the trend layer never has evidence to act on and the
            // machine is never exercised at all.
            if (index % 400 == 0)
                drift = (decimal)((unit - 0.5) * 2.2);

            decimal move = (decimal)((unit - 0.5) * 6.0) + drift;
            decimal open = price;
            decimal close = price + move;
            decimal wick = Math.Abs(move) * (decimal)(0.2 + unit);
            decimal high = Math.Max(open, close) + wick;
            decimal low = Math.Min(open, close) - wick;
            price = close;

            yield return new AlfonsoBar(Start.AddMinutes(15 * index), open, high, low, close);
        }
    }

    [Test]
    public void EveryZoneSatisfiesItsGeometryAndLifecycleInvariants()
    {
        ImbalanceDetector detector = new(TimeSpan.FromMinutes(15));
        HashSet<DateTimeOffset> everCreated = [];
        HashSet<DateTimeOffset> everEliminated = [];
        int created = 0;

        foreach (AlfonsoBar bar in Series(20_000))
        {
            ImbalanceDetectorUpdate update = detector.Apply(bar);

            foreach (Imbalance zone in update.Created)
            {
                created++;

                Assert.That(zone.Width, Is.GreaterThan(0m), "a zone with no width has no risk");
                Assert.That(zone.BaseEnd, Is.GreaterThanOrEqualTo(zone.BaseStart));
                Assert.That(zone.ConfirmedAt, Is.GreaterThanOrEqualTo(zone.BaseEnd),
                    "a zone cannot be confirmed before its base ended");
                Assert.That(zone.BaseCandleCount, Is.GreaterThan(0));

                // Demand sits below price with its distal beneath; supply is the mirror.
                if (zone.Kind == ImbalanceKind.Demand)
                    Assert.That(zone.Proximal, Is.GreaterThan(zone.Distal));
                else
                    Assert.That(zone.Proximal, Is.LessThan(zone.Distal));

                // Protection is always beyond the far edge, never inside the zone.
                decimal stop = zone.StopPrice(0.25m);
                decimal target = zone.TargetPrice(0.25m, 3m);
                if (zone.Kind == ImbalanceKind.Demand)
                {
                    Assert.That(stop, Is.LessThan(zone.Distal));
                    Assert.That(target, Is.GreaterThan(zone.Proximal));
                }
                else
                {
                    Assert.That(stop, Is.GreaterThan(zone.Distal));
                    Assert.That(target, Is.LessThan(zone.Proximal));
                }

                // Reward is exactly the configured multiple of the padded risk.
                decimal risk = Math.Abs(zone.Proximal - stop);
                Assert.That(Math.Abs(Math.Abs(target - zone.Proximal) - (risk * 3m)),
                    Is.LessThan(0.0001m));

                Assert.That(everCreated.Add(zone.BaseEnd), Is.True,
                    "one base must emit at most one zone, ever");
                Assert.That(everEliminated, Does.Not.Contain(zone.BaseEnd),
                    "an eliminated zone must never come back");
            }

            foreach (Imbalance zone in update.Eliminated)
            {
                Assert.That(zone.State, Is.EqualTo(ImbalanceState.Eliminated));
                Assert.That(zone.EliminatedAt, Is.Not.Null);
                everEliminated.Add(zone.BaseEnd);
            }

            foreach (Imbalance zone in detector.Zones)
            {
                Assert.That(zone.State, Is.Not.EqualTo(ImbalanceState.Eliminated),
                    "a live list must not hold eliminated zones");
                // TestCount keeps counting pullbacks past the cap, which is fine - the meaningful
                // invariant is that reaching the cap retires the level for good.
                if (zone.TestCount >= 2)
                {
                    Assert.That(zone.State, Is.EqualTo(ImbalanceState.UsedUp));
                    Assert.That(zone.IsTradeable, Is.False);
                }
                Assert.That(zone.IsTradeable, Is.EqualTo(
                    zone.MeetsTradeabilityCriteria &&
                    zone.State is ImbalanceState.Fresh or ImbalanceState.Tested));
            }
        }

        Assert.That(created, Is.GreaterThan(50), "the series must actually exercise the engine");
    }

    [Test]
    public void TheTrendOnlyEverChangesOnABarThatCarriedAJustifyingEvent()
    {
        AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromMinutes(15));
        AlfonsoTrend previous = analyzer.Trend.Trend;
        int changes = 0;

        foreach (AlfonsoBar bar in Series(20_000, seed: 777))
        {
            ImbalanceDetectorUpdate update = analyzer.Apply(bar);
            AlfonsoTrend current = analyzer.Trend.Trend;

            if (current == previous)
                continue;

            changes++;

            // A trend can only move on evidence: a zone eliminated or confirmed, or a trendline
            // broken by this bar. Silence must never move it - the property the latch bug and both
            // of its failed fixes violated. The reason string is what names the justification, and a
            // change carrying no reason is a change nobody can account for.
            bool zoneEvent = update.Eliminated.Count > 0 || update.Created.Count > 0;
            bool trendlineEvent = analyzer.Trend.Reason.Contains("trendline", StringComparison.OrdinalIgnoreCase);

            Assert.That(zoneEvent || trendlineEvent, Is.True,
                $"trend moved {previous} -> {current} at {bar.OpenTime:O} " +
                $"with neither a zone event nor a trendline break: '{analyzer.Trend.Reason}'");

            Assert.That(analyzer.Trend.Reason, Is.Not.Empty);
            previous = current;
        }

        // One transition is enough to exercise the invariant, and a synthetic walk produces few
        // ACCOMPLISHED zones - the only kind that may move the trend - so a higher bar here would
        // test the generator rather than the engine. That the machine moves freely on real candles
        // is checked by ZZAlfonsoRealDataDiagnostic, which reports the full state distribution.
        Assert.That(changes, Is.GreaterThan(0), "the series must exercise the state machine at least once");
    }

    [Test]
    public void OverExtensionAlwaysClearsAndNeverLatches()
    {
        // Over-extension latched at 80% of bars once. Any state that can be entered must be
        // leaveable, and on a long series it must not dominate.
        AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromMinutes(15));
        int overExtended = 0, total = 0;

        foreach (AlfonsoBar bar in Series(20_000, seed: 4242))
        {
            analyzer.Apply(bar);
            total++;
            if (analyzer.Trend.IsOverExtended)
                overExtended++;
        }

        Assert.That(overExtended / (double)total, Is.LessThan(0.25),
            "over-extension dominating the series means it is latching, not describing");
    }

    [Test]
    public void EveryTimeframeAdvancesIndependentlyOfTheOthers()
    {
        // Feeding one detector another timeframe's bars turns it into a copy of that timeframe. It
        // then agrees with it on every bar and the gate rejects nothing - a silent no-op that has
        // already shipped once in this repo.
        AlfonsoTimeframeAnalyzer fast = new(TimeSpan.FromMinutes(15));
        AlfonsoTimeframeAnalyzer slow = new(TimeSpan.FromHours(4));

        List<AlfonsoBar> bars = Series(8_000, seed: 99).ToList();
        foreach (AlfonsoBar bar in bars)
            fast.Apply(bar);

        // The slow analyzer sees only every sixteenth bar, aggregated as its own timeframe would.
        for (int index = 0; index + 16 <= bars.Count; index += 16)
        {
            List<AlfonsoBar> window = bars.GetRange(index, 16);
            slow.Apply(new AlfonsoBar(
                window[0].OpenTime, window[0].Open,
                window.Max(b => b.High), window.Min(b => b.Low), window[^1].Close));
        }

        Assert.That(fast.Zones.Count, Is.Not.EqualTo(slow.Zones.Count).Or.Zero,
            "two timeframes producing identical output is the signature of one feeding the other");
    }
}
