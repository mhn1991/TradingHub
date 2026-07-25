using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Simulator.Tests;

/// <summary>
/// Proves <see cref="StructuralGeometryBuilder.BuildAdaptive"/> is wired to
/// <see cref="Agent.Strategies.StructuralConfluence.TargetManagement.TargetMapBuilder"/> and that
/// the legacy <see cref="StructuralGeometryBuilder.Build"/> nearest-obstacle path stays untouched.
/// </summary>
[TestFixture]
public sealed class StructuralGeometryBuilderAdaptiveTests
{
    private static readonly InstrumentKey Instrument = new("EURUSD");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-05T12:00:00Z");
    private static readonly BarInterval Context = BarInterval.Hours(1);
    private static readonly BarInterval Setup = BarInterval.Minutes(15);
    private static readonly BarInterval Trigger = BarInterval.Minutes(5);

    [Test]
    public void BuildAdaptive_PopulatesTargetPlanAndCompatibilityFields_ForFixedStructuralTarget()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            ContextInterval = Context,
            SetupInterval = Setup,
            TriggerInterval = Trigger,
            StrategyVersion = "structural-confluence-v2",
            AdaptiveTargetManagement = new AdaptiveTargetManagementOptions { Enabled = true }
        };
        LiquidityPool pool = Pool(LiquiditySide.BuySide, lower: 106m, upper: 106.4m, quality: 0.9m, prominence: 0.9m);
        StructuralEvidencePacket evidence = Packet(atr: 1m,
            context: Snapshot(Context, pools: [pool]), setup: Snapshot(Setup), trigger: Snapshot(Trigger));
        var builder = new StructuralGeometryBuilder(options);

        StructuralGeometry geometry = builder.BuildAdaptive(
            evidence, PriceActionDirection.Bullish, rawStop: 98m, stopSource: "test-stop", TradeExitPolicy.FixedStructuralTarget);

        Assert.Multiple(() =>
        {
            Assert.That(geometry.IsValid, Is.True, geometry.ReasonCode);
            Assert.That(geometry.ExitPolicy, Is.EqualTo(TradeExitPolicy.FixedStructuralTarget));
            Assert.That(geometry.TargetPlan, Is.Not.Null);
            Assert.That(geometry.Target, Is.Not.Null);
            Assert.That(geometry.TargetSource, Does.Contain("LiquidityPool"));
        });
    }

    [Test]
    public void Build_LegacyNearestObstaclePath_RejectsOnTheWeakNearbySwing_ThatBuildAdaptiveTreatsAsAJustCheckpoint()
    {
        // This is the exact problem the plan (§1) describes: v1's Build() only ever sees the
        // setup-timeframe obstacle list, so the weak nearby swing (R=1.45, below the 1.5
        // minimum) is its only candidate and it rejects - even though the stronger,
        // context-timeframe pool that BuildAdaptive found above was reachable the whole time.
        // BuildAdaptive is unaffected by this call and is never invoked from Build(), so v1's
        // exact (if suboptimal) behavior here is preserved deliberately, not a regression.
        StructuralConfluenceStrategyOptions options = new()
        {
            ContextInterval = Context,
            SetupInterval = Setup,
            TriggerInterval = Trigger,
            StrategyVersion = "structural-confluence-v2",
            AdaptiveTargetManagement = new AdaptiveTargetManagementOptions { Enabled = true }
        };
        LiquidityPool pool = Pool(LiquiditySide.BuySide, lower: 106m, upper: 106.4m, quality: 0.9m, prominence: 0.9m);
        SwingPoint nearSwing = new()
        {
            PivotTime = At.AddMinutes(-35),
            ConfirmedAt = At.AddMinutes(-30),
            Price = 103.0m,
            Type = SwingType.High,
            Strength = 3
        };
        StructuralEvidencePacket evidence = Packet(atr: 1m,
            context: Snapshot(Context, pools: [pool]), setup: Snapshot(Setup, swings: [nearSwing]), trigger: Snapshot(Trigger));
        var builder = new StructuralGeometryBuilder(options);

        StructuralGeometry geometry = builder.Build(evidence, PriceActionDirection.Bullish, rawStop: 98m, stopSource: "test-stop");

        Assert.Multiple(() =>
        {
            Assert.That(geometry.IsValid, Is.False);
            Assert.That(geometry.ReasonCode, Is.EqualTo("StructuralRewardRiskBelowMinimum"));
            Assert.That(geometry.ExitPolicy, Is.Null);
            Assert.That(geometry.TargetPlan, Is.Null);
        });
    }

    private static StructuralEvidencePacket Packet(
        decimal atr,
        AnalysisSnapshot context,
        AnalysisSnapshot setup,
        AnalysisSnapshot trigger) => new()
    {
        Instrument = Instrument,
        AvailableAt = At,
        ExecutableSpread = 0m,
        Context = context,
        Setup = setup,
        Trigger = trigger,
        AdditionalContexts = [],
        ContextEvidence = new StructuralContextEvidence(EvidenceAlignment.Neutral, EvidenceAlignment.Neutral, 50m, 50m),
        SupplyDemand = new SupplyDemandPacket(setup.SupplyDemand.Zones, []),
        Liquidity = new LiquidityPacket(setup.Liquidity.Pools, [], []),
        TriggerEvidence = new TriggerPacket([], []),
        Indicators = new IndicatorConfirmationPacket(
            atr, null, CciAnalysisSnapshot.Empty, null, RsiAnalysisSnapshot.Empty, StochRsiSnapshot.Empty,
            BollingerAnalysisSnapshot.Empty, AdxAnalysisSnapshot.Empty, null)
    };

    private static AnalysisSnapshot Snapshot(
        BarInterval interval,
        IReadOnlyList<SwingPoint>? swings = null,
        IReadOnlyList<LiquidityPool>? pools = null) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = At,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, At.AddMinutes(-5), interval, 100m, 100.5m, 99.5m, 100m),
        Indicators = new IndicatorSnapshot { Atr = 1m },
        Swings = swings ?? [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        SupplyDemand = new SupplyDemandAnalysisSnapshot
        {
            IsEnabled = true,
            ProfileHash = "sd",
            SnapshotVersion = 1,
            AvailableAt = At,
            Zones = [],
            ActiveZones = [],
            RecentEvents = [],
            Quality = new SupplyDemandAnalysisQuality
            {
                AtrReady = true,
                ActiveZoneCount = 0,
                SuppressedCandidateCount = 0,
                LastEvaluatedAt = At
            }
        },
        Liquidity = new LiquidityAnalysisSnapshot
        {
            IsEnabled = true,
            ProfileHash = "liq",
            SnapshotVersion = 1,
            AvailableAt = At,
            Pools = pools ?? [],
            ActivePools = pools ?? [],
            RecentEvents = [],
            RecentSweeps = [],
            Quality = new LiquidityAnalysisQuality
            {
                AtrReady = true,
                ActivePoolCount = pools?.Count ?? 0,
                SuppressedCandidateCount = 0,
                LastEvaluatedAt = At
            }
        },
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };

    private static LiquidityPool Pool(LiquiditySide side, decimal lower, decimal upper, decimal quality, decimal prominence) => new()
    {
        PoolId = Guid.NewGuid(),
        Instrument = Instrument,
        Interval = Context,
        Side = side,
        Type = side == LiquiditySide.SellSide ? LiquidityPoolType.EqualLows : LiquidityPoolType.EqualHighs,
        LowerPrice = lower,
        UpperPrice = upper,
        ReferencePrice = (lower + upper) / 2m,
        OriginatedAt = At.AddHours(-2),
        ConfirmedAt = At.AddHours(-1),
        AvailableAt = At.AddHours(-1),
        State = LiquidityPoolState.Active,
        SourcePointCount = 2,
        TouchCount = 0,
        EqualnessScore = 0.9m,
        VisibilityScore = 0.7m,
        CompressionScore = 0.6m,
        ProminenceScore = prominence,
        FreshnessScore = 1m,
        QualityScore = quality,
        SourcePoolIds = [],
        SnapshotVersion = 1,
        ProfileHash = "liq"
    };
}
