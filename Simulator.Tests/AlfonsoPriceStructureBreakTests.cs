using Brokers.Models;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoPriceStructureBreakTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestCase(false)]
    [TestCase(true)]
    public void ConfirmedSwingCloseBreakNeutralizesBothDirectionsWithoutFlipping(bool bullish)
    {
        AlfonsoTrendDetector detector = Seed(bullish, enabled: true);
        Apply(detector, 5, 113m, 111m, bullish);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
        Assert.That(detector.Snapshot.Reason, Does.Contain("confirmed price swing"));

        // Old evidence cannot immediately resurrect the invalidated direction.
        Apply(detector, 6, 114m, 112m, bullish, eliminations: 2);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WickOrEqualCloseDoesNotInvalidate(bool bullish)
    {
        AlfonsoTrendDetector detector = Seed(bullish, enabled: true);
        Apply(detector, 5, 113m, 109m, bullish);
        Apply(detector, 6, 112m, 110m, bullish);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(Direction(bullish)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DisabledOptionPreservesBaseline(bool bullish)
    {
        AlfonsoTrendDetector detector = Seed(bullish, enabled: false);
        Apply(detector, 5, 113m, 111m, bullish);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(Direction(bullish)));
    }

    [Test]
    public void UnconfirmedPeakDoesNotLeakFutureInformation()
    {
        AlfonsoTrendDetector detector = new(new AlfonsoTrendOptions { InvalidateOnPriceStructureBreak = true });
        Apply(detector, 0, 104m, 100m, false, eliminations: 2);
        Apply(detector, 1, 105m, 100m, false);
        Apply(detector, 2, 110m, 100m, false);
        Apply(detector, 3, 108m, 100m, false);
        // Before a second right-hand candle can confirm 110, it is exceeded: no confirmed break.
        Apply(detector, 4, 113m, 111m, false);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.Downtrend));
    }

    [Test]
    public void ConfigurationWiresTheOptInWithoutChangingDefaults()
    {
        Assert.That(new AlfonsoTrendOptions().InvalidateOnPriceStructureBreak, Is.False);
        var definition = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAU/USD"), From = Start, To = Start.AddDays(1),
            AlfonsoInvalidateOnPriceStructureBreak = true
        }.ResolveAgentDefinition("alfonso");
        Assert.That(definition.Alfonso!.Trend.InvalidateOnPriceStructureBreak, Is.True);
    }

    private static AlfonsoTrend Direction(bool bullish) => bullish ? AlfonsoTrend.Uptrend : AlfonsoTrend.Downtrend;

    private static AlfonsoTrendDetector Seed(bool bullish, bool enabled)
    {
        AlfonsoTrendDetector detector = new(new AlfonsoTrendOptions { InvalidateOnPriceStructureBreak = enabled });
        decimal[] highs = [104m, 105m, 110m, 108m, 107m];
        for (int i = 0; i < highs.Length; i++)
            Apply(detector, i, highs[i], 100m, bullish, i == 0 ? 2 : 0);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(Direction(bullish)));
        return detector;
    }

    private static void Apply(AlfonsoTrendDetector detector, int index, decimal high, decimal close,
        bool mirror, int eliminations = 0)
    {
        AlfonsoBar bar = mirror
            ? new(Start.AddHours(index * 4), 100m, 110m, 200m - high, 200m - close)
            : new(Start.AddHours(index * 4), 100m, high, 90m, close);
        Imbalance zone = new()
        {
            Interval = TimeSpan.FromHours(4),
            Kind = mirror ? ImbalanceKind.Supply : ImbalanceKind.Demand,
            Proximal = mirror ? 110m : 100m,
            Distal = mirror ? 112m : 98m,
            BaseStart = Start, BaseEnd = Start, DistalAt = Start, ConfirmedAt = Start,
            BaseCandleCount = 1, Strength = ImpulseStrength.Strong,
            Accomplished = Accomplishment.OpposingImbalanceEliminated,
            ImpulseToBaseRatio = 3m, ImpulseDisplacement = 6m, ImpulseBarsTracked = 2,
            MeetsTradeabilityCriteria = true, IsContinuationPattern = false
        };
        detector.Apply(bar, new ImbalanceDetectorUpdate
        {
            Eliminated = Enumerable.Repeat(zone, eliminations).ToArray()
        });
    }
}
