using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Module 6: the supply and demand range, the 20/80 bands, and what it means for an imbalance to be
/// in control.
/// </summary>
[TestFixture]
public sealed class AlfonsoRangeTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Imbalance Zone(ImbalanceKind kind, decimal proximal, decimal distal,
        bool tradeable = true, ImbalanceState state = ImbalanceState.Fresh) => new()
    {
        Interval = TimeSpan.FromHours(4),
        Kind = kind,
        Proximal = proximal,
        Distal = distal,
        BaseStart = Start,
        BaseEnd = Start.AddMinutes(proximal is var p ? (double)p : 0),
        DistalAt = Start.AddMinutes(proximal is var q ? (double)q : 0),
        ConfirmedAt = Start,
        BaseCandleCount = 2,
        Strength = ImpulseStrength.Strong,
        Accomplished = Accomplishment.TrendlineBreak,
        ImpulseToBaseRatio = 3m,
        ImpulseDisplacement = 12m,
        ImpulseBarsTracked = 2,
        IsContinuationPattern = false,
        MeetsTradeabilityCriteria = tradeable,
        State = state
    };

    /// <summary>
    /// Module 6's own worked example, reproduced exactly: opposing proximal lines 7.5 points apart
    /// at 113.50 and 106, a 20% rule, giving band edges at 112 and 107.50.
    /// </summary>
    [Test]
    public void ReproducesTheWorkedExampleFromTheCourse()
    {
        List<Imbalance> zones =
        [
            Zone(ImbalanceKind.Supply, 113.50m, 115m),
            Zone(ImbalanceKind.Demand, 106m, 104m)
        ];

        SupplyDemandRange range = RangeCalculator.Compute(zones, price: 110m);

        Assert.That(range.SupplyProximal, Is.EqualTo(113.50m));
        Assert.That(range.DemandProximal, Is.EqualTo(106m));
        Assert.That(range.TooHighAbove, Is.EqualTo(112m));
        Assert.That(range.TooLowBelow, Is.EqualTo(107.50m));
        Assert.That(range.Location, Is.EqualTo(RangeLocation.Middle));
        Assert.That(range.AllowsBuying, Is.True);
        Assert.That(range.AllowsSelling, Is.True);
    }

    [Test]
    public void TooHighStopsBuyingAndTooLowStopsSelling()
    {
        // "If the price is too low in the range, stop selling into it using lower timeframes. If the
        // price is too high in the range, stop buying into it using lower timeframes."
        List<Imbalance> zones =
        [
            Zone(ImbalanceKind.Supply, 113.50m, 115m),
            Zone(ImbalanceKind.Demand, 106m, 104m)
        ];

        SupplyDemandRange high = RangeCalculator.Compute(zones, price: 112.5m);
        Assert.That(high.Location, Is.EqualTo(RangeLocation.TooHigh));
        Assert.That(high.AllowsBuying, Is.False);
        Assert.That(high.AllowsSelling, Is.True, "too high blocks buying, not selling");

        SupplyDemandRange low = RangeCalculator.Compute(zones, price: 107m);
        Assert.That(low.Location, Is.EqualTo(RangeLocation.TooLow));
        Assert.That(low.AllowsSelling, Is.False);
        Assert.That(low.AllowsBuying, Is.True);
    }

    [Test]
    public void NearestOpposingZonesBoundTheRange()
    {
        // Price has to deal with the closest levels first, so a distant pair must not widen the
        // range and make an expensive price look mid-range.
        List<Imbalance> zones =
        [
            Zone(ImbalanceKind.Supply, 200m, 205m),
            Zone(ImbalanceKind.Supply, 113.50m, 115m),
            Zone(ImbalanceKind.Demand, 106m, 104m),
            Zone(ImbalanceKind.Demand, 20m, 15m)
        ];

        SupplyDemandRange range = RangeCalculator.Compute(zones, price: 112.5m);

        Assert.That(range.SupplyProximal, Is.EqualTo(113.50m));
        Assert.That(range.DemandProximal, Is.EqualTo(106m));
        Assert.That(range.Location, Is.EqualTo(RangeLocation.TooHigh));
    }

    [Test]
    public void OneSidedMarketsHaveNoRangeAndBlockNothing()
    {
        // "In all-time highs and all-time lows scenarios, we will not be able to calculate the range
        // because there is no opposing imbalance to do the math." Module 11 still offers entries in
        // that case, so a missing range must not read as a prohibition.
        List<Imbalance> onlyDemand = [Zone(ImbalanceKind.Demand, 106m, 104m)];
        SupplyDemandRange range = RangeCalculator.Compute(onlyDemand, price: 110m);

        Assert.That(range.Location, Is.EqualTo(RangeLocation.Unavailable));
        Assert.That(range.AllowsBuying, Is.True);
        Assert.That(range.AllowsSelling, Is.True);
        Assert.That(range.Reason, Does.Contain("all-time-high"));
    }

    [Test]
    public void EliminatedZonesNeverBoundTheRange()
    {
        List<Imbalance> zones =
        [
            Zone(ImbalanceKind.Supply, 113.50m, 115m, state: ImbalanceState.Eliminated),
            Zone(ImbalanceKind.Supply, 130m, 135m),
            Zone(ImbalanceKind.Demand, 106m, 104m)
        ];

        SupplyDemandRange range = RangeCalculator.Compute(zones, price: 112.5m);

        Assert.That(range.SupplyProximal, Is.EqualTo(130m));
        Assert.That(range.Location, Is.EqualTo(RangeLocation.Middle),
            "with the broken level gone, 112.5 is mid-range rather than expensive");
    }

    [Test]
    public void UntradeableZonesStillBoundTheRangeByDefault()
    {
        // Module 7: failing the 2:1 bar "does not negate the level as a valid imbalance". Price still
        // has to travel through it.
        List<Imbalance> zones =
        [
            Zone(ImbalanceKind.Supply, 113.50m, 115m, tradeable: false),
            Zone(ImbalanceKind.Demand, 106m, 104m)
        ];

        Assert.That(RangeCalculator.Compute(zones, 112.5m).Location, Is.EqualTo(RangeLocation.TooHigh));
        Assert.That(
            RangeCalculator.Compute(zones, 112.5m, new RangeOptions { BoundWithUntradeableZones = false })
                .Location,
            Is.EqualTo(RangeLocation.Unavailable));
    }

    [Test]
    public void EdgeFractionAtAHalfIsRejected()
    {
        // At 0.5 the two bands meet and every price is simultaneously too high and too low, so
        // nothing could ever be traded.
        Assert.Throws<InvalidOperationException>(
            () => new RangeOptions { EdgeFraction = 0.5m }.Validate());
    }

    // ---- control ------------------------------------------------------------------------------

    [Test]
    public void ReachingAProximalLineHandsThatZoneControl()
    {
        // "once price reaches the supply [1] proximal line at [A], we consider the imbalance to be
        // in control."
        AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromMinutes(15));
        Assert.That(analyzer.InControl, Is.Null);
        Assert.That(analyzer.Range.Location, Is.EqualTo(RangeLocation.Unavailable));

        foreach (AlfonsoBar bar in DemandThenPullback())
            analyzer.Apply(bar);

        Assert.That(analyzer.InControl, Is.Not.Null);
        Assert.That(analyzer.InControl!.Kind, Is.EqualTo(ImbalanceKind.Demand));
    }

    /// <summary>A demand zone forms, price runs away, then returns to its proximal line.</summary>
    private static List<AlfonsoBar> DemandThenPullback()
    {
        static AlfonsoBar Bar(int index, decimal o, decimal h, decimal l, decimal c) =>
            new(Start.AddMinutes(15 * index), o, h, l, c);

        static AlfonsoBar Erc(int index, decimal open, decimal close)
        {
            decimal pad = Math.Abs(close - open) * 0.05m;
            return Bar(index, open, Math.Max(open, close) + pad, Math.Min(open, close) - pad, close);
        }

        List<AlfonsoBar> bars = [];
        int index = 0;
        for (decimal price = 110m; price > 100m; price -= 2m)
            bars.Add(Erc(index++, price, price - 2m));
        for (int step = 0; step < 3; step++)
            bars.Add(Bar(index++, 99.95m, 101m, 99m, 100.05m));

        bars.Add(Erc(index++, 100.1m, 108m));
        bars.Add(Erc(index++, 108m, 118m));
        bars.Add(Bar(index++, 118m, 119m, 117m, 118.5m));
        bars.Add(Bar(index++, 118m, 118.5m, 100m, 100.5m));
        return bars;
    }
}
