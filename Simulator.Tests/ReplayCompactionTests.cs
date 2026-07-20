using System.Text.Json;
using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using Simulator.Replay;

namespace Simulator.Tests;

[TestFixture]
public sealed class ReplayCompactionTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-01T12:00:00Z");

    [Test]
    public void CompactAnalysisForReplay_BoundsStructuralCollectionsWithoutChangingOhlcv()
    {
        SupplyDemandZone zone = Zone();
        SupplyDemandZoneEvent zoneEvent = ZoneEvent(zone.ZoneId);
        LiquidityPool pool = Pool();
        LiquidityEvent liquidityEvent = PoolEvent(pool.PoolId);
        LiquiditySweepEvent sweep = Sweep(pool.PoolId);
        Candle candle = TestCandles.Create(Instrument, Now, Interval, 1.1m, 1.2m, 1.0m, 1.15m, 987m);
        DateTimeOffset availableAt = candle.CloseTime ?? Now.AddMinutes(1);
        var original = new AnalysisSnapshot
        {
            Instrument = Instrument,
            Interval = Interval,
            AvailableAt = availableAt,
            Version = 500,
            LatestCandle = candle,
            Indicators = new IndicatorSnapshot { Atr = 0.001m },
            Swings = Enumerable.Range(0, 500).Select(index => new SwingPoint
            {
                PivotTime = Now.AddMinutes(index),
                ConfirmedAt = Now.AddMinutes(index + 2),
                Price = 1m + index / 10_000m,
                Type = index % 2 == 0 ? SwingType.High : SwingType.Low,
                Strength = 2
            }).ToArray(),
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            PriceAction = new PriceActionSnapshot
            {
                Diagnostics = Enumerable.Repeat(new PriceActionDiagnostic
                {
                    Candidate = "structural-candidate-with-diagnostic-context",
                    Accepted = false,
                    ReasonCode = "InsufficientConfluence",
                    Explanation = "Representative replay diagnostic retained for recent chart context."
                }, 500).ToArray()
            },
            SupplyDemand = new SupplyDemandAnalysisSnapshot
            {
                IsEnabled = true,
                ProfileHash = "supply-profile",
                SnapshotVersion = 500,
                AvailableAt = availableAt,
                Zones = Enumerable.Repeat(zone, 500).ToArray(),
                ActiveZones = Enumerable.Repeat(zone, 300).ToArray(),
                RecentEvents = Enumerable.Repeat(zoneEvent, 400).ToArray(),
                Quality = SupplyDemandAnalysisQuality.Empty
            },
            Liquidity = new LiquidityAnalysisSnapshot
            {
                IsEnabled = true,
                ProfileHash = "liquidity-profile",
                SnapshotVersion = 500,
                AvailableAt = availableAt,
                Pools = Enumerable.Repeat(pool, 500).ToArray(),
                ActivePools = Enumerable.Repeat(pool, 300).ToArray(),
                RecentEvents = Enumerable.Repeat(liquidityEvent, 400).ToArray(),
                RecentSweeps = Enumerable.Repeat(sweep, 400).ToArray(),
                Quality = LiquidityAnalysisQuality.Empty
            },
            Confidence = new ConfidenceScore { Total = 75m, Contributions = [] }
        };

        AnalysisSnapshot compact = ChunkedReplayWriter.CompactAnalysisForReplay(original)!;
        int originalBytes = JsonSerializer.SerializeToUtf8Bytes(original).Length;
        int compactBytes = JsonSerializer.SerializeToUtf8Bytes(compact).Length;

        Assert.Multiple(() =>
        {
            Assert.That(compact.Swings, Has.Count.EqualTo(100));
            Assert.That(compact.SupplyDemand.Zones, Has.Count.EqualTo(100));
            Assert.That(compact.SupplyDemand.ActiveZones, Has.Count.EqualTo(50));
            Assert.That(compact.SupplyDemand.RecentEvents, Has.Count.EqualTo(50));
            Assert.That(compact.Liquidity.Pools, Has.Count.EqualTo(100));
            Assert.That(compact.Liquidity.ActivePools, Has.Count.EqualTo(64));
            Assert.That(compact.Liquidity.RecentEvents, Has.Count.EqualTo(64));
            Assert.That(compact.Liquidity.RecentSweeps, Has.Count.EqualTo(64));
            Assert.That(compact.PriceAction.Diagnostics, Has.Count.EqualTo(64));
            Assert.That(compact.LatestCandle.Prices, Is.EqualTo(original.LatestCandle.Prices));
            Assert.That(compact.LatestCandle.Volume, Is.EqualTo(original.LatestCandle.Volume));
            Assert.That(compactBytes, Is.LessThan(originalBytes / 2));
            Assert.That(original.SupplyDemand.Zones, Has.Count.EqualTo(500), "source snapshot must remain immutable");
        });
    }

    private static SupplyDemandZone Zone() => new()
    {
        ZoneId = Guid.NewGuid(), Instrument = Instrument, Interval = Interval,
        Type = SupplyDemandZoneType.Demand, Pattern = SupplyDemandPattern.DropBaseRally,
        ProximalPrice = 1.1m, DistalPrice = 1.09m, BaseStartedAt = Now, BaseEndedAt = Now.AddMinutes(2),
        DepartureStartedAt = Now.AddMinutes(3), ConfirmedAt = Now.AddMinutes(4), AvailableAt = Now.AddMinutes(4),
        State = SupplyDemandZoneState.ConfirmedFresh, BaseCandleCount = 3, TouchCount = 0,
        DepartureAtr = 2m, DepartureEfficiency = 0.8m, BaseCompactness = 0.9m,
        ImbalanceRatio = 1.5m, PenetrationRatio = 0m, FreshnessScore = 1m, QualityScore = 0.9m,
        BrokeStructure = true, HasFairValueGap = true, BoundaryMode = ZoneBoundaryMode.BodyToExtreme,
        SourceZoneIds = [], SnapshotVersion = 1, ProfileHash = "supply-profile"
    };

    private static SupplyDemandZoneEvent ZoneEvent(Guid zoneId) => new()
    {
        EventId = Guid.NewGuid(), ZoneId = zoneId, EventType = SupplyDemandZoneEventType.Confirmed,
        StateBefore = SupplyDemandZoneState.Forming, StateAfter = SupplyDemandZoneState.ConfirmedFresh,
        Price = 1.1m, OccurredAt = Now, AvailableAt = Now, SnapshotVersion = 1
    };

    private static LiquidityPool Pool() => new()
    {
        PoolId = Guid.NewGuid(), Instrument = Instrument, Interval = Interval,
        Side = LiquiditySide.BuySide, Type = LiquidityPoolType.EqualHighs,
        LowerPrice = 1.19m, UpperPrice = 1.2m, ReferencePrice = 1.2m,
        OriginatedAt = Now, ConfirmedAt = Now.AddMinutes(2), AvailableAt = Now.AddMinutes(2),
        State = LiquidityPoolState.Active, SourcePointCount = 2, TouchCount = 0,
        EqualnessScore = 0.9m, VisibilityScore = 0.8m, CompressionScore = 0.7m,
        ProminenceScore = 0.8m, FreshnessScore = 1m, QualityScore = 0.85m,
        SourcePoolIds = [], SnapshotVersion = 1, ProfileHash = "liquidity-profile"
    };

    private static LiquidityEvent PoolEvent(Guid poolId) => new()
    {
        EventId = Guid.NewGuid(), PoolId = poolId, EventType = LiquidityEventType.Touch,
        StateBefore = LiquidityPoolState.Active, StateAfter = LiquidityPoolState.Touched,
        Price = 1.2m, OccurredAt = Now, AvailableAt = Now, SnapshotVersion = 1
    };

    private static LiquiditySweepEvent Sweep(Guid poolId) => new()
    {
        SweepId = Guid.NewGuid(), PoolId = poolId, SweepStartedAt = Now, ConfirmedAt = Now.AddMinutes(1),
        AvailableAt = Now.AddMinutes(1), ExtremePrice = 1.21m, PenetrationAtr = 0.5m,
        ClosedBackInside = true, ClosedBackBeyondOriginSide = true, DisplacementConfirmed = true,
        StructureShiftConfirmed = true, RejectionStrength = 0.8m, QualityScore = 0.9m, SnapshotVersion = 1
    };
}
