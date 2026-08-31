using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// The four rule clarifications, each pinned with its switch in both positions so neither reading
/// can silently become a no-op.
/// </summary>
[TestFixture]
public sealed class AlfonsoRuleClarificationTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static AlfonsoBar Bar(int i, decimal o, decimal h, decimal l, decimal c) =>
        new(Start.AddMinutes(15 * i), o, h, l, c);

    private static AlfonsoBar Erc(int i, decimal open, decimal close)
    {
        decimal pad = Math.Abs(close - open) * 0.05m;
        return Bar(i, open, Math.Max(open, close) + pad, Math.Min(open, close) - pad, close);
    }

    private static List<AlfonsoBar> DemandSequence()
    {
        List<AlfonsoBar> bars = [];
        int i = 0;
        for (decimal p = 110m; p > 100m; p -= 2m)
            bars.Add(Erc(i++, p, p - 2m));
        for (int s = 0; s < 3; s++)
            bars.Add(Bar(i++, 99.95m, 101m, 99m, 100.05m));
        bars.Add(Erc(i++, 100.1m, 108m));
        bars.Add(Erc(i++, 108m, 118m));
        bars.Add(Bar(i++, 118m, 119m, 117m, 118.5m));
        return bars;
    }

    // ---- 1. any penetration beyond the distal eliminates the imbalance --------------------------

    [Test]
    public void AWickThroughTheDistalEliminatesByDefault()
    {
        ImbalanceDetector detector = new(Interval);
        List<AlfonsoBar> bars = DemandSequence();
        foreach (AlfonsoBar bar in bars)
            detector.Apply(bar);

        decimal distal = detector.Zones[0].Distal;
        int index = bars.Count;

        // Module 4: penetration by even a tick or pip eliminates the imbalance.
        ImbalanceDetectorUpdate wick = detector.Apply(
            Bar(index++, 105m, 106m, distal - 0.50m, 105m));
        Assert.That(wick.Eliminated, Has.Count.EqualTo(1));
        Assert.That(detector.Zones, Is.Empty);
    }

    [Test]
    public void TheCloseOnlyReadingIsStillAvailableAndBehavesDifferently()
    {
        ImbalanceDetector detector = new(Interval, new ImbalanceOptions { EliminationRequiresClose = true });
        List<AlfonsoBar> bars = DemandSequence();
        foreach (AlfonsoBar bar in bars)
            detector.Apply(bar);

        decimal distal = detector.Zones[0].Distal;
        int index = bars.Count;

        ImbalanceDetectorUpdate wick = detector.Apply(
            Bar(index++, 105m, 106m, distal - 0.50m, 105m));
        Assert.That(wick.Eliminated, Is.Empty);
        Assert.That(detector.Zones, Has.Count.EqualTo(1));

        ImbalanceDetectorUpdate close = detector.Apply(
            Bar(index, 105m, 106m, distal - 0.50m, distal - 0.01m));
        Assert.That(close.Eliminated, Has.Count.EqualTo(1));
        Assert.That(detector.Zones, Is.Empty);
    }

    // ---- 2. breaking a peak or valley is an accomplishment ---------------------------------------

    [Test]
    public void TakingOutAPriorPeakCountsAsAnAccomplishment()
    {
        // A supply zone forms a peak; a later bullish impulse trades above it. That move has
        // accomplished something even though it never approached an all-time high.
        List<AlfonsoBar> bars = [];
        int i = 0;

        // Price must RISE into the supply base for it to be a peak. Falling into it would make it a
        // pause inside the fall - a continuation pattern - which is excluded from swings entirely.
        for (decimal p = 130m; p < 150m; p += 4m)
            bars.Add(Erc(i++, p, p + 4m));

        // Supply base near 152, then a drop away: this records a peak.
        for (int s = 0; s < 3; s++)
            bars.Add(Bar(i++, 151.95m, 153m, 151m, 152.05m));
        bars.Add(Erc(i++, 151.9m, 140m));
        bars.Add(Erc(i++, 140m, 130m));
        bars.Add(Bar(i++, 130m, 131m, 129m, 130.5m));

        // Demand base near 128, then a rally back above the 153 peak.
        for (int s = 0; s < 3; s++)
            bars.Add(Bar(i++, 127.95m, 129m, 127m, 128.05m));
        bars.Add(Erc(i++, 128.1m, 145m));
        bars.Add(Erc(i++, 145m, 160m));
        bars.Add(Bar(i++, 160m, 161m, 159m, 160.5m));

        ImbalanceDetector detector = new(Interval);
        List<Imbalance> created = [];
        foreach (AlfonsoBar bar in bars)
            created.AddRange(detector.Apply(bar).Created);

        Imbalance? rally = created.LastOrDefault(zone => zone.Kind == ImbalanceKind.Demand);
        Assert.That(rally, Is.Not.Null);
        Assert.That(rally!.Accomplished.HasFlag(Accomplishment.SwingBroken), Is.True);

        // And the switch genuinely gates it.
        ImbalanceDetector without = new(Interval,
            new ImbalanceOptions { SwingBreakIsAnAccomplishment = false });
        List<Imbalance> plain = [];
        foreach (AlfonsoBar bar in bars)
            plain.AddRange(without.Apply(bar).Created);

        Assert.That(
            plain.Any(zone => zone.Accomplished.HasFlag(Accomplishment.SwingBroken)), Is.False);
    }

    // ---- 3. only a VALID opposing zone moves the trend -------------------------------------------

    [Test]
    public void EliminatingAZoneThatNeverAccomplishedAnythingDoesNotChangeTheTrend()
    {
        AlfonsoTrendDetector detector = new();

        AlfonsoTrendSnapshot state = detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply, Accomplishment.None),
                              Zone(ImbalanceKind.Supply, Accomplishment.None)]
            });

        Assert.That(state.Trend, Is.EqualTo(AlfonsoTrend.Unknown),
            "two meaningless structures are not two accomplishments");

        state = detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply, Accomplishment.TrendlineBreak),
                              Zone(ImbalanceKind.Supply, Accomplishment.SwingBroken)]
            });

        Assert.That(state.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));
        Assert.That(state.OpposingEliminations, Is.EqualTo(2));
    }

    [Test]
    public void TheValidZoneRequirementCanBeTurnedOffAndChangesTheAnswer()
    {
        AlfonsoTrendDetector permissive = new(
            new AlfonsoTrendOptions { RequireValidZoneForTrendChange = false });

        AlfonsoTrendSnapshot state = permissive.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply, Accomplishment.None),
                              Zone(ImbalanceKind.Supply, Accomplishment.None)]
            });

        Assert.That(state.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));
    }

    // ---- 4. a trendline break is a CLOSE beyond the line ----------------------------------------

    [Test]
    public void ACandleClosingBeyondTheLineBreaksIt_EvenWithItsWickStillOnTheFarSide()
    {
        Trendline line = new()
        {
            Direction = TrendlineDirection.Bullish,
            FromIndex = 0,
            FromPrice = 100m,
            ToIndex = 10,
            ToPrice = 110m,
            FromTime = Start,
            ToTime = Start.AddHours(10)
        };

        // Level at index 10 is 110. Closes at 108 with the high still at 112.
        Assert.That(line.IsBrokenBy(10, high: 112m, low: 107m, close: 108m), Is.True);
        Assert.That(line.IsBrokenBy(10, high: 112m, low: 107m, close: 108m, requireClose: false), Is.False,
            "the whole-candle reading rejects the same bar");

        // A wick through the line that closes back above it is never a break under either reading.
        Assert.That(line.IsBrokenBy(10, high: 112m, low: 107m, close: 111m), Is.False);
        Assert.That(line.IsBrokenBy(10, high: 112m, low: 107m, close: 111m, requireClose: false), Is.False);
    }

    // ---- 5. a trend may change directly, without passing through out of alignment ---------------

    [Test]
    public void ATrendFlipsStraightToTheOppositeDirectionOnEnoughEvidence()
    {
        // The rule states the change directly: the market reversed, an opposing zone is gone and a
        // new trendline can be drawn the other way - or two opposing zones are gone and none can.
        // It does not require a stop in out of alignment on the way.
        AlfonsoTrendDetector detector = new();

        AlfonsoTrendSnapshot down = detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Demand, Accomplishment.TrendlineBreak),
                              Zone(ImbalanceKind.Demand, Accomplishment.SwingBroken)]
            });
        Assert.That(down.Trend, Is.EqualTo(AlfonsoTrend.Downtrend));

        AlfonsoTrendSnapshot up = detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply, Accomplishment.TrendlineBreak),
                              Zone(ImbalanceKind.Supply, Accomplishment.SwingBroken)]
            });

        Assert.That(up.Trend, Is.EqualTo(AlfonsoTrend.Uptrend),
            "two valid supply eliminations flip a downtrend straight to an uptrend");
    }

    [Test]
    public void AnEstablishedTrendIsNotDislodgedByAbsenceOfEvidence()
    {
        // The counterpart: quiet tape must not end a trend. A trend ends because its trendline broke
        // or one of its own zones was taken out, never because nothing happened.
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply, Accomplishment.TrendlineBreak),
                              Zone(ImbalanceKind.Supply, Accomplishment.SwingBroken)]
            });

        AlfonsoTrendSnapshot quiet = detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 101m, 99m, 100.5m),
            ImbalanceDetectorUpdate.Empty);

        Assert.That(quiet.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));
    }

    [Test]
    public void TheLosingSidesEvidenceIsSpentByAFlipSoItCannotImmediatelyFlipBack()
    {
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Demand, Accomplishment.TrendlineBreak),
                              Zone(ImbalanceKind.Demand, Accomplishment.SwingBroken)]
            });

        AlfonsoTrendSnapshot flipped = detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply, Accomplishment.TrendlineBreak),
                              Zone(ImbalanceKind.Supply, Accomplishment.SwingBroken)]
            });
        Assert.That(flipped.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));

        // One further demand elimination is not two: the pre-flip count must not still be banked.
        AlfonsoTrendSnapshot after = detector.Apply(
            new AlfonsoBar(Start.AddHours(2), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Demand, Accomplishment.SwingBroken)]
            });

        Assert.That(after.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment),
            "a demand elimination undermines the fresh uptrend rather than re-establishing a downtrend");
    }

    private static Imbalance Zone(ImbalanceKind kind, Accomplishment accomplished) => new()
    {
        Interval = Interval,
        Kind = kind,
        Proximal = kind == ImbalanceKind.Demand ? 100m : 110m,
        Distal = kind == ImbalanceKind.Demand ? 98m : 112m,
        BaseStart = Start,
        BaseEnd = Start,
        ConfirmedAt = Start,
        BaseCandleCount = 2,
        Strength = ImpulseStrength.Strong,
        Accomplished = accomplished,
        ImpulseToBaseRatio = 3m,
        ImpulseDisplacement = 12m,
        ImpulseBarsTracked = 2,
        IsContinuationPattern = false,
        MeetsTradeabilityCriteria = true
    };
}
