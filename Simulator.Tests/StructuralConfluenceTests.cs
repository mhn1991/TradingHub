using Brokers.Models;
using ChartAnnotator.Confluence;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralConfluenceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-05T12:00:00Z");

    [TestCase(LiquiditySide.SellSide, SupplyDemandZoneType.Demand, ConfluenceDirection.Bullish)]
    [TestCase(LiquiditySide.BuySide, SupplyDemandZoneType.Supply, ConfluenceDirection.Bearish)]
    public void MatchingSweepAndZone_CreateExplicitDirectionalRelationship(
        LiquiditySide side,
        SupplyDemandZoneType zoneType,
        ConfluenceDirection expectedDirection)
    {
        LiquidityPool pool = Pool(side);
        LiquiditySweepEvent sweep = Sweep(pool.PoolId);
        SupplyDemandZone zone = Zone(zoneType);
        var analyzer = new SupplyDemandLiquidityConfluenceAnalyzer(
            new SupplyDemandLiquidityConfluenceOptions { Enabled = true, MaximumDistanceAtr = 0.5m });

        SupplyDemandLiquidityConfluenceSnapshot result = analyzer.Update(
            SupplyDemand([zone]),
            Liquidity([pool], [sweep]),
            1m,
            At);

        SupplyDemandLiquidityConfluence relationship = result.Relationships.Single();
        Assert.Multiple(() =>
        {
            Assert.That(relationship.ZoneId, Is.EqualTo(zone.ZoneId));
            Assert.That(relationship.PoolId, Is.EqualTo(pool.PoolId));
            Assert.That(relationship.SweepId, Is.EqualTo(sweep.SweepId));
            Assert.That(relationship.Direction, Is.EqualTo(expectedDirection));
            Assert.That(relationship.AvailableAt, Is.EqualTo(At));
        });
    }

    [Test]
    public void ZoneOnlyOrSweepOnly_CreatesNoRelationship()
    {
        var analyzer = new SupplyDemandLiquidityConfluenceAnalyzer(
            new SupplyDemandLiquidityConfluenceOptions { Enabled = true });
        SupplyDemandLiquidityConfluenceSnapshot zoneOnly = analyzer.Update(
            SupplyDemand([Zone(SupplyDemandZoneType.Demand)]),
            Liquidity([], []),
            1m,
            At);
        SupplyDemandLiquidityConfluenceSnapshot sweepOnly = analyzer.Update(
            SupplyDemand([]),
            Liquidity([Pool(LiquiditySide.SellSide)], []),
            1m,
            At.AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(zoneOnly.Relationships, Is.Empty);
            Assert.That(sweepOnly.Relationships, Is.Empty);
        });
    }

    [Test]
    public void FutureDatedSweep_IsNotVisible()
    {
        LiquidityPool pool = Pool(LiquiditySide.SellSide);
        var analyzer = new SupplyDemandLiquidityConfluenceAnalyzer(
            new SupplyDemandLiquidityConfluenceOptions { Enabled = true });

        SupplyDemandLiquidityConfluenceSnapshot result = analyzer.Update(
            SupplyDemand([Zone(SupplyDemandZoneType.Demand)]),
            Liquidity([pool], [Sweep(pool.PoolId) with { AvailableAt = At.AddMinutes(1) }]),
            1m,
            At);

        Assert.That(result.Relationships, Is.Empty);
    }

    private static SupplyDemandZone Zone(SupplyDemandZoneType type) => new()
    {
        ZoneId = Guid.NewGuid(),
        Instrument = new InstrumentKey("EURUSD"),
        Interval = BarInterval.Minutes(15),
        Type = type,
        Pattern = type == SupplyDemandZoneType.Demand
            ? SupplyDemandPattern.DropBaseRally
            : SupplyDemandPattern.RallyBaseDrop,
        ProximalPrice = type == SupplyDemandZoneType.Demand ? 99.9m : 100.1m,
        DistalPrice = type == SupplyDemandZoneType.Demand ? 99.5m : 100.5m,
        BaseStartedAt = At.AddHours(-1),
        BaseEndedAt = At.AddMinutes(-45),
        DepartureStartedAt = At.AddMinutes(-30),
        ConfirmedAt = At,
        AvailableAt = At,
        State = SupplyDemandZoneState.ConfirmedFresh,
        BaseCandleCount = 2,
        TouchCount = 0,
        DepartureAtr = 2m,
        DepartureEfficiency = 0.8m,
        BaseCompactness = 0.8m,
        ImbalanceRatio = 0.7m,
        PenetrationRatio = 0m,
        FreshnessScore = 1m,
        QualityScore = 0.8m,
        BrokeStructure = true,
        HasFairValueGap = false,
        BoundaryMode = ZoneBoundaryMode.FullWickRange,
        SourceZoneIds = [],
        SnapshotVersion = 1,
        ProfileHash = "sd"
    };

    private static LiquidityPool Pool(LiquiditySide side) => new()
    {
        PoolId = Guid.NewGuid(),
        Instrument = new InstrumentKey("EURUSD"),
        Interval = BarInterval.Minutes(15),
        Side = side,
        Type = side == LiquiditySide.SellSide ? LiquidityPoolType.EqualLows : LiquidityPoolType.EqualHighs,
        LowerPrice = 99.8m,
        UpperPrice = 100.2m,
        ReferencePrice = 100m,
        OriginatedAt = At.AddHours(-2),
        ConfirmedAt = At.AddHours(-1),
        AvailableAt = At.AddHours(-1),
        State = LiquidityPoolState.Swept,
        SourcePointCount = 2,
        TouchCount = 0,
        EqualnessScore = 0.9m,
        VisibilityScore = 0.7m,
        CompressionScore = 0.6m,
        ProminenceScore = 0.7m,
        FreshnessScore = 1m,
        QualityScore = 0.8m,
        SourcePoolIds = [],
        SnapshotVersion = 1,
        ProfileHash = "liq"
    };

    private static LiquiditySweepEvent Sweep(Guid poolId) => new()
    {
        SweepId = Guid.NewGuid(),
        PoolId = poolId,
        SweepStartedAt = At.AddMinutes(-15),
        ConfirmedAt = At,
        AvailableAt = At,
        ExtremePrice = 99.5m,
        PenetrationAtr = 0.3m,
        ClosedBackInside = true,
        ClosedBackBeyondOriginSide = true,
        DisplacementConfirmed = true,
        StructureShiftConfirmed = true,
        RejectionStrength = 0.8m,
        QualityScore = 0.8m,
        SnapshotVersion = 1
    };

    private static SupplyDemandAnalysisSnapshot SupplyDemand(IReadOnlyList<SupplyDemandZone> zones) => new()
    {
        IsEnabled = true,
        ProfileHash = "sd",
        SnapshotVersion = 1,
        AvailableAt = At,
        Zones = zones,
        ActiveZones = zones,
        RecentEvents = [],
        Quality = new SupplyDemandAnalysisQuality
        {
            AtrReady = true,
            ActiveZoneCount = zones.Count,
            SuppressedCandidateCount = 0,
            LastEvaluatedAt = At
        }
    };

    private static LiquidityAnalysisSnapshot Liquidity(
        IReadOnlyList<LiquidityPool> pools,
        IReadOnlyList<LiquiditySweepEvent> sweeps) => new()
    {
        IsEnabled = true,
        ProfileHash = "liq",
        SnapshotVersion = 1,
        AvailableAt = At,
        Pools = pools,
        ActivePools = pools.Where(item => item.State is not LiquidityPoolState.Swept).ToArray(),
        RecentEvents = [],
        RecentSweeps = sweeps,
        Quality = new LiquidityAnalysisQuality
        {
            AtrReady = true,
            ActivePoolCount = pools.Count,
            SuppressedCandidateCount = 0,
            LastEvaluatedAt = At
        }
    };
}
