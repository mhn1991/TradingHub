using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;

namespace Simulator.Tests;

[TestFixture]
public sealed class SupplyDemandAnalyzerTests
{
    private static readonly InstrumentKey Instrument = new("EURUSD");
    private static readonly BarInterval Interval = BarInterval.Minutes(15);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static Candle C(int index, decimal open, decimal high, decimal low, decimal close) =>
        TestCandles.Create(Instrument, Start + TimeSpan.FromMinutes(15 * index), Interval, open, high, low, close);

    /// <summary>A flat, zero-body candle - trivially satisfies every base-compactness threshold.</summary>
    private static Candle Doji(int index, decimal price) =>
        C(index, price, price + 0.05m, price - 0.05m, price);

    /// <summary>A no-wick candle (body == range) moving from <paramref name="open"/> to
    /// <paramref name="close"/> - trivially satisfies every departure-conviction threshold.</summary>
    private static Candle Marubozu(int index, decimal open, decimal close) =>
        close >= open ? C(index, open, close, open, close) : C(index, open, open, close, close);

    /// <summary>Runs a full candle list through a fresh analyzer, one <c>Update</c> call per
    /// candle with a growing history snapshot, mirroring <c>ChartAnnotationEngine.ProcessCore</c>'s
    /// contract exactly.</summary>
    private static List<SupplyDemandAnalysisSnapshot> Replay(
        SupplyDemandAnalyzer analyzer,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<SwingPoint>? swings = null,
        decimal atr = 1.0m)
    {
        var history = new List<Candle>();
        var snapshots = new List<SupplyDemandAnalysisSnapshot>();
        long sequence = 0;
        foreach (Candle candle in candles)
        {
            history.Add(candle);
            snapshots.Add(analyzer.Update(candle, history, swings ?? [], atr, sequence++));
        }

        return snapshots;
    }

    private static SupplyDemandCalculationProfile EnabledProfile(
        Action<SupplyDemandCalculationProfileBuilder>? configure = null)
    {
        var builder = new SupplyDemandCalculationProfileBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    // A tiny mutable builder so tests can override only what they care about while keeping
    // Enabled=true, avoiding a wall of `with` clauses per test.
    private sealed class SupplyDemandCalculationProfileBuilder
    {
        private SupplyDemandCalculationProfile _profile = new() { Enabled = true };

        public SupplyDemandCalculationProfileBuilder With(Func<SupplyDemandCalculationProfile, SupplyDemandCalculationProfile> mutate)
        {
            _profile = mutate(_profile);
            return this;
        }

        public SupplyDemandCalculationProfile Build() => _profile;
    }

    // ----- Formation: base + departure ----------------------------------

    [Test]
    public void Update_QualifyingBaseAndDeparture_FormsDemandZone()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        List<SupplyDemandAnalysisSnapshot> snapshots = Replay(analyzer, candles);

        SupplyDemandAnalysisSnapshot last = snapshots[^1];
        Assert.That(last.ActiveZones, Has.Count.EqualTo(1));
        SupplyDemandZone zone = last.ActiveZones[0];
        Assert.Multiple(() =>
        {
            Assert.That(zone.Type, Is.EqualTo(SupplyDemandZoneType.Demand));
            Assert.That(zone.State, Is.EqualTo(SupplyDemandZoneState.ConfirmedFresh));
            Assert.That(zone.ProximalPrice, Is.EqualTo(100.05m));
            Assert.That(zone.DistalPrice, Is.EqualTo(99.95m));
            Assert.That(zone.DepartureAtr, Is.GreaterThanOrEqualTo(1.5m));
        });
    }

