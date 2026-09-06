using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoConfirmedTrendStructureTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestCase(false)]
    [TestCase(true)]
    public void MissingStructureCannotEstablishATrend(bool bullish)
    {
        var detector = Detector();
        Apply(detector, 0, bullish, eliminations: 2);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.Unknown));
        Assert.That(detector.Snapshot.Reason, Does.Contain("waiting for two confirmed"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StrictConfirmedStructureAndAccomplishmentAreBothRequired(bool bullish)
    {
        var detector = Detector();
        Apply(detector, 0, bullish, peak: 112m, valley: 90m);
        Apply(detector, 1, bullish, peak: 108m, valley: 86m);
        Assert.That(detector.Snapshot.Trend, Is.Not.EqualTo(Direction(bullish)));
        Apply(detector, 2, bullish, eliminations: 2);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(Direction(bullish)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EqualPeaksOrValleysAreNotDirectionalConfirmation(bool bullish)
    {
        foreach (bool flatPeak in new[] { false, true })
        {
            var detector = Detector();
            Apply(detector, 0, bullish, peak: 112m, valley: 90m);
            Apply(detector, 1, bullish, peak: flatPeak ? 112m : 108m, valley: flatPeak ? 86m : 90m);
            Apply(detector, 2, bullish, eliminations: 2);
            Assert.That(detector.Snapshot.Trend, Is.Not.EqualTo(Direction(bullish)));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReversalWaitsForBothSwingSeriesRatherThanFlippingOnElimination(bool bullish)
    {
        var detector = Detector();
        Apply(detector, 0, bullish, peak: 112m, valley: 90m);
        Apply(detector, 1, bullish, peak: 108m, valley: 86m);
        Apply(detector, 2, bullish, eliminations: 2);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(Direction(bullish)));

        Apply(detector, 3, bullish, eliminations: 2, oppositeEvidence: true);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
        // Only the peak series has reversed. The valley is not known until a later update.
        Apply(detector, 4, bullish, peak: 114m);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
        Apply(detector, 5, bullish, valley: 92m);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(Direction(!bullish)));
    }

    [Test]
    public void ContinuationZonesCannotSupplyMissingConfirmation()
    {
        var detector = Detector();
        Apply(detector, 0, false, peak: 112m, valley: 90m, continuation: true);
        Apply(detector, 1, false, peak: 108m, valley: 86m, continuation: true);
        Apply(detector, 2, false, eliminations: 2);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.Unknown));
    }

    [Test]
    public void OptInIsWiredAndBaselineRemainsUnchanged()
    {
        Assert.That(new AlfonsoTrendOptions().RequireConfirmedTrendStructure, Is.False);
        var detector = new AlfonsoTrendDetector();
        Apply(detector, 0, false, eliminations: 2);
        Assert.That(detector.Snapshot.Trend, Is.EqualTo(AlfonsoTrend.Downtrend));
        var definition = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAU/USD"), From = Start, To = Start.AddDays(1),
            AlfonsoRequireConfirmedTrendStructure = true
        }.ResolveAgentDefinition("alfonso");
        Assert.That(definition.Alfonso!.Trend.RequireConfirmedTrendStructure, Is.True);
    }

    private static AlfonsoTrendDetector Detector() => new(new AlfonsoTrendOptions
    {
        RequireConfirmedTrendStructure = true
    });

    private static AlfonsoTrend Direction(bool bullish) => bullish ? AlfonsoTrend.Uptrend : AlfonsoTrend.Downtrend;

    private static void Apply(AlfonsoTrendDetector detector, int index, bool mirror,
        decimal? peak = null, decimal? valley = null, int eliminations = 0,
        bool oppositeEvidence = false, bool continuation = false)
    {
        DateTimeOffset at = Start.AddHours(index * 4);
        var created = new List<Imbalance>();
        if (peak is decimal high)
            created.Add(Zone(at, mirror ? ImbalanceKind.Demand : ImbalanceKind.Supply,
                mirror ? 200m - high : high, continuation));
        if (valley is decimal low)
            created.Add(Zone(at, mirror ? ImbalanceKind.Supply : ImbalanceKind.Demand,
                mirror ? 200m - low : low, continuation));
        ImbalanceKind eliminatedKind = mirror ^ oppositeEvidence ? ImbalanceKind.Supply : ImbalanceKind.Demand;
        detector.Apply(new AlfonsoBar(at, 100m, 120m, 80m, 100m), new ImbalanceDetectorUpdate
        {
            Created = created,
            Eliminated = Enumerable.Repeat(Zone(at, eliminatedKind, 100m, false), eliminations).ToArray()
        });
    }

    private static Imbalance Zone(DateTimeOffset at, ImbalanceKind kind, decimal distal, bool continuation) => new()
    {
        Interval = TimeSpan.FromHours(4), Kind = kind, Distal = distal,
        Proximal = distal + (kind == ImbalanceKind.Demand ? 2m : -2m),
        BaseStart = at, BaseEnd = at, DistalAt = at, ConfirmedAt = at, BaseCandleCount = 1,
        Strength = ImpulseStrength.Strong, Accomplished = Accomplishment.OpposingImbalanceEliminated,
        ImpulseToBaseRatio = 3m, ImpulseDisplacement = 6m, ImpulseBarsTracked = 2,
        IsContinuationPattern = continuation, MeetsTradeabilityCriteria = true
    };
}
