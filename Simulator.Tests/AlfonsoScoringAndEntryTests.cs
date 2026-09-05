using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// The rules added after re-reading the course PDFs end to end: module 7's zone grading and its
/// rule that a higher-timeframe impulse which never became an imbalance negates the lower-timeframe
/// zones nested at it, module 10's half-of-the-imbalance entry, and module 3's aggressive
/// over-extension trendline.
/// </summary>
[TestFixture]
public sealed class AlfonsoScoringAndEntryTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Imbalance Zone(
        ImbalanceKind kind = ImbalanceKind.Demand,
        decimal proximal = 100m,
        decimal distal = 96m,
        ImpulseStrength strength = ImpulseStrength.Strong,
        Accomplishment accomplished = Accomplishment.TrendlineBreak,
        int baseCandles = 3,
        decimal ratio = 3m,
        ImbalanceState state = ImbalanceState.Fresh,
        int minute = 0) => new()
    {
        Interval = TimeSpan.FromHours(4),
        Kind = kind,
        Proximal = proximal,
        Distal = distal,
        BaseStart = Start.AddMinutes(minute),
        BaseEnd = Start.AddMinutes(minute),
        DistalAt = Start.AddMinutes(minute),
        ConfirmedAt = Start.AddMinutes(minute),
        BaseCandleCount = baseCandles,
        Strength = strength,
        Accomplished = accomplished,
        ImpulseToBaseRatio = ratio,
        ImpulseDisplacement = 12m,
        ImpulseBarsTracked = 2,
        IsContinuationPattern = false,
        MeetsTradeabilityCriteria = true,
        State = state
    };

    // ---- Module 7: scoring ------------------------------------------------------------------

    /// <summary>
    /// The best zone module 7 describes: a gap away, more than one accomplishment, a tight base,
    /// never tested, past the 2:1 bar. Every qualifier at full marks is the top of the scale.
    /// </summary>
    [Test]
    public void AZoneThatSatisfiesEveryQualifierScoresTheMaximum()
    {
        ZoneScore score = ZoneScorer.Score(Zone(
            strength: ImpulseStrength.Gap,
            accomplished: Accomplishment.TrendlineBreak | Accomplishment.OpposingImbalanceEliminated,
            baseCandles: 2));

        Assert.That(score.Total, Is.EqualTo(ZoneScorer.MaximumPoints));
        Assert.That(score.Grade, Is.EqualTo(ZoneGrade.Strong));
    }

    /// <summary>
    /// "Very low score = 0." A weak departure cannot be carried to a passing grade by the other
    /// qualifiers, however good they are.
    /// </summary>
    [Test]
    public void AWeakDepartureIsGradedWeakNoMatterWhatElseIsTrue()
    {
        ZoneScore score = ZoneScorer.Score(Zone(
            strength: ImpulseStrength.Weak,
            accomplished: Accomplishment.TrendlineBreak | Accomplishment.ExtremeBroken,
            baseCandles: 1));

        Assert.That(score.Impulse, Is.Zero);
        Assert.That(score.Grade, Is.EqualTo(ZoneGrade.Weak));
    }

    /// <summary>
    /// Module 4: an impulse that accomplished nothing is not an imbalance. It cannot grade above
    /// weak even off a gap.
    /// </summary>
    [Test]
    public void AZoneWithNoAccomplishmentIsGradedWeak()
    {
        ZoneScore score = ZoneScorer.Score(Zone(
            strength: ImpulseStrength.Gap, accomplished: Accomplishment.None));

        Assert.That(score.Grade, Is.EqualTo(ZoneGrade.Weak));
    }

    /// <summary>
    /// Freshness is scored, not merely gated: "The first pullback is always the highest odds ...
    /// The second pullback does not have the same odds."
    /// </summary>
    [Test]
    public void FreshnessMovesTheScoreDownAsTheLevelIsUsed()
    {
        int fresh = ZoneScorer.Score(Zone(state: ImbalanceState.Fresh)).Freshness;
        int tested = ZoneScorer.Score(Zone(state: ImbalanceState.Tested)).Freshness;
        int usedUp = ZoneScorer.Score(Zone(state: ImbalanceState.UsedUp)).Freshness;

        Assert.That(fresh, Is.GreaterThan(tested));
        Assert.That(tested, Is.GreaterThan(usedUp));
        Assert.That(usedUp, Is.Zero);
    }

    /// <summary>
    /// "We want to see a maximum of 4-6 candlesticks at the base", and the worked example praises
    /// four tight candles while faulting bases with "more than six".
    /// </summary>
    [Test]
    public void ABaseBeyondTheCourseMaximumScoresNothingForStructure()
    {
        Assert.That(ZoneScorer.Score(Zone(baseCandles: 4)).BaseStructure, Is.EqualTo(2));
        Assert.That(ZoneScorer.Score(Zone(baseCandles: 6)).BaseStructure, Is.EqualTo(1));
        Assert.That(ZoneScorer.Score(Zone(baseCandles: 9)).BaseStructure, Is.Zero);
    }

    /// <summary>
    /// The 2:1 rule is a threshold, not a gradation: "A 2:1 RR is a minimum requirement for
    /// tradeability. It does not negate the level as a valid imbalance."
    /// </summary>
    [Test]
    public void TheTwoToOneRuleScoresAsPassOrFail()
    {
        Assert.That(ZoneScorer.Score(Zone(ratio: 1.9m)).RewardRisk, Is.Zero);
        Assert.That(ZoneScorer.Score(Zone(ratio: 2m)).RewardRisk, Is.EqualTo(1));
        Assert.That(ZoneScorer.Score(Zone(ratio: 40m)).RewardRisk, Is.EqualTo(1));
    }

    /// <summary>The grade cut-offs are conventions and must actually be settable.</summary>
    [Test]
    public void GradeThresholdsAreConfigurable()
    {
        Imbalance zone = Zone(baseCandles: 6, state: ImbalanceState.Tested);
        ImbalanceOptions strict = new() { StrongGradePoints = 10, MediumGradePoints = 9 };

        Assert.That(ZoneScorer.Grade(zone), Is.EqualTo(ZoneGrade.Medium));
        Assert.That(ZoneScorer.Grade(zone, strict), Is.EqualTo(ZoneGrade.Weak));
    }

    // ---- Module 10: half of the imbalance ---------------------------------------------------

    /// <summary>
    /// "Use half the width of the original imbalance." The entry moves; protection does not, so the
    /// half entry risks less and needs less travel for the same 3:1.
    /// </summary>
    [Test]
    public void TheHalfEntrySitsMidZoneAndKeepsTheStopBeyondTheDistal()
    {
        Imbalance demand = Zone(proximal: 100m, distal: 96m);

        Assert.That(demand.EntryPrice(ZoneEntryPlacement.Proximal), Is.EqualTo(100m));
        Assert.That(demand.EntryPrice(ZoneEntryPlacement.Midpoint), Is.EqualTo(98m));

        decimal stop = demand.StopPrice(0.25m);
        Assert.That(stop, Is.EqualTo(95m));

        decimal full = demand.TargetPrice(0.25m, 3m, ZoneEntryPlacement.Proximal);
        decimal half = demand.TargetPrice(0.25m, 3m, ZoneEntryPlacement.Midpoint);

        // 5 points of risk from the proximal, 3 from the midpoint.
        Assert.That(full, Is.EqualTo(115m));
        Assert.That(half, Is.EqualTo(107m));
    }

    /// <summary>A supply zone mirrors it: the midpoint is below the proximal, the stop above the distal.</summary>
    [Test]
    public void TheHalfEntryMirrorsForSupply()
    {
        Imbalance supply = Zone(kind: ImbalanceKind.Supply, proximal: 100m, distal: 104m);

        Assert.That(supply.EntryPrice(ZoneEntryPlacement.Midpoint), Is.EqualTo(102m));
        Assert.That(supply.StopPrice(0.25m), Is.EqualTo(105m));
        Assert.That(supply.TargetPrice(0.25m, 3m, ZoneEntryPlacement.Midpoint), Is.EqualTo(93m));
    }

    /// <summary>The default has to stay the proximal line, so existing results are unaffected.</summary>
    [Test]
    public void TheDefaultPlacementIsStillTheProximalLine()
    {
        Assert.That(new ImbalanceOptions().EntryPlacement, Is.EqualTo(ZoneEntryPlacement.Proximal));
        Assert.That(
            Zone().TargetPrice(0.25m, 3m),
            Is.EqualTo(Zone().TargetPrice(0.25m, 3m, ZoneEntryPlacement.Proximal)));
    }

    // ---- Module 7: an invalid higher-timeframe impulse negates what nests at it ---------------

    /// <summary>
    /// "A bigger timeframe impulse that doesn't become an imbalance negates lower timeframe
    /// imbalances." A host that accomplished nothing is not an imbalance under module 4, so it may
    /// not carry a nested entry - but it may once it has accomplished something.
    /// </summary>
    [Test]
    public void ANestedEntryNeedsAHostThatIsAValidImbalance()
    {
        Imbalance inner = Zone(proximal: 100m, distal: 98m, minute: 1);
        Imbalance unaccomplished = Zone(
            proximal: 101m, distal: 95m, accomplished: Accomplishment.None, minute: 2);
        Imbalance accomplished = Zone(proximal: 101m, distal: 95m, minute: 3);

        Assert.That(Nesting.IsNested(inner, unaccomplished), Is.True, "the geometry is nested either way");

        Assert.That(
            Nesting.FindHost(inner, [unaccomplished]),
            Is.Not.Null,
            "geometry alone still finds it; the rule is applied by the analyzer");

        Assert.That(
            Nesting.FindHost(
                inner,
                new[] { unaccomplished }.Where(host => host.Accomplished != Accomplishment.None)),
            Is.Null);

        Assert.That(
            Nesting.FindHost(
                inner,
                new[] { accomplished }.Where(host => host.Accomplished != Accomplishment.None)),
            Is.Not.Null);
    }

    /// <summary>The rule is on by default; the opt-out exists for controlled comparisons.</summary>
    [Test]
    public void TheHostValidityRuleIsOnByDefault()
    {
        Assert.That(new Agent.Strategies.Alfonso.AlfonsoStrategyOptions().RequireValidHost, Is.True);
        Assert.That(
            new Agent.Strategies.Alfonso.AlfonsoStrategyOptions().MinimumZoneGrade,
            Is.EqualTo(ZoneGrade.Weak),
            "the grade must gate nothing until it has been measured");
    }

    // ---- Module 3: the over-extension trendline ----------------------------------------------

    private static SwingPoint Swing(int index, decimal price, ImbalanceKind kind) => new()
    {
        Index = index,
        At = Start.AddHours(index * 4),
        Price = price,
        Kind = kind
    };

    /// <summary>
    /// "In over-extension with three or more consecutive CPs, the trendlines can be drawn more
    /// aggressively connecting the last three CPs." Three descending continuation peaks draw a
    /// bearish line; the same three out of order draw nothing.
    /// </summary>
    [Test]
    public void ThreeDescendingContinuationPatternsDrawTheOverExtensionLine()
    {
        List<SwingPoint> peaks =
        [
            Swing(0, 120m, ImbalanceKind.Supply),
            Swing(4, 116m, ImbalanceKind.Supply),
            Swing(8, 112m, ImbalanceKind.Supply)
        ];
        List<decimal> highs = Enumerable.Range(0, 12).Select(i => 120m - i).ToList();
        List<DateTimeOffset> times =
            Enumerable.Range(0, 12).Select(i => Start.AddHours(i * 4)).ToList();

        Trendline? line = TrendlineBuilder.OverExtended(
            peaks, highs, times, currentIndex: 11, TrendlineDirection.Bearish);

        Assert.That(line, Is.Not.Null);
        Assert.That(line!.Direction, Is.EqualTo(TrendlineDirection.Bearish));
        Assert.That(line.Slope, Is.LessThan(0m));
    }

    /// <summary>Anchors that do not run the way the line does are three pauses, not a trendline.</summary>
    [Test]
    public void ContinuationPatternsOutOfOrderDrawNothing()
    {
        List<SwingPoint> peaks =
        [
            Swing(0, 112m, ImbalanceKind.Supply),
            Swing(4, 120m, ImbalanceKind.Supply),
            Swing(8, 116m, ImbalanceKind.Supply)
        ];
        List<decimal> highs = Enumerable.Repeat(130m, 12).ToList();
        List<DateTimeOffset> times =
            Enumerable.Range(0, 12).Select(i => Start.AddHours(i * 4)).ToList();

        Assert.That(
            TrendlineBuilder.OverExtended(peaks, highs, times, 11, TrendlineDirection.Bearish),
            Is.Null);
    }

    /// <summary>Fewer than three continuation patterns is not over-extension, so there is no line.</summary>
    [Test]
    public void TwoContinuationPatternsAreNotEnough()
    {
        List<SwingPoint> peaks =
        [
            Swing(0, 120m, ImbalanceKind.Supply),
            Swing(4, 116m, ImbalanceKind.Supply)
        ];
        List<decimal> highs = Enumerable.Repeat(130m, 12).ToList();
        List<DateTimeOffset> times =
            Enumerable.Range(0, 12).Select(i => Start.AddHours(i * 4)).ToList();

        Assert.That(
            TrendlineBuilder.OverExtended(peaks, highs, times, 11, TrendlineDirection.Bearish),
            Is.Null);
    }

    /// <summary>
    /// The aggressive line is opt-in: module 3 says it "can be" drawn, and a line that exists is a
    /// line that can be broken, which creates imbalances and moves the trend.
    /// </summary>
    [Test]
    public void TheOverExtensionLineIsOffByDefault()
    {
        Assert.That(new AlfonsoTrendOptions().OverExtensionTrendlines, Is.False);
    }
}