    [Test]
    public void Update_QualifyingBaseAndDeparture_FormsSupplyZone()
    {
        // Exact price-mirror of the demand scenario (departure direction flipped).
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 99m),
            Marubozu(2, 99m, 98m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        List<SupplyDemandAnalysisSnapshot> snapshots = Replay(analyzer, candles);

        SupplyDemandZone zone = snapshots[^1].ActiveZones.Single();
        Assert.Multiple(() =>
        {
            Assert.That(zone.Type, Is.EqualTo(SupplyDemandZoneType.Supply));
            Assert.That(zone.ProximalPrice, Is.EqualTo(99.95m));
            Assert.That(zone.DistalPrice, Is.EqualTo(100.05m));
            Assert.That(zone.DepartureAtr, Is.GreaterThanOrEqualTo(1.5m));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Update_LongShortSymmetry_ProducesMirroredMagnitudes(bool bullish)
    {
        List<Candle> candles = bullish
            ? [Doji(0, 100m), Marubozu(1, 100m, 101m), Marubozu(2, 101m, 102m)]
            : [Doji(0, 100m), Marubozu(1, 100m, 99m), Marubozu(2, 99m, 98m)];

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        SupplyDemandZone zone = Replay(analyzer, candles)[^1].ActiveZones.Single();

        Assert.Multiple(() =>
        {
            Assert.That(zone.Type, Is.EqualTo(bullish ? SupplyDemandZoneType.Demand : SupplyDemandZoneType.Supply));
            Assert.That(zone.DepartureAtr, Is.EqualTo(2.0m));
            Assert.That(zone.DepartureEfficiency, Is.EqualTo(1.0m));
            Assert.That(Math.Abs(zone.ProximalPrice - zone.DistalPrice), Is.EqualTo(0.10m));
        });
    }

    [Test]
    public void Update_BaseTooWide_NoZoneForms()
    {
        // The only candidate base window is a single, oversized candle (2.0 ATR range) - every
        // base length fails, regardless of how strong the following departure is.
        var candles = new List<Candle>
        {
            C(0, 100m, 101.0m, 99.0m, 100m),
            Marubozu(1, 100m, 101.5m),
            Marubozu(2, 101.5m, 103m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        SupplyDemandAnalysisSnapshot last = Replay(analyzer, candles)[^1];

        Assert.That(last.ActiveZones, Is.Empty);
    }

    [Test]
    public void Update_DepartureTooWeak_NoZoneForms_ThenFormsOnceStrongDepartureFollows()
    {
        var weak = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 100.5m) // 0.5 ATR - below MinimumDepartureAtr (1.5)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        List<SupplyDemandAnalysisSnapshot> weakSnapshots = Replay(analyzer, weak);
        Assert.That(weakSnapshots[^1].ActiveZones, Is.Empty, "a single weak departure candle must not form a zone");

        // Confirm the analyzer isn't simply broken - a properly strong departure right after does form one.
        var strong = new List<Candle> { Marubozu(2, 100.5m, 102m) };
        var history = new List<Candle>(weak) { strong[0] };
        SupplyDemandAnalysisSnapshot final = analyzer.Update(strong[0], history, [], 1.0m, 2);
        Assert.That(final.ActiveZones, Has.Count.EqualTo(1));
    }

    // ----- No early visibility -------------------------------------------

    [Test]
    public void Update_ZoneOnlyVisibleStartingOnDepartureConfirmationCandle()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m), // departure len 1: 1.0 ATR < 1.5 required, not yet visible
            Marubozu(2, 101m, 102m)  // departure len 2: 2.0 ATR, zone confirms exactly here
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        List<SupplyDemandAnalysisSnapshot> snapshots = Replay(analyzer, candles);

        Assert.Multiple(() =>
        {
            Assert.That(snapshots[0].ActiveZones, Is.Empty, "no zone before any departure candle");
            Assert.That(snapshots[1].ActiveZones, Is.Empty, "no zone while departure is still below threshold");
            Assert.That(snapshots[2].ActiveZones, Has.Count.EqualTo(1), "zone must appear exactly on the confirming candle");
            Assert.That(snapshots[2].ActiveZones[0].AvailableAt, Is.EqualTo(candles[2].CloseTime));
        });
    }

    // ----- Boundary modes --------------------------------------------------

    [Test]
    public void Update_BoundaryModes_ProduceDistinctBoundaries()
    {
        var candles = new List<Candle>
        {
            C(0, 100.00m, 100.06m, 99.97m, 100.03m), // small non-doji base body
            Marubozu(1, 100.03m, 101.03m),
            Marubozu(2, 101.03m, 102.03m)
        };

        SupplyDemandZone FormWith(ZoneBoundaryMode mode)
        {
            var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { BoundaryMode = mode })));
            return Replay(analyzer, candles)[^1].ActiveZones.Single();
        }

