using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using TradeManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralTradeManagementTests
{
    private static readonly InstrumentKey Instrument = new("EURUSD");
    private static readonly BarInterval Interval = BarInterval.Minutes(15);
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-05T12:00:00Z");
    private static readonly Guid ZoneId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PoolId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public void EntryPinnedZoneInvalidation_ExitsOnlyWhenBothEntryAndManagerOptedIn()
    {
        var manager = Manager(supplyDemand: true);
        AnalysisSnapshot analysis = Analysis() with { SupplyDemand = InvalidatedZoneSnapshot() };

        TradeManagementRecommendation optedOut = manager.Evaluate(
            Trade() with { EntrySupplyDemandZoneId = ZoneId },
            analysis,
            TradeManagementEvaluationScope.Thesis);
        TradeManagementRecommendation optedIn = manager.Evaluate(
            Trade() with
            {
                EntrySupplyDemandZoneId = ZoneId,
                EntrySupplyDemandManagementEnabled = true
            },
            analysis,
            TradeManagementEvaluationScope.Thesis);

        Assert.Multiple(() =>
        {
            Assert.That(optedOut.Action, Is.EqualTo(TradeManagementAction.Hold));
            Assert.That(optedIn.Action, Is.EqualTo(TradeManagementAction.Exit));
            Assert.That(optedIn.ExitReason, Is.EqualTo(TradeManagementExitReason.EntrySupplyDemandZoneInvalidated));
        });
    }

    [Test]
    public void EntryPinnedLiquidityAcceptedBreak_ExitsOnlyForAdverseSide()
    {
        var manager = Manager(liquidity: true);
        ManagedTradeState trade = Trade() with
        {
            TargetLiquidityPoolId = PoolId,
            EntryLiquidityManagementEnabled = true
        };

        TradeManagementRecommendation adverse = manager.Evaluate(
            trade,
            Analysis() with { Liquidity = AcceptedBreakSnapshot(LiquiditySide.SellSide) },
            TradeManagementEvaluationScope.Thesis);
        TradeManagementRecommendation favourable = manager.Evaluate(
            trade,
            Analysis() with { Liquidity = AcceptedBreakSnapshot(LiquiditySide.BuySide) },
            TradeManagementEvaluationScope.Thesis);

        Assert.Multiple(() =>
        {
            Assert.That(adverse.ExitReason, Is.EqualTo(TradeManagementExitReason.TargetLiquidityAcceptedBreak));
            Assert.That(favourable.Action, Is.EqualTo(TradeManagementAction.Hold));
        });
    }

    [Test]
    public void PolicyRevisionMismatch_CannotRetrofitManagement()
    {
        var manager = Manager(supplyDemand: true);
        TradeManagementRecommendation result = manager.Evaluate(
            Trade() with
            {
                EntrySupplyDemandZoneId = ZoneId,
                EntrySupplyDemandManagementEnabled = true,
                StructuralManagementPolicyRevision = "older-policy"
            },
            Analysis() with { SupplyDemand = InvalidatedZoneSnapshot() },
            TradeManagementEvaluationScope.Thesis);

        Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Hold));
    }

    [Test]
    public void EquityProtection_RemainsAuthoritativeOverStructuralExit()
    {
        var manager = Manager(supplyDemand: true);
        TradeManagementRecommendation result = manager.Evaluate(
            Trade() with
            {
                EntrySupplyDemandZoneId = ZoneId,
                EntrySupplyDemandManagementEnabled = true
            },
            Analysis() with { SupplyDemand = InvalidatedZoneSnapshot() },
            TradeManagementEvaluationScope.Thesis,
            new EquityProtectionDirective
            {
                TierId = "account-safety",
                Action = EquityProtectionPositionAction.FlattenAllPositions
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(TradeManagementAction.Exit));
            Assert.That(result.ExitReason, Is.EqualTo(TradeManagementExitReason.EquityProtectionBreach));
        });
    }

    private static StructureBasedTradeManager Manager(bool supplyDemand = false, bool liquidity = false) =>
        new(new PositionManagementOptions
        {
            Mode = TrailingStopMode.Disabled,
            SupplyDemandManagementEnabled = supplyDemand,
            LiquidityManagementEnabled = liquidity,
            StructuralManagementPolicyRevision = "structural-management-v1"
        });

    private static ManagedTradeState Trade() => new()
    {
        Instrument = Instrument,
        Side = OrderSide.Buy,
        EntryPrice = 100m,
        InitialStopPrice = 99m,
        CurrentStopPrice = 99m,
        CurrentPrice = 100m,
        EvaluatedAt = At,
        MinimumPriceIncrement = 0.0001m,
        StructuralManagementPolicyRevision = "structural-management-v1"
    };

    private static AnalysisSnapshot Analysis() => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = At,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, At.AddMinutes(-15), Interval, 100m, 101m, 99m, 100m),
        Indicators = new IndicatorSnapshot { Atr = 1m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        MarketStructure = MarketStructureSnapshot.Empty,
        Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
    };

    private static SupplyDemandAnalysisSnapshot InvalidatedZoneSnapshot() => new()
    {
        IsEnabled = true,
        ProfileHash = "sd",
        SnapshotVersion = 1,
        AvailableAt = At,
        Zones = [],
        ActiveZones = [],
        RecentEvents =
        [
            new SupplyDemandZoneEvent
            {
                EventId = Guid.NewGuid(),
                ZoneId = ZoneId,
                EventType = SupplyDemandZoneEventType.Invalidated,
                StateBefore = SupplyDemandZoneState.Tested,
                StateAfter = SupplyDemandZoneState.Invalidated,
                Price = 99m,
                OccurredAt = At,
                AvailableAt = At,
                SnapshotVersion = 1
            }
        ],
        Quality = new SupplyDemandAnalysisQuality
        {
            AtrReady = true,
            ActiveZoneCount = 0,
            SuppressedCandidateCount = 0,
            LastEvaluatedAt = At
        }
    };

    private static LiquidityAnalysisSnapshot AcceptedBreakSnapshot(LiquiditySide side)
    {
        var pool = new LiquidityPool
        {
            PoolId = PoolId,
            Instrument = Instrument,
            Interval = Interval,
            Side = side,
            Type = side == LiquiditySide.SellSide ? LiquidityPoolType.EqualLows : LiquidityPoolType.EqualHighs,
            LowerPrice = 99.9m,
            UpperPrice = 100.1m,
            ReferencePrice = 100m,
            OriginatedAt = At.AddHours(-2),
            ConfirmedAt = At.AddHours(-1),
            AvailableAt = At.AddHours(-1),
            State = LiquidityPoolState.AcceptedBreak,
            SourcePointCount = 2,
            TouchCount = 0,
            EqualnessScore = 1m,
            VisibilityScore = 1m,
            CompressionScore = 1m,
            ProminenceScore = 1m,
            FreshnessScore = 1m,
            QualityScore = 1m,
            SourcePoolIds = [],
            SnapshotVersion = 1,
            ProfileHash = "liq"
        };
        return new LiquidityAnalysisSnapshot
        {
            IsEnabled = true,
            ProfileHash = "liq",
            SnapshotVersion = 1,
            AvailableAt = At,
            Pools = [pool],
            ActivePools = [],
            RecentEvents =
            [
                new LiquidityEvent
                {
                    EventId = Guid.NewGuid(),
                    PoolId = PoolId,
                    EventType = LiquidityEventType.AcceptedBreak,
                    StateBefore = LiquidityPoolState.Active,
                    StateAfter = LiquidityPoolState.AcceptedBreak,
                    Price = 100m,
                    OccurredAt = At,
                    AvailableAt = At,
                    SnapshotVersion = 1
                }
            ],
            RecentSweeps = [],
            Quality = new LiquidityAnalysisQuality
            {
                AtrReady = true,
                ActivePoolCount = 0,
                SuppressedCandidateCount = 0,
                LastEvaluatedAt = At
            }
        };
    }
}
