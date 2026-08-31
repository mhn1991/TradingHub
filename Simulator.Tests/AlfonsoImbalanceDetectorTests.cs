using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Rule-level tests for the Set and Forget zone engine. Each test names the course rule it pins so
/// a future change that breaks one can be traced back to the text rather than to a guess.
/// </summary>
[TestFixture]
public sealed class AlfonsoImbalanceDetectorTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static AlfonsoBar Bar(int index, decimal open, decimal high, decimal low, decimal close) =>
        new(Start.AddMinutes(15 * index), open, high, low, close);

    /// <summary>A pause: body well under half the range.</summary>
    private static AlfonsoBar Basing(int index, decimal centre) =>
        Bar(index, centre - 0.05m, centre + 1m, centre - 1m, centre + 0.05m);

    /// <summary>An extended range candle: body about 90% of the range.</summary>
    private static AlfonsoBar Erc(int index, decimal open, decimal close)
    {
        decimal body = Math.Abs(close - open);
        decimal pad = body * 0.05m;
        return Bar(index, open, Math.Max(open, close) + pad, Math.Min(open, close) - pad, close);
    }

    /// <summary>
    /// The canonical demand: a fall in, a tight base, two strong bullish candles out, then a candle
    /// that stays clear. Long enough beforehand that the impulse also breaks the running high.
    /// </summary>
    private static List<AlfonsoBar> DemandSequence()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;
        for (decimal price = 110m; price > 100m; price -= 2m)
            bars.Add(Erc(index++, price, price - 2m));

        for (int step = 0; step < 3; step++)
            bars.Add(Basing(index++, 100m));

        bars.Add(Erc(index++, 100.1m, 108m));
        bars.Add(Erc(index++, 108m, 118m));
        bars.Add(Bar(index++, 118m, 119m, 117m, 118.5m));
        return bars;
    }

    private static (ImbalanceDetector Detector, List<Imbalance> Created) Run(
        IEnumerable<AlfonsoBar> bars, ImbalanceOptions? options = null)
    {
        ImbalanceDetector detector = new(Interval, options);
        List<Imbalance> created = [];
        foreach (AlfonsoBar bar in bars)
            created.AddRange(detector.Apply(bar).Created);
        return (detector, created);
    }

    [Test]
    public void DetectsDemandZoneWithProximalAtBodyTopAndDistalAtLowestLow()
    {
        (_, List<Imbalance> created) = Run(DemandSequence());

        Assert.That(created, Has.Count.EqualTo(1));
        Imbalance zone = created[0];
        Assert.That(zone.Kind, Is.EqualTo(ImbalanceKind.Demand));
        Assert.That(zone.BaseCandleCount, Is.EqualTo(3));

        // Module 4: the distal "must always include the lowest low in the basing structure".
        Assert.That(zone.Distal, Is.EqualTo(99m));
        Assert.That(zone.Proximal, Is.EqualTo(100.05m));
        Assert.That(zone.MeetsTradeabilityCriteria, Is.True);
    }

    [Test]
    public void ImpulseShorterThanTwiceTheBaseIsValidButNotTradeable()
    {
        // Module 7: "A 2:1 RR is a minimum requirement for tradeability. It does not negate the
        // level as a valid imbalance."
        // The leg in is deliberately short: the impulse still has to break the prior high to earn
        // an accomplishment, otherwise the zone is rejected before tradeability is ever assessed.
        List<AlfonsoBar> bars = [];
        int index = 0;
        bars.Add(Erc(index++, 101m, 100.15m));
        for (int step = 0; step < 3; step++)
            bars.Add(Basing(index++, 100m));

        // Base spans 99 to 100.05, so a move to 101.5 is well under 2x the width.
        bars.Add(Erc(index++, 100.1m, 101.2m));
        bars.Add(Erc(index++, 101.2m, 101.5m));
        bars.Add(Bar(index++, 101.5m, 101.9m, 101.4m, 101.6m));

        (_, List<Imbalance> created) = Run(bars);

        Assert.That(created, Has.Count.EqualTo(1));
        Imbalance zone = created[0];
        Assert.That(zone.ImpulseToBaseRatio < 2m, Is.True);
        Assert.That(zone.MeetsTradeabilityCriteria, Is.False);
        Assert.That(zone.IsTradeable, Is.False);
    }

    [Test]
    public void ImpulseThatReturnsToTheBaseImmediatelyIsNotConfirmed()
    {
        // Module 4: "An imbalance will not be confirmed if price returns to the origin of the move
        // in the very next candlestick."
        // The impulse leaves at index 8; index 9 is "the very next candlestick" and is the one that
        // has to return. Modifying the final bar instead would test nothing, because the zone is
        // confirmed as soon as one full candle stands clear - which happens at index 9.
        List<AlfonsoBar> bars = DemandSequence();
        bars[9] = Bar(9, 108m, 109m, 99.5m, 100m);
        bars[10] = Bar(10, 100m, 101m, 99.5m, 100.2m);

        (_, List<Imbalance> created) = Run(bars);

        Assert.That(created, Is.Empty);
    }

    [Test]
    public void ClosingATickBeyondTheDistalEliminatesTheZone()
    {
        // A close beyond the distal necessarily penetrates it; the wick-only boundary is pinned
        // separately in AlfonsoRuleClarificationTests.
        List<AlfonsoBar> bars = DemandSequence();
        ImbalanceDetector detector = new(Interval);
        foreach (AlfonsoBar bar in bars)
            detector.Apply(bar);

        Assert.That(detector.Zones, Has.Count.EqualTo(1));
        decimal distal = detector.Zones[0].Distal;

        ImbalanceDetectorUpdate update = detector.Apply(
            Bar(bars.Count, 118m, 118.5m, distal - 0.50m, distal - 0.01m));

        Assert.That(update.Eliminated, Has.Count.EqualTo(1));
        Assert.That(update.Eliminated[0].State, Is.EqualTo(ImbalanceState.Eliminated));
        Assert.That(detector.Zones, Is.Empty);
    }

    [Test]
    public void TestIsCompletedOnlyAfterAFullCandleBackAwayFromProximal()
    {
        // Module 7: tested means price "retraces to its proximal line and consolidates away with at
        // least a full OHCL candle away from the original imbalance proximal line".
        List<AlfonsoBar> bars = DemandSequence();
        ImbalanceDetector detector = new(Interval);
        foreach (AlfonsoBar bar in bars)
            detector.Apply(bar);

        Imbalance zone = detector.Zones[0];
        int index = bars.Count;

        // Touching the proximal line starts a test but does not complete one.
        ImbalanceDetectorUpdate touch = detector.Apply(
            Bar(index++, 105m, 106m, zone.Proximal - 0.2m, 104m));
        Assert.That(touch.Tested, Is.Empty);
        Assert.That(detector.Zones[0].State, Is.EqualTo(ImbalanceState.Fresh));
        Assert.That(detector.TradeableZones(ImbalanceKind.Demand, 105m), Is.Empty,
            "after a missed first touch the detector must not offer a late order during that pullback");

        ImbalanceDetectorUpdate away = detector.Apply(
            Bar(index, 104m, 112m, zone.Proximal + 1m, 111m));

        Assert.That(away.Tested, Has.Count.EqualTo(1));
        Assert.That(detector.Zones[0].TestCount, Is.EqualTo(1));
        Assert.That(detector.Zones[0].State, Is.EqualTo(ImbalanceState.Tested));
        Assert.That(detector.Zones[0].IsTradeable, Is.True);
    }

    [Test]
    public void SecondCompletedTestUsesTheLevelUpAndBlocksFurtherTrading()
    {
        // Module 7: "Taking a third pullback to a level is not allowed."
        List<AlfonsoBar> bars = DemandSequence();
        ImbalanceDetector detector = new(Interval);
        foreach (AlfonsoBar bar in bars)
            detector.Apply(bar);

        Imbalance zone = detector.Zones[0];
        int index = bars.Count;

        for (int pullback = 0; pullback < 2; pullback++)
        {
            detector.Apply(Bar(index++, 105m, 106m, zone.Proximal - 0.2m, 104m));
            detector.Apply(Bar(index++, 104m, 112m, zone.Proximal + 1m, 111m));
        }

        Assert.That(detector.Zones[0].TestCount, Is.EqualTo(2));
        Assert.That(detector.Zones[0].State, Is.EqualTo(ImbalanceState.UsedUp));
        Assert.That(detector.Zones[0].IsTradeable, Is.False);
        Assert.That(detector.TradeableZones(ImbalanceKind.Demand, 105m), Is.Empty);
    }

    [Test]
    public void ExtremeBrokenIsDetectedAgainstTheMarketBeforeTheImpulse()
    {
        // Regression: a single running extreme folds the impulse's own bars in before the zone is
        // confirmed, so the impulse would be compared against itself and this flag would never be
        // set - a silent no-op that prices the accomplishment at zero.
        (_, List<Imbalance> created) = Run(DemandSequence());

        Assert.That(created, Has.Count.EqualTo(1));
        Imbalance zone = created[0];
        Assert.That(zone.Accomplished.HasFlag(Accomplishment.ExtremeBroken), Is.True);
    }

    [Test]
    public void OpposingEliminationEarlierInTheImpulseCountsAtConfirmation()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;

        // First create a supply level around 100.
        for (decimal price = 90m; price < 100m; price += 2m)
            bars.Add(Erc(index++, price, price + 2m));
        for (int step = 0; step < 3; step++)
            bars.Add(Basing(index++, 100m));
        bars.Add(Erc(index++, 99.9m, 92m));
        bars.Add(Erc(index++, 92m, 82m));
        bars.Add(Bar(index++, 82m, 83m, 81m, 82.5m));

        // Base a demand move, eliminate that supply on the first impulse candle, then confirm the
        // demand level one candle later. The accomplishment belongs to the entire impulse window.
        for (int step = 0; step < 3; step++)
            bars.Add(Basing(index++, 82m));
        bars.Add(Bar(index++, 82.1m, 105.5m, 81.5m, 105m));
        bars.Add(Bar(index++, 105m, 111m, 104m, 110m));

        ImbalanceDetector detector = new(Interval);
        List<Imbalance> created = [];
        List<Imbalance> eliminated = [];
        foreach (AlfonsoBar bar in bars)
        {
            ImbalanceDetectorUpdate update = detector.Apply(bar);
            created.AddRange(update.Created);
            eliminated.AddRange(update.Eliminated);
        }

        Imbalance removedSupply = eliminated.Single(zone => zone.Kind == ImbalanceKind.Supply);
        Imbalance demand = created.Last(zone => zone.Kind == ImbalanceKind.Demand);

        Assert.That(removedSupply.EliminatedAt, Is.LessThan(demand.ConfirmedAt));
        Assert.That(
            demand.Accomplished.HasFlag(Accomplishment.OpposingImbalanceEliminated), Is.True);
    }

    [Test]
    public void RelativeHistoryIndexesAreTranslatedIntoTheAbsoluteTimeline()
    {
        Assert.That(ImbalanceDetector.ToAbsoluteBarIndex(relativeIndex: 12, barOffset: 300),
            Is.EqualTo(312));
    }

    [Test]
    public void ZoneIsNotEmittedTwiceAfterHistoryIsTrimmed()
    {
        // Regression: bar indices are absolute, so trimming history without rebasing the claimed
        // set would let the same base be detected again and duplicate its zone.
        List<AlfonsoBar> bars = DemandSequence();
        ImbalanceDetector detector = new(Interval);
        List<Imbalance> created = [];
        foreach (AlfonsoBar bar in bars)
            created.AddRange(detector.Apply(bar).Created);

        decimal price = 118.5m;
        for (int step = 0; step < 400; step++)
        {
            price += 0.01m;
            created.AddRange(detector.Apply(
                Bar(bars.Count + step, price, price + 0.05m, price - 0.05m, price + 0.01m)).Created);
        }

        Assert.That(created.Select(zone => zone.BaseEnd).Distinct().Count(), Is.EqualTo(created.Count));
    }

    [Test]
    public void AccomplishmentGateRejectsAnImpulseThatAchievedNothing()
    {
        // Module 4 makes the accomplishment compulsory for TRADEABILITY. The structure is still
        // tracked either way, because modules 2 and 3 draw trendlines from valleys and peaks without
        // requiring them to be validated imbalances first - gating detection on the accomplishment
        // deadlocks the method, since the accomplishment that matters is a trendline break.
        List<AlfonsoBar> bars = [];
        int index = 0;
        for (decimal price = 130m; price > 100m; price -= 2m)
            bars.Add(Erc(index++, price, price - 2m));
        for (int step = 0; step < 3; step++)
            bars.Add(Basing(index++, 100m));
        bars.Add(Erc(index++, 100.1m, 104m));
        bars.Add(Erc(index++, 104m, 108m));
        bars.Add(Bar(index, 108m, 109m, 107m, 108.5m));

        (_, List<Imbalance> gated) = Run(bars);
        Assert.That(gated, Has.Count.EqualTo(1));
        Assert.That(gated[0].Accomplished, Is.EqualTo(Accomplishment.None));
        Assert.That(gated[0].MeetsTradeabilityCriteria, Is.False, "no accomplishment, so not tradeable");
        Assert.That(gated[0].IsTradeable, Is.False);

        (_, List<Imbalance> ungated) = Run(bars, new ImbalanceOptions { RequireAccomplishment = false });
        Assert.That(ungated, Has.Count.EqualTo(1));
        Assert.That(ungated[0].Accomplished, Is.EqualTo(Accomplishment.None));
    }

    [Test]
    public void StopAndTargetFollowThePaddingAndFixedRewardRules()
    {
        // Module 10 pads protection by 25% of the zone width; module 11 exits at a fixed 3:1
        // "three times the width of the imbalance including the padding".
        Imbalance zone = new()
        {
            Interval = Interval,
            Kind = ImbalanceKind.Demand,
            Proximal = 100m,
            Distal = 96m,
            BaseStart = Start,
            BaseEnd = Start,
            ConfirmedAt = Start,
            BaseCandleCount = 1,
            Strength = ImpulseStrength.Strong,
            Accomplished = Accomplishment.TrendlineBreak,
            ImpulseToBaseRatio = 3m,
            ImpulseDisplacement = 12m,
            ImpulseBarsTracked = 2,
            IsContinuationPattern = false,
            MeetsTradeabilityCriteria = true
        };

        Assert.That(zone.StopPrice(0.25m), Is.EqualTo(95m));
        Assert.That(zone.TargetPrice(0.25m, 3m), Is.EqualTo(115m));
    }

    [Test]
    public void SupplyZoneMirrorsDemandExactly()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;
        for (decimal price = 90m; price < 100m; price += 2m)
            bars.Add(Erc(index++, price, price + 2m));
        for (int step = 0; step < 3; step++)
            bars.Add(Basing(index++, 100m));
        bars.Add(Erc(index++, 99.9m, 92m));
        bars.Add(Erc(index++, 92m, 82m));
        bars.Add(Bar(index, 82m, 83m, 81m, 82.5m));

        (_, List<Imbalance> created) = Run(bars);

        Assert.That(created, Has.Count.EqualTo(1));
        Imbalance zone = created[0];
        Assert.That(zone.Kind, Is.EqualTo(ImbalanceKind.Supply));
        Assert.That(zone.Distal, Is.EqualTo(101m));
        Assert.That(zone.Proximal, Is.EqualTo(99.95m));
        Assert.That(zone.StopPrice(0.25m) > zone.Distal, Is.True);
        Assert.That(zone.TargetPrice(0.25m, 3m) < zone.Proximal, Is.True);
    }

    [Test]
    public void OptionsRejectAnErcThresholdThatWouldAlsoCountAsBasing()
    {
        ImbalanceOptions options = new()
        {
            MaximumBasingBodyRatio = 0.8m,
            ExtendedRangeBodyRatio = 0.5m
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }
}
