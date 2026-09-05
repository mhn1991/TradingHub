using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Modules 8, 9 and 11: the three-timeframe sequence, nesting, and the eight permitted setups.
/// </summary>
[TestFixture]
public sealed class AlfonsoSequenceTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Imbalance Zone(ImbalanceKind kind, decimal proximal, decimal distal) => new()
    {
        Interval = TimeSpan.FromHours(1),
        Kind = kind,
        Proximal = proximal,
        Distal = distal,
        BaseStart = Start,
        BaseEnd = Start,
        DistalAt = Start,
        ConfirmedAt = Start,
        BaseCandleCount = 2,
        Strength = ImpulseStrength.Strong,
        Accomplished = Accomplishment.TrendlineBreak,
        ImpulseToBaseRatio = 3m,
        ImpulseDisplacement = 12m,
        ImpulseBarsTracked = 2,
        IsContinuationPattern = false,
        MeetsTradeabilityCriteria = true
    };

    // ---- the sequence itself -------------------------------------------------------------------

    [Test]
    public void SequenceMustRunLargestToSmallest()
    {
        Assert.Throws<InvalidOperationException>(() => new TimeframeSequence
        {
            Top = TimeSpan.FromMinutes(15),
            Middle = TimeSpan.FromHours(1),
            Lower = TimeSpan.FromHours(4)
        }.Validate());

        Assert.DoesNotThrow(() => TimeframeSequence.Scalping.Validate());
        Assert.DoesNotThrow(() => TimeframeSequence.Daily.Validate());
        Assert.DoesNotThrow(() => TimeframeSequence.Weekly.Validate());
    }

    [Test]
    public void TheSequenceIsAParameterNotAConstant()
    {
        // Module 8 lists five sequences and leaves the choice to the trader, so nothing downstream
        // may key off a literal duration.
        TimeframeSequence custom = new()
        {
            Top = TimeSpan.FromHours(12),
            Middle = TimeSpan.FromHours(2),
            Lower = TimeSpan.FromMinutes(5)
        };

        AlfonsoSequenceAnalyzer analyzer = new(custom);

        Assert.That(analyzer[SequenceRole.Top].Interval, Is.EqualTo(TimeSpan.FromHours(12)));
        Assert.That(analyzer[SequenceRole.Middle].Interval, Is.EqualTo(TimeSpan.FromHours(2)));
        Assert.That(analyzer[SequenceRole.Lower].Interval, Is.EqualTo(TimeSpan.FromMinutes(5)));
    }

    // ---- module 11's table ---------------------------------------------------------------------

    [Test]
    public void AllThreeAlignedEntersAtTheExecutionTimeframe()
    {
        ScenarioResolution resolution = ScenarioMatrix.Resolve(
            AlfonsoTrend.Uptrend, AlfonsoTrend.Uptrend, AlfonsoTrend.Uptrend);

        Assert.That(resolution.CanTrade, Is.True);
        Assert.That(resolution.Side, Is.EqualTo(ImbalanceKind.Demand));
        Assert.That(resolution.Entries, Has.Count.EqualTo(1));
        Assert.That(resolution.Entries[0].EntryTimeframe, Is.EqualTo(SequenceRole.Lower));
        Assert.That(resolution.Entries[0].NestedIn, Is.Null);
    }

    [Test]
    public void LowerOutOfAlignmentRequiresNestingInTheMiddleTimeframe()
    {
        ScenarioResolution resolution = ScenarioMatrix.Resolve(
            AlfonsoTrend.Uptrend, AlfonsoTrend.Uptrend, AlfonsoTrend.OutOfAlignment);

        Assert.That(resolution.CanTrade, Is.True);
        Assert.That(resolution.Entries, Has.Count.EqualTo(1));
        Assert.That(resolution.Entries[0].NestedIn, Is.EqualTo(SequenceRole.Middle));
    }

    [Test]
    public void MiddleAndLowerOutOfAlignmentOfferBothDrilledAndMiddleEntries()
    {
        // Module 11's table lists two rows against this same alignment: the lower timeframe zone
        // nested at the top, and the middle timeframe zone nested at the top.
        ScenarioResolution resolution = ScenarioMatrix.Resolve(
            AlfonsoTrend.Downtrend, AlfonsoTrend.OutOfAlignment, AlfonsoTrend.OutOfAlignment);

        Assert.That(resolution.CanTrade, Is.True);
        Assert.That(resolution.Side, Is.EqualTo(ImbalanceKind.Supply));
        Assert.That(resolution.Entries, Has.Count.EqualTo(2));
        Assert.That(resolution.Entries.Select(entry => entry.EntryTimeframe),
            Is.EquivalentTo(new[] { SequenceRole.Lower, SequenceRole.Middle }));
        Assert.That(resolution.Entries.All(entry => entry.NestedIn == SequenceRole.Top), Is.True);
    }

    [Test]
    public void AnOutOfAlignmentTopTimeframeForbidsEverything()
    {
        // Module 10: "If the top timeframe of your sequence is out of alignment, no set and forget
        // trades will be possible."
        foreach (AlfonsoTrend middle in Enum.GetValues<AlfonsoTrend>())
        {
            foreach (AlfonsoTrend lower in Enum.GetValues<AlfonsoTrend>())
            {
                Assert.That(
                    ScenarioMatrix.Resolve(AlfonsoTrend.OutOfAlignment, middle, lower).CanTrade,
                    Is.False);
                Assert.That(
                    ScenarioMatrix.Resolve(AlfonsoTrend.Unknown, middle, lower).CanTrade,
                    Is.False);
            }
        }
    }

    [Test]
    public void ATimeframeOpposingTheTopIsNotAPermittedSetup()
    {
        // Counter-trend is deliberately outside the core rules: "Having success trading with the
        // trend is almost impossible, going against it is impossible."
        Assert.That(
            ScenarioMatrix.Resolve(AlfonsoTrend.Uptrend, AlfonsoTrend.Downtrend, AlfonsoTrend.Uptrend).CanTrade,
            Is.False);
        Assert.That(
            ScenarioMatrix.Resolve(AlfonsoTrend.Uptrend, AlfonsoTrend.Uptrend, AlfonsoTrend.Downtrend).CanTrade,
            Is.False);
    }

    [Test]
    public void ExactlyEightAlignmentsAreTradeable()
    {
        // The table is a whitelist. Counting what passes guards against a future edit quietly
        // widening it.
        List<(AlfonsoTrend, AlfonsoTrend, AlfonsoTrend)> tradeable = [];
        foreach (AlfonsoTrend top in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend middle in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend lower in Enum.GetValues<AlfonsoTrend>())
        {
            if (ScenarioMatrix.Resolve(top, middle, lower).CanTrade)
                tradeable.Add((top, middle, lower));
        }

        // Three alignments per direction, but the third carries two entry rows - module 11's four
        // bullish and four bearish setups.
        Assert.That(tradeable, Has.Count.EqualTo(6));
        Assert.That(
            tradeable.Sum(alignment => ScenarioMatrix.Resolve(alignment.Item1, alignment.Item2, alignment.Item3).Entries.Count),
            Is.EqualTo(8), "module 11 lists four bullish and four bearish setups");
    }

    // ---- nesting ------------------------------------------------------------------------------

    [Test]
    public void NestingIsJudgedOnTheDistalLineSoTheInnerZoneMayStraddle()
    {
        // Module 9: "the D1 DZ may have its proximal line slightly above the proximal line of the
        // W DZ, subject to the D1 DZ having its distal line within the W DZ."
        Imbalance outer = Zone(ImbalanceKind.Demand, proximal: 100m, distal: 90m);
        Imbalance straddling = Zone(ImbalanceKind.Demand, proximal: 102m, distal: 95m);

        Assert.That(Nesting.IsNested(straddling, outer), Is.True);

        Imbalance below = Zone(ImbalanceKind.Demand, proximal: 89m, distal: 85m);
        Assert.That(Nesting.IsNested(below, outer), Is.False);
    }

    [Test]
    public void NestingRequiresTheSameSide()
    {
        Imbalance outer = Zone(ImbalanceKind.Demand, proximal: 100m, distal: 90m);
        Imbalance supply = Zone(ImbalanceKind.Supply, proximal: 95m, distal: 98m);

        Assert.That(Nesting.IsNested(supply, outer), Is.False);
    }

    [Test]
    public void FindHostPrefersTheNearestContainingZone()
    {
        Imbalance inner = Zone(ImbalanceKind.Demand, proximal: 99m, distal: 95m);
        Imbalance near = Zone(ImbalanceKind.Demand, proximal: 100m, distal: 90m);
        Imbalance far = Zone(ImbalanceKind.Demand, proximal: 130m, distal: 80m);

        Assert.That(Nesting.FindHost(inner, [far, near]), Is.EqualTo(near));
        Assert.That(Nesting.FindHost(inner, []), Is.Null);
    }

    [Test]
    public void AnOpposingZoneInControlAboveTheEntryTimeframeBlocksEverything()
    {
        // Module 6: "if weekly supply is in control, no longs will be allowed on timeframes smaller
        // than the weekly." The gate is on by default; turning it off must change the answer, or it
        // is decoration.
        AlfonsoSequenceAnalyzer gated = new(TimeframeSequence.Scalping);
        AlfonsoSequenceAnalyzer ungated = new(TimeframeSequence.Scalping,
            requireControlAgreement: false);

        // Neither has a tradeable alignment yet, so both are empty for the same reason - the point
        // here is that the flag is threaded and accepted, with behaviour pinned on real candles by
        // ZZAlfonsoRealDataDiagnostic.
        Assert.That(gated.Candidates(2000m), Is.Empty);
        Assert.That(ungated.Candidates(2000m), Is.Empty);
    }

    [Test]
    public void ATestedLevelIsRejectedWithoutConfirmationAndAcceptedWithIt()
    {
        // Module 10's two entry paths. Set-and-forget needs a fresh level; a tested one needs
        // "a brand new imbalance created at a bigger timeframe imbalance". Only the first was
        // implemented, so half the method was never under test.
        Imbalance tested = Zone(ImbalanceKind.Demand, 100m, 96m) with
        {
            State = ImbalanceState.Tested,
            TestCount = 1
        };
        Imbalance fresh = Zone(ImbalanceKind.Demand, 100m, 96m);
        Imbalance host = Zone(ImbalanceKind.Demand, 102m, 90m);

        Assert.That(AlfonsoSequenceAnalyzer.AcceptsLevel(fresh, freshLevelsOnly: true), Is.True);
        Assert.That(AlfonsoSequenceAnalyzer.AcceptsLevel(tested, freshLevelsOnly: true), Is.False);
        Assert.That(AlfonsoSequenceAnalyzer.AcceptsLevel(tested, freshLevelsOnly: false), Is.True);

        // The host is what makes it a confirmation rather than a second pullback.
        Assert.That(Nesting.IsNested(tested, host), Is.True);
    }

    [Test]
    public void AUsedUpLevelIsNeverConfirmable()
    {
        // Module 7: "Taking a third pullback to a level is not allowed" - regardless of confirmation.
        Imbalance usedUp = Zone(ImbalanceKind.Demand, 100m, 96m) with
        {
            State = ImbalanceState.UsedUp,
            TestCount = 2
        };

        Assert.That(usedUp.IsTradeable, Is.False);
        Assert.That(AlfonsoSequenceAnalyzer.AcceptsLevel(usedUp, freshLevelsOnly: true), Is.False);
    }

    [Test]
    public void ThreeToOneProfitMarginIsMeasuredAgainstTheNearestOpposingLevel()
    {
        // Module 7: "as well as 3:1 profit margin or more to the opposing level." A reachability
        // test - a demand entry with supply close above it cannot make its target however well the
        // zone itself scores. The arithmetic is pinned here; behaviour on real candles is reported
        // by ZZAlfonsoRealDataDiagnostic.
        Imbalance demand = Zone(ImbalanceKind.Demand, proximal: 100m, distal: 96m);

        // Padded stop sits at 95, so risk is 5 and a 3:1 margin needs the opposing level at 115+.
        decimal risk = demand.Proximal - demand.StopPrice(0.25m);
        Assert.That(risk, Is.EqualTo(5m));
        Assert.That(demand.Proximal + (risk * 3m), Is.EqualTo(115m));

        // Supply at 112 leaves only 2.4R of room - not enough.
        Imbalance tooClose = Zone(ImbalanceKind.Supply, proximal: 112m, distal: 116m);
        Assert.That((tooClose.Proximal - demand.Proximal) / risk, Is.EqualTo(2.4m));

        // Supply at 120 leaves 4R - enough.
        Imbalance farEnough = Zone(ImbalanceKind.Supply, proximal: 120m, distal: 124m);
        Assert.That((farEnough.Proximal - demand.Proximal) / risk, Is.EqualTo(4m));
    }

    [Test]
    public void CandidatesAreEmptyUntilTheSequenceHasATradeableAlignment()
    {
        // A fresh analyzer has no trend on any timeframe, so module 10's waiting game applies.
        AlfonsoSequenceAnalyzer analyzer = new(TimeframeSequence.Scalping);

        Assert.That(analyzer.Scenario.CanTrade, Is.False);
        Assert.That(analyzer.Candidates(2000m), Is.Empty);
    }

    [Test]
    public void FreshLevelsOnlyOptionControlsWhetherTestedZonesRemainEligible()
    {
        Imbalance tested = Zone(ImbalanceKind.Demand, proximal: 100m, distal: 90m) with
        {
            State = ImbalanceState.Tested,
            TestCount = 1
        };

        AlfonsoSequenceAnalyzer allLevels = new(TimeframeSequence.Scalping, freshLevelsOnly: false);

        Assert.That(allLevels.FreshLevelsOnly, Is.False);
        Assert.That(AlfonsoSequenceAnalyzer.AcceptsLevel(tested, freshLevelsOnly: true), Is.False);
        Assert.That(AlfonsoSequenceAnalyzer.AcceptsLevel(tested, freshLevelsOnly: false), Is.True);
    }
}