        SupplyDemandZone fullWick = FormWith(ZoneBoundaryMode.FullWickRange);
        SupplyDemandZone bodyToExtreme = FormWith(ZoneBoundaryMode.BodyToExtreme);
        SupplyDemandZone departureOrigin = FormWith(ZoneBoundaryMode.DepartureOriginBody);

        Assert.Multiple(() =>
        {
            Assert.That(fullWick.ProximalPrice, Is.EqualTo(100.06m));
            Assert.That(fullWick.DistalPrice, Is.EqualTo(99.97m));
            Assert.That(bodyToExtreme.ProximalPrice, Is.EqualTo(100.03m));
            Assert.That(bodyToExtreme.DistalPrice, Is.EqualTo(99.97m));
            Assert.That(departureOrigin.ProximalPrice, Is.EqualTo(100.03m));
            Assert.That(departureOrigin.DistalPrice, Is.EqualTo(100.00m));
            Assert.That(new[] { fullWick.ProximalPrice, bodyToExtreme.ProximalPrice, departureOrigin.ProximalPrice }.Distinct().Count(), Is.EqualTo(2));
        });
    }

    // ----- Structure break / fair value gap options ------------------------

    [Test]
    public void Update_RequireStructureBreak_BlocksWithoutPriorSwing()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { RequireStructureBreak = true })));
        Assert.That(Replay(analyzer, candles)[^1].ActiveZones, Is.Empty);
    }

    [Test]
    public void Update_RequireStructureBreak_AllowsOncePriorSwingBroken()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };
        SwingPoint priorHigh = new()
        {
            PivotTime = Start - TimeSpan.FromMinutes(30),
            ConfirmedAt = Start - TimeSpan.FromMinutes(15),
            Price = 100.5m,
            Type = SwingType.High,
            Strength = 2
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { RequireStructureBreak = true })));
        List<SupplyDemandAnalysisSnapshot> snapshots = Replay(analyzer, candles, [priorHigh]);

        SupplyDemandZone zone = snapshots[^1].ActiveZones.Single();
        Assert.That(zone.BrokeStructure, Is.True);
    }

    [Test]
    public void Update_RequireFairValueGap_BlocksContiguousDeparture()
    {
        // Candle 3 has a deep lower wick reaching back below candle 1's high (101), so the
        // standard i-1/i+1 FVG check finds no gap - unlike a pure marubozu chain, where every
        // consecutive triplet trivially gaps because the middle candle's range is skipped by the
        // comparison and price only ever moves one way.
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m),
            C(3, 102m, 103m, 100.5m, 103m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { RequireFairValueGap = true })));
        Assert.That(Replay(analyzer, candles)[^1].ActiveZones, Is.Empty);
    }

    [Test]
    public void Update_RequireFairValueGap_AllowsGappedDeparture()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            C(2, 101m, 102.5m, 101m, 102.5m),
            C(3, 102.5m, 103.5m, 101.5m, 103.5m) // low (101.5) gaps above candle 1's high (101)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { RequireFairValueGap = true })));
        SupplyDemandZone zone = Replay(analyzer, candles)[^1].ActiveZones.Single();

        Assert.That(zone.HasFairValueGap, Is.True);
    }

    [Test]
    public void Update_RequireOriginationMove_BlocksBaseWithNoQualifyingPriorMove()
    {
        // Nothing precedes the base with a genuine directional move (a flat doji, not a real
        // approach leg), so the standard "strong move in, base, strong move out" pattern is
        // incomplete even though the base+departure alone would otherwise qualify.
        var candles = new List<Candle>
        {
            Doji(0, 99m),
            Doji(1, 100m),
            Marubozu(2, 100m, 101m),
            Marubozu(3, 101m, 102m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { RequireOriginationMove = true })));
        Assert.That(Replay(analyzer, candles)[^1].ActiveZones, Is.Empty);
    }

    [Test]
    public void Update_RequireOriginationMove_AllowsBaseWithQualifyingPriorMove()
    {
        // Bearish approach into the base, then bullish departure out - the full
        // Drop-Base-Rally pattern the standard demand-zone definition describes.
        var candles = new List<Candle>
        {
            Marubozu(0, 101m, 100m),
            Doji(1, 100m),
            Marubozu(2, 100m, 101m),
            Marubozu(3, 101m, 102m)
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { RequireOriginationMove = true })));
        SupplyDemandZone zone = Replay(analyzer, candles)[^1].ActiveZones.Single();

        Assert.That(zone.Type, Is.EqualTo(SupplyDemandZoneType.Demand));
    }

    // ----- Lifecycle: fresh -> approached -> tested -> partially mitigated -> mitigated/invalidated/expired

    [Test]
    public void Lifecycle_ApproachTouchAndPartiallyMitigate_TransitionsInOrder()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m) // zone confirms: proximal=100.05, distal=99.95
        };
        List<SupplyDemandAnalysisSnapshot> snapshots = Replay(analyzer, candles);
        Guid zoneId = snapshots[^1].ActiveZones.Single().ZoneId;

        var history = new List<Candle>(candles);

        // Approach: close within 1 zone-height (0.10) of proximal, without touching.
        Candle approach = C(3, 102.0m, 102.0m, 100.10m, 100.10m);
        history.Add(approach);
        SupplyDemandAnalysisSnapshot afterApproach = analyzer.Update(approach, history, [], 1.0m, 3);
        Assert.That(afterApproach.ActiveZones.Single(z => z.ZoneId == zoneId).State, Is.EqualTo(SupplyDemandZoneState.Approached));

        // Touch: shallow penetration (0.02 of a 0.10 zone => ratio 0.2, below the 0.5 partial-mitigation threshold).
        Candle touch = C(4, 100.10m, 100.10m, 100.03m, 100.03m);
        history.Add(touch);
        SupplyDemandAnalysisSnapshot afterTouch = analyzer.Update(touch, history, [], 1.0m, 4);
        SupplyDemandZone touchedZone = afterTouch.ActiveZones.Single(z => z.ZoneId == zoneId);
        Assert.Multiple(() =>
        {
            Assert.That(touchedZone.State, Is.EqualTo(SupplyDemandZoneState.Tested));
            Assert.That(touchedZone.TouchCount, Is.EqualTo(1));
        });

        // Deeper penetration (0.07 of 0.10 => ratio 0.7) crosses the 0.5 partial-mitigation threshold.
        Candle deepen = C(5, 100.03m, 100.03m, 99.98m, 99.98m);
        history.Add(deepen);
        SupplyDemandAnalysisSnapshot afterDeepen = analyzer.Update(deepen, history, [], 1.0m, 5);
        SupplyDemandZone deepenedZone = afterDeepen.ActiveZones.Single(z => z.ZoneId == zoneId);
        Assert.That(deepenedZone.State, Is.EqualTo(SupplyDemandZoneState.PartiallyMitigated));
    }

    [Test]
    public void Lifecycle_DwellEmitsOneTouch_ButExitAndRevisitEmitsAnother()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(
            builder => builder.With(profile => profile with { MinimumDistinctTouchBars = 2 })));
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };
        List<SupplyDemandAnalysisSnapshot> formed = Replay(analyzer, candles);
        Guid zoneId = formed[^1].ActiveZones.Single().ZoneId;
        var history = new List<Candle>(candles);

        SupplyDemandAnalysisSnapshot Update(Candle candle, long sequence)
        {
            history.Add(candle);
            return analyzer.Update(candle, history, [], 1.0m, sequence);
        }

        Update(C(3, 100.10m, 100.10m, 100.03m, 100.04m), 3);
        SupplyDemandAnalysisSnapshot dwell =
            Update(C(4, 100.04m, 100.05m, 100.02m, 100.03m), 4);
        Update(C(5, 100.40m, 100.60m, 100.30m, 100.50m), 5);
        SupplyDemandAnalysisSnapshot revisit =
            Update(C(6, 100.10m, 100.10m, 100.03m, 100.04m), 6);

        Assert.Multiple(() =>
        {
            Assert.That(dwell.RecentEvents.Count(item =>
                item.ZoneId == zoneId && item.EventType == SupplyDemandZoneEventType.Touched), Is.EqualTo(1));
            Assert.That(revisit.RecentEvents.Count(item =>
                item.ZoneId == zoneId && item.EventType == SupplyDemandZoneEventType.Touched), Is.EqualTo(2));
            Assert.That(revisit.ActiveZones.Single(item => item.ZoneId == zoneId).DistinctTouchCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Lifecycle_WickReachesDistalWithoutClosingBeyond_BecomesMitigatedNotInvalidated()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m) // proximal=100.05, distal=99.95
        };
        var history = new List<Candle>(candles);
        long sequence = 0;
        foreach (Candle candle in candles)
        {
            analyzer.Update(candle, history, [], 1.0m, sequence++);
        }

        // Wick dips to 99.94 (through distal 99.95) but closes back at 99.96 (still above distal).
        Candle wickThrough = C(3, 99.99m, 99.99m, 99.94m, 99.96m);
        history.Add(wickThrough);
        SupplyDemandAnalysisSnapshot result = analyzer.Update(wickThrough, history, [], 1.0m, sequence);

        SupplyDemandZone? zone = result.ActiveZones.FirstOrDefault();
        Assert.That(zone, Is.Not.Null);
        Assert.That(zone!.State, Is.EqualTo(SupplyDemandZoneState.Mitigated));
    }

    [Test]
    public void Lifecycle_CloseBeyondDistal_Invalidates()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m) // proximal=100.05, distal=99.95
        };
        var history = new List<Candle>(candles);
        long sequence = 0;
        foreach (Candle candle in candles)
        {
            analyzer.Update(candle, history, [], 1.0m, sequence++);
        }

        Candle closeBeyond = C(3, 100.00m, 100.00m, 99.85m, 99.85m); // close 99.85 < distal 99.95
        history.Add(closeBeyond);
        SupplyDemandAnalysisSnapshot result = analyzer.Update(closeBeyond, history, [], 1.0m, sequence);

        Assert.That(result.ActiveZones, Is.Empty, "an invalidated zone must drop out of ActiveZones");
        SupplyDemandZoneEvent invalidatedEvent = result.RecentEvents.Single(e => e.EventType == SupplyDemandZoneEventType.Invalidated);
        Assert.That(invalidatedEvent.StateAfter, Is.EqualTo(SupplyDemandZoneState.Invalidated));
    }

    [Test]
    public void Lifecycle_ExceedsMaximumAge_Expires()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile(b => b.With(p => p with { MaximumZoneAgeBars = 2 })));
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m) // confirmed at sequence 2
        };
        var history = new List<Candle>(candles);
        long sequence = 0;
        foreach (Candle candle in candles)
        {
            analyzer.Update(candle, history, [], 1.0m, sequence++);
        }

        // Two neutral candles, far from the zone, that don't touch/approach/invalidate it.
        for (int i = 0; i < 2; i++)
        {
            Candle neutral = C(3 + i, 105m, 105.1m, 104.9m, 105m);
            history.Add(neutral);
            var result = analyzer.Update(neutral, history, [], 1.0m, sequence++);
            if (i == 1)
            {
                Assert.That(result.ActiveZones, Is.Empty);
                Assert.That(result.RecentEvents.Any(e => e.EventType == SupplyDemandZoneEventType.Expired), Is.True);
            }
        }
    }

    // ----- Merge / nesting --------------------------------------------------

    [Test]
    public void Merge_OverlappingSameSideZonesFromSeparateDepartures_MergeIntoOne()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        var history = new List<Candle>();
        long sequence = 0;

        void Feed(Candle candle) { history.Add(candle); analyzer.Update(candle, history, [], 1.0m, sequence++); }

        // First demand zone: base+departure around price 100.
        Feed(Doji(0, 100m));
        Feed(Marubozu(1, 100m, 101m));
        Feed(Marubozu(2, 101m, 102m)); // zone #1 confirmed (proximal 100.05 / distal 99.95)

        // Retrace back down and away, then form a second, separately-timed demand zone at
        // (almost) the same price band - no time overlap with zone #1's [BaseStartedAt, ConfirmedAt].
        Feed(Marubozu(3, 102m, 100m));
        Feed(Doji(4, 100m));
        Feed(Marubozu(5, 100m, 101m));
        SupplyDemandAnalysisSnapshot final = default!;
        Candle last = Marubozu(6, 101m, 102m);
        history.Add(last);
        final = analyzer.Update(last, history, [], 1.0m, sequence);

        Assert.That(final.ActiveZones, Has.Count.EqualTo(1), "two heavily overlapping same-side zones must merge");
        SupplyDemandZoneEvent mergedEvent = final.RecentEvents.Single(e => e.EventType == SupplyDemandZoneEventType.Merged);
        Assert.That(mergedEvent.RelatedZoneId, Is.EqualTo(final.ActiveZones[0].ZoneId));
        Assert.That(final.ActiveZones[0].SourceZoneIds, Has.Count.EqualTo(1));
    }

    [Test]
    public void Merge_OpposingZones_NeverMerge()
    {
        // A completed close beyond distal correctly invalidates a zone (real domain behavior,
        // covered by Lifecycle_CloseBeyondDistal_Invalidates) - so to keep the Demand zone alive
        // (non-terminal) while price travels all the way through it to form an overlapping Supply
        // zone below, disable invalidation for this test via a threshold the clamped [0,1]
        // penetration ratio can never reach. This isolates exactly one property: merge must never
        // cross zone Type, independent of how the two zones' lifecycles otherwise evolve.
        var profile = EnabledProfile(b => b.With(p => p with
        {
            InvalidationMode = ZoneInvalidationMode.PenetrationThreshold,
            InvalidationPenetrationRatio = 2.0m
        }));
        var analyzer = new SupplyDemandAnalyzer(profile);
        var history = new List<Candle>();
        long sequence = 0;
        void Feed(Candle candle) { history.Add(candle); analyzer.Update(candle, history, [], 1.0m, sequence++); }

        // Demand zone around 100.
        Feed(Doji(0, 100m));
        Feed(Marubozu(1, 100m, 101m));
        Feed(Marubozu(2, 101m, 102m));

        // Supply zone that departs back down through almost the same band.
        Feed(Marubozu(3, 102m, 100m));
        Feed(Doji(4, 100m));
        Feed(Marubozu(5, 100m, 99m));
        Candle last = Marubozu(6, 99m, 98m);
        history.Add(last);
        SupplyDemandAnalysisSnapshot final = analyzer.Update(last, history, [], 1.0m, sequence);

        Assert.That(final.ActiveZones.Select(z => z.Type), Is.EquivalentTo(new[] { SupplyDemandZoneType.Demand, SupplyDemandZoneType.Supply }));
    }

    [Test]
    public void Detection_ContinuingMoveAfterZoneForms_DoesNotSpawnOverlappingDuplicate()
    {
        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        var history = new List<Candle>();
        long sequence = 0;
        void Feed(Candle candle) { history.Add(candle); analyzer.Update(candle, history, [], 1.0m, sequence++); }

        Feed(Doji(0, 100m));
        Feed(Marubozu(1, 100m, 101m));
        Feed(Marubozu(2, 101m, 102m)); // zone #1 confirmed

        SupplyDemandAnalysisSnapshot latest = default!;
        for (int i = 3; i < 6; i++)
        {
            Candle candle = Marubozu(i, 100m + (i - 2), 100m + (i - 1));
            history.Add(candle);
            latest = analyzer.Update(candle, history, [], 1.0m, sequence++);
        }

        Assert.That(latest.ActiveZones, Has.Count.EqualTo(1), "a continuing move overlapping the same base must not spawn a duplicate zone");
    }

    // ----- Determinism / replay parity / profile separation ----------------

    [Test]
    public void Update_SameCandles_TwoIndependentAnalyzers_ProduceIdenticalZoneIds()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };

        var first = new SupplyDemandAnalyzer(EnabledProfile());
        var second = new SupplyDemandAnalyzer(EnabledProfile());

        SupplyDemandZone zoneA = Replay(first, candles)[^1].ActiveZones.Single();
        SupplyDemandZone zoneB = Replay(second, candles)[^1].ActiveZones.Single();

        Assert.That(zoneA.ZoneId, Is.EqualTo(zoneB.ZoneId));
        Assert.That(zoneA, Is.EqualTo(zoneB));
    }

    [Test]
    public void Update_DifferingProfiles_ProduceDifferentProfileHashOnZones()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };

        var profileA = EnabledProfile();
        var profileB = EnabledProfile(b => b.With(p => p with { BoundaryMode = ZoneBoundaryMode.BodyToExtreme }));

        SupplyDemandZone zoneA = Replay(new SupplyDemandAnalyzer(profileA), candles)[^1].ActiveZones.Single();
        SupplyDemandZone zoneB = Replay(new SupplyDemandAnalyzer(profileB), candles)[^1].ActiveZones.Single();

        Assert.That(zoneA.ProfileHash, Is.Not.EqualTo(zoneB.ProfileHash));
        Assert.That(zoneA.ProfileHash, Is.EqualTo(SupplyDemandCalculationProfileHasher.ComputeHash(profileA)));
    }

    // ----- Pattern classification -------------------------------------------

    [Test]
    public void Update_BullishDepartureWithPriorSwingHigh_ClassifiesAsDropBaseRally()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };
        SwingPoint priorHigh = new()
        {
            PivotTime = Start - TimeSpan.FromMinutes(30),
            ConfirmedAt = Start - TimeSpan.FromMinutes(15),
            Price = 105m,
            Type = SwingType.High,
            Strength = 2
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        SupplyDemandZone zone = Replay(analyzer, candles, [priorHigh])[^1].ActiveZones.Single();

        Assert.That(zone.Pattern, Is.EqualTo(SupplyDemandPattern.DropBaseRally));
    }

    [Test]
    public void Update_BullishDepartureWithPriorSwingLow_ClassifiesAsRallyBaseRally()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };
        SwingPoint priorLow = new()
        {
            PivotTime = Start - TimeSpan.FromMinutes(30),
            ConfirmedAt = Start - TimeSpan.FromMinutes(15),
            Price = 95m,
            Type = SwingType.Low,
            Strength = 2
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        SupplyDemandZone zone = Replay(analyzer, candles, [priorLow])[^1].ActiveZones.Single();

        Assert.That(zone.Pattern, Is.EqualTo(SupplyDemandPattern.RallyBaseRally));
    }

    [Test]
    public void Update_BearishDepartureWithPriorSwingLow_ClassifiesAsRallyBaseDrop()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 99m),
            Marubozu(2, 99m, 98m)
        };
        SwingPoint priorLow = new()
        {
            PivotTime = Start - TimeSpan.FromMinutes(30),
            ConfirmedAt = Start - TimeSpan.FromMinutes(15),
            Price = 95m,
            Type = SwingType.Low,
            Strength = 2
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        SupplyDemandZone zone = Replay(analyzer, candles, [priorLow])[^1].ActiveZones.Single();

        Assert.That(zone.Pattern, Is.EqualTo(SupplyDemandPattern.RallyBaseDrop));
    }

    [Test]
    public void Update_BearishDepartureWithPriorSwingHigh_ClassifiesAsDropBaseDrop()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 99m),
            Marubozu(2, 99m, 98m)
        };
        SwingPoint priorHigh = new()
        {
            PivotTime = Start - TimeSpan.FromMinutes(30),
            ConfirmedAt = Start - TimeSpan.FromMinutes(15),
            Price = 105m,
            Type = SwingType.High,
            Strength = 2
        };

        var analyzer = new SupplyDemandAnalyzer(EnabledProfile());
        SupplyDemandZone zone = Replay(analyzer, candles, [priorHigh])[^1].ActiveZones.Single();

        Assert.That(zone.Pattern, Is.EqualTo(SupplyDemandPattern.DropBaseDrop));
    }

    // ----- Disabled profile --------------------------------------------------

    [Test]
    public void Update_DisabledProfile_NeverFormsZones()
    {
        var candles = new List<Candle>
        {
            Doji(0, 100m),
            Marubozu(1, 100m, 101m),
            Marubozu(2, 101m, 102m)
        };

        var analyzer = new SupplyDemandAnalyzer(new SupplyDemandCalculationProfile { Enabled = false });
        SupplyDemandAnalysisSnapshot last = Replay(analyzer, candles)[^1];

        Assert.Multiple(() =>
        {
            Assert.That(last.IsEnabled, Is.False);
            Assert.That(last.ActiveZones, Is.Empty);
        });
    }

    /// <summary>
    /// Regression guard for the terminal-zone trimming added to bound <c>_zones</c> to roughly
    /// MaximumActiveZones + MaximumRetainedEvents (previously every zone ever formed in a run was
    /// retained forever, making every candle's update scan/re-sort the whole history - O(total
    /// zones) per candle, roughly quadratic total cost over a long stream). Runs long enough
    /// (10,000 candles, far more than the default 250-entry retention window) to force many
    /// trims, then proves the append-only lifecycle guarantee still holds: no zone ID is ever
    /// "Formed" twice, which is exactly what a resurrection bug (a trimmed zone's deterministic
    /// ID being re-detected as brand new) would produce. Also confirms the analyzer's own bounded
    /// output actually stays bounded.
    /// </summary>
    [Test]
    public void Update_LongRun_NeverResurrectsATrimmedZoneAndKeepsOutputBounded()
    {
        var candles = BuildLongOscillatingStream(count: 10_000);
        var profile = EnabledProfile();
        var analyzer = new SupplyDemandAnalyzer(profile);
        List<SupplyDemandAnalysisSnapshot> snapshots = Replay(analyzer, candles, atr: 1m);

        var formedCounts = new Dictionary<Guid, int>();
        var seenEventIds = new HashSet<Guid>();
        foreach (SupplyDemandAnalysisSnapshot snapshot in snapshots)
        {
            foreach (SupplyDemandZoneEvent zoneEvent in snapshot.RecentEvents)
            {
                if (!seenEventIds.Add(zoneEvent.EventId))
                {
                    continue;
                }

                if (zoneEvent.EventType == SupplyDemandZoneEventType.Formed)
                {
                    formedCounts[zoneEvent.ZoneId] = formedCounts.GetValueOrDefault(zoneEvent.ZoneId) + 1;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(formedCounts, Is.Not.Empty, "the synthetic stream should have formed multiple zones");
            Assert.That(formedCounts.Values, Is.All.EqualTo(1),
                "a zone ID was 'Formed' more than once - a trimmed terminal zone was resurrected");
            SupplyDemandAnalysisSnapshot last = snapshots[^1];
            Assert.That(last.Zones.Count, Is.LessThanOrEqualTo(profile.MaximumActiveZones + profile.MaximumRetainedEvents));
            Assert.That(last.ActiveZones.Count, Is.LessThanOrEqualTo(profile.MaximumActiveZones));
        });
    }

    /// <summary>Long oscillating series with a slow drift so base/departure geometry keeps
    /// producing new, non-overlapping zone candidates rather than repeatedly re-forming the
    /// same one (mirrors the shape used in the manual candles/second benchmark that originally
    /// surfaced the unbounded-growth cost).</summary>
    private static Candle[] BuildLongOscillatingStream(int count)
    {
        var candles = new Candle[count];
        decimal price = 100m;
        for (int i = 0; i < count; i++)
        {
            decimal wave = (decimal)Math.Sin(i / 17.0) * 1.5m;
            decimal drift = i * 0.01m;
            decimal open = price;
            decimal close = price + wave + drift + (i % 23 == 0 ? 2m : 0m) - (i % 29 == 0 ? 2m : 0m);
            decimal high = Math.Max(open, close) + 0.4m;
            decimal low = Math.Min(open, close) - 0.4m;
            DateTimeOffset openTime = Start + TimeSpan.FromMinutes(15 * i);
            candles[i] = new Candle
            {
                Instrument = Instrument,
                Interval = Interval,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(15),
                Prices = new Ohlc(open, high, low, close),
                Volume = new MarketVolume(100m, VolumeKind.Unknown),
                IsComplete = true
            };
            price = close;
        }

        return candles;
    }
}
