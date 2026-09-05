using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Module 2 ("Types of Imbalances") rule tests: the drop/rally base and the swing-versus-continuation
/// classification.
/// <para>
/// These two branches had no coverage at all before 2026-09-05 - they were exercised only by
/// <c>ZZAlfonsoRealDataDiagnostic</c>, which is <c>[Explicit]</c> and does not run in the suite.
/// CLAUDE.md records that zone creation is the most bug-prone code in this repo, so the branches the
/// book states outright are pinned here against the text.
/// </para>
/// </summary>
[TestFixture]
public sealed class AlfonsoModuleTwoTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static AlfonsoBar Bar(int index, decimal open, decimal high, decimal low, decimal close) =>
        new(Start.AddMinutes(15 * index), open, high, low, close);

    /// <summary>An extended range candle: body about 90% of the range, wicks symmetric.</summary>
    private static AlfonsoBar Erc(int index, decimal open, decimal close)
    {
        decimal pad = Math.Abs(close - open) * 0.05m;
        return Bar(index, open, Math.Max(open, close) + pad, Math.Min(open, close) - pad, close);
    }

    /// <summary>An ERC with an explicit low, so the two candles of a drop/rally can be made to differ.</summary>
    private static AlfonsoBar ErcWithLow(int index, decimal open, decimal close, decimal low)
    {
        decimal pad = Math.Abs(close - open) * 0.05m;
        return Bar(index, open, Math.Max(open, close) + pad, low, close);
    }

    private static AlfonsoBar ErcWithHigh(int index, decimal open, decimal close, decimal high)
    {
        decimal pad = Math.Abs(close - open) * 0.05m;
        return Bar(index, open, high, Math.Min(open, close) - pad, close);
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

    /// <summary>
    /// Module 2's alternative valley base: "the basing structure of a valley may be formed by non 50%
    /// candlesticks and be made of only a bearish ERC and a bullish ERC (drop/rally)". The bearish
    /// ERC is given the lower low, which is the ordinary case - an ERC closes within 20% of its range
    /// of its close by definition, so a drop candle's low usually sits under the rally candle's.
    /// </summary>
    private static List<AlfonsoBar> DropRallySequence()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;

        // Approach from above, so the turn is a valley rather than a continuation pattern.
        for (decimal price = 120m; price > 112m; price -= 2m)
            bars.Add(Erc(index++, price, price - 2m));

        // The drop/rally pair. No pause: both candles are ERCs and they oppose each other.
        bars.Add(ErcWithLow(index++, 112m, 104m, 103m));   // bearish ERC, low 103
        bars.Add(Erc(index++, 104m, 112m));                // bullish ERC, low ~103.6

        // Leg out and consolidation away.
        bars.Add(Erc(index++, 112m, 122m));
        bars.Add(Bar(index++, 122m, 123m, 121m, 122.5m));
        return bars;
    }

    private static List<AlfonsoBar> RallyDropSequence()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;

        for (decimal price = 100m; price < 108m; price += 2m)
            bars.Add(Erc(index++, price, price + 2m));

        bars.Add(ErcWithHigh(index++, 108m, 116m, 117m));  // bullish ERC, high 117
        bars.Add(Erc(index++, 116m, 108m));                // bearish ERC, high ~116.4

        bars.Add(Erc(index++, 108m, 98m));
        bars.Add(Bar(index++, 98m, 99m, 97m, 97.5m));
        return bars;
    }

    [Test]
    public void DropRallyPairFormsAZoneWithNoBasingCandleAtAll()
    {
        (_, List<Imbalance> created) = Run(DropRallySequence());

        Imbalance? zone = created.FirstOrDefault(z => z.Kind == ImbalanceKind.Demand);
        Assert.That(zone, Is.Not.Null, "module 2's drop/rally base must produce a demand zone");
    }

    /// <summary>
    /// Module 2 makes the PAIR the basing structure, and module 4 binds the geometry to it: "The
    /// distal line of an imbalance must always include the lowest low in the basing structure when
    /// drawing a demand level". The bearish ERC's 103 is that low, not the bullish ERC's.
    /// </summary>
    [Test]
    public void DropRallyDistalIncludesTheOpposingErcsLow()
    {
        (_, List<Imbalance> created) = Run(DropRallySequence());
        Imbalance zone = created.First(z => z.Kind == ImbalanceKind.Demand);

        Assert.Multiple(() =>
        {
            Assert.That(zone.Distal, Is.EqualTo(103m), "the drop candle's low is the low of the base");
            Assert.That(zone.BaseCandleCount, Is.EqualTo(2), "the base is both ERCs, not the turn alone");
        });
    }

    /// <summary>
    /// The opt-out restores the pre-2026-09-05 reading, and it is not inert: the distal moves up to
    /// the rally candle's own low, which is inside the structure the book draws. Pins the default in
    /// both directions so it cannot be flipped back silently.
    /// </summary>
    [Test]
    public void TheSingleCandleDropRallyReadingIsOptInAndMovesTheDistal()
    {
        ImbalanceOptions narrow = new() { DropRallyBaseSpansBothCandles = false };
        (_, List<Imbalance> created) = Run(DropRallySequence(), narrow);
        Imbalance zone = created.First(z => z.Kind == ImbalanceKind.Demand);

        Assert.Multiple(() =>
        {
            Assert.That(new ImbalanceOptions().DropRallyBaseSpansBothCandles, Is.True,
                "the book states the pair IS the base, so it is the default");
            Assert.That(zone.BaseCandleCount, Is.EqualTo(1));
            Assert.That(zone.Distal, Is.GreaterThan(103m),
                "the one-candle reading leaves the drop candle's low outside the zone");
        });
    }

    [Test]
    public void RallyDropMirrorsDropRallyAndTakesTheHigherHigh()
    {
        (_, List<Imbalance> created) = Run(RallyDropSequence());
        Imbalance zone = created.First(z => z.Kind == ImbalanceKind.Supply);

        Assert.Multiple(() =>
        {
            Assert.That(zone.Distal, Is.EqualTo(117m), "the rally candle's high is the high of the base");
            Assert.That(zone.BaseCandleCount, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// Module 2's alternative base is "a bearish ERC and a bullish ERC" - opposing. Two ERCs running
    /// the same way are a leg, not a turn, and must not open a base.
    /// </summary>
    [Test]
    public void TwoErcsRunningTheSameWayAreNotADropRallyBase()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;
        for (decimal price = 120m; price > 112m; price -= 2m)
            bars.Add(Erc(index++, price, price - 2m));

        bars.Add(ErcWithLow(index++, 112m, 104m, 103m));
        bars.Add(Erc(index++, 104m, 96m));                  // bearish again: no turn
        bars.Add(Erc(index++, 96m, 106m));
        bars.Add(Bar(index++, 106m, 107m, 105m, 106.5m));

        (_, List<Imbalance> created) = Run(bars);

        Assert.That(created.Any(z => z.BaseCandleCount == 2 && z.Distal == 103m), Is.False,
            "an unopposed ERC pair is a leg, not module 2's drop/rally base");
    }

    // ---- Swing versus continuation pattern -------------------------------------------------

    /// <summary>
    /// A base whose approach came from BELOW its own distal: price rose into the base, paused, and
    /// carried on rising. That is module 2's CP - "a pause in the market before price resumes the
    /// underlying trend" - not a valley, which is "a V shape formation".
    /// </summary>
    [Test]
    public void ABaseApproachedFromBelowItsDistalIsAContinuationPattern()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;

        // Rise into the pause from well below it.
        for (decimal price = 90m; price < 100m; price += 2m)
            bars.Add(Erc(index++, price, price + 2m));

        for (int step = 0; step < 3; step++)
            bars.Add(Bar(index++, 100m - 0.05m, 101m, 99m, 100m + 0.05m));

        bars.Add(Erc(index++, 100.1m, 108m));
        bars.Add(Erc(index++, 108m, 118m));
        bars.Add(Bar(index++, 118m, 119m, 117m, 118.5m));

        (_, List<Imbalance> created) = Run(bars);
        Imbalance zone = created.First(z => z.Kind == ImbalanceKind.Demand);

        Assert.That(zone.IsContinuationPattern, Is.True,
            "price had already traded below this base, so the base turned nothing");
    }

    /// <summary>
    /// The same structure approached from ABOVE: price fell into the base and turned. That is the
    /// V shape, so it is a swing and module 3 may build a trendline from it.
    /// </summary>
    [Test]
    public void ABaseThatTurnedPriceIsASwingNotAContinuation()
    {
        List<AlfonsoBar> bars = [];
        int index = 0;

        for (decimal price = 110m; price > 100m; price -= 2m)
            bars.Add(Erc(index++, price, price - 2m));

        for (int step = 0; step < 3; step++)
            bars.Add(Bar(index++, 100m - 0.05m, 101m, 99m, 100m + 0.05m));

        bars.Add(Erc(index++, 100.1m, 108m));
        bars.Add(Erc(index++, 108m, 118m));
        bars.Add(Bar(index++, 118m, 119m, 117m, 118.5m));

        (_, List<Imbalance> created) = Run(bars);
        Imbalance zone = created.First(z => z.Kind == ImbalanceKind.Demand);

        Assert.That(zone.IsContinuationPattern, Is.False,
            "the base is the lowest point of its own approach, so it is a valley");
    }

    /// <summary>
    /// Module 2: "When you are in doubt, consider them as a CP." A base with no approach to read at
    /// all is the only doubt the engine can detect, and the switch is off by default - a departure
    /// recorded in PROJECT_STATE.md 3.50 on measured grounds, pinned here in both directions.
    /// </summary>
    [Test]
    public void ABaseWithNoReadableApproachFollowsTheAmbiguousBaseSwitch()
    {
        // Starts on the base itself: there are no candles before it to read an approach from.
        List<AlfonsoBar> bars = [];
        int index = 0;
        for (int step = 0; step < 3; step++)
            bars.Add(Bar(index++, 100m - 0.05m, 101m, 99m, 100m + 0.05m));

        bars.Add(Erc(index++, 100.1m, 108m));
        bars.Add(Erc(index++, 108m, 118m));
        bars.Add(Bar(index++, 118m, 119m, 117m, 118.5m));

        (_, List<Imbalance> byDefault) = Run(bars);
        (_, List<Imbalance> bookRule) = Run(bars,
            new ImbalanceOptions { TreatAmbiguousBaseAsContinuation = true });

        Assert.Multiple(() =>
        {
            Assert.That(new ImbalanceOptions().TreatAmbiguousBaseAsContinuation, Is.False,
                "off by default - see 3.50; turning it on cut trades 127 -> 40");
            Assert.That(byDefault.First(z => z.Kind == ImbalanceKind.Demand).IsContinuationPattern,
                Is.False, "default reads an unreadable approach as a swing");
            Assert.That(bookRule.First(z => z.Kind == ImbalanceKind.Demand).IsContinuationPattern,
                Is.True, "the book's sentence reads it as a CP");
        });
    }
}
