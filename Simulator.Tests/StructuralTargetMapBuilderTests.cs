using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.TargetManagement;
using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralTargetMapBuilderTests
{
    private static readonly InstrumentKey Instrument = new("EURUSD");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-05T12:00:00Z");
    private static readonly BarInterval Context = BarInterval.Hours(1);
    private static readonly BarInterval Setup = BarInterval.Minutes(15);
    private static readonly BarInterval Trigger = BarInterval.Minutes(5);

    private static StructuralConfluenceStrategyOptions Options() => new()
    {
        ContextInterval = Context,
        SetupInterval = Setup,
        TriggerInterval = Trigger,
        StrategyVersion = "structural-confluence-v2",
        AdaptiveTargetManagement = new AdaptiveTargetManagementOptions { Enabled = true }
    };

    [Test]
    public void WeakNearestSwing_BecomesCheckpoint_WhileStrongerExternalTargetRemainsTerminal()
    {
        // Buy: entry=100, stop=98 (risk=2), atr=1. Weak setup-tf swing at 103 (R=1.45 after buffer)
        // is nearer than a strong context-tf liquidity pool at 106 (R=2.95, Tier A).
        SwingPoint weakSwing = Swing(SwingType.High, 103.0m, At.AddMinutes(-30));
        LiquidityPool strongPool = Pool(LiquiditySide.BuySide, lower: 106.0m, upper: 106.4m, quality: 0.70m, prominence: 0.65m);
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context, pools: [strongPool]),
            setup: Snapshot(Setup, swings: [weakSwing]),
            trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult result = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.PartialThenRunner);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsAdmissible, Is.True, result.ReasonCode);
            Assert.That(result.SelectedTerminal, Is.Not.Null);
            Assert.That(result.SelectedTerminal!.SourceKind, Is.EqualTo(TradeTargetSourceKind.LiquidityPool));
            Assert.That(result.SelectedTerminal.Tier, Is.EqualTo(TradeTargetSignificanceTier.TierA));
            Assert.That(result.SelectedCheckpoint, Is.Not.Null);
            Assert.That(result.SelectedCheckpoint!.SourceKind, Is.EqualTo(TradeTargetSourceKind.Swing));
            Assert.That(result.SelectedCheckpoint.Tier, Is.EqualTo(TradeTargetSignificanceTier.TierC));
            Assert.That(result.Plan, Is.Not.Null);
            Assert.That(result.Plan!.PlannedR, Is.GreaterThan(1.5m));
        });
    }

    [Test]
    public void HardOppositingZoneBefore1_5R_RejectsFixedStructuralTarget()
    {
        // Terminal zone at 101.2 gives R = 1.2 (buffered), below the 1.5R fixed-target minimum.
        SupplyDemandZone zone = Zone(SupplyDemandZoneType.Supply, lower: 101.2m, upper: 101.6m, quality: 0.60m, touches: 0);
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context, zones: [zone]),
            setup: Snapshot(Setup),
            trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult result = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsAdmissible, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("StructuralRewardRiskBelowMinimum"));
        });
    }

    [Test]
    public void NearerTierBBarrier_IsSelectedBeforeFartherTierATarget()
    {
        // The setup pool is a Tier B hard barrier at sub-minimum R. A farther context Tier A pool
        // must not be selected through it merely because its tier is stronger.
        LiquidityPool nearBarrier = Pool(
            LiquiditySide.BuySide, lower: 102.8m, upper: 103.0m, quality: 0.50m, prominence: 0.40m);
        LiquidityPool farTarget = Pool(
            LiquiditySide.BuySide, lower: 106.0m, upper: 106.4m, quality: 0.90m, prominence: 0.90m);
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context, pools: [farTarget]),
            setup: Snapshot(Setup, pools: [nearBarrier]),
            trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult result = builder.Build(
            evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget);

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates.Single(item =>
                    item.SourceId == $"LiquidityPool:{nearBarrier.PoolId:N}").Role,
                Is.EqualTo(TradeTargetRole.Terminal));
            Assert.That(result.Candidates.Single(item =>
                    item.SourceId == $"LiquidityPool:{farTarget.PoolId:N}").Role,
                Is.EqualTo(TradeTargetRole.HardBarrier));
            Assert.That(result.IsAdmissible, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("StructuralRewardRiskBelowMinimum"));
        });
    }

    [Test]
    public void ExcludedLifecycleStates_AreNeverCandidates()
    {
        LiquidityPool consumed = Pool(LiquiditySide.BuySide, lower: 106m, upper: 106.4m, quality: 0.9m, prominence: 0.9m)
            with { State = LiquidityPoolState.Consumed };
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context, pools: [consumed]),
            setup: Snapshot(Setup),
            trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult result = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget);

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Is.Empty);
            Assert.That(result.IsAdmissible, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("StructuralTargetMapNoTerminalCandidate"));
        });
    }

    [Test]
    public void ExcludedSourceId_RemovesTheBrokenCatalystLevelFromCandidacy()
    {
        LiquidityPool pool = Pool(LiquiditySide.BuySide, lower: 106m, upper: 106.4m, quality: 0.9m, prominence: 0.9m);
        string catalystId = $"LiquidityPool:{pool.PoolId:N}";
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context, pools: [pool]),
            setup: Snapshot(Setup),
            trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult result = builder.Build(
            evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget, [catalystId]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Is.Empty);
            Assert.That(result.IsAdmissible, Is.False);
        });
    }

    [Test]
    public void ClusteredIndependentSources_PromoteOneTierAndMergeIntoOneCandidate()
    {
        // Two independent Tier-C setup/trigger swings within 0.25 ATR of each other, with no
        // Tier A/B candidate anywhere: the cluster should promote C -> B and become terminal.
        SwingPoint setupSwing = Swing(SwingType.High, 106.0m, At.AddMinutes(-30));
        SwingPoint triggerSwing = Swing(SwingType.High, 106.1m, At.AddMinutes(-10));
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context),
            setup: Snapshot(Setup, swings: [setupSwing]),
            trigger: Snapshot(Trigger, swings: [triggerSwing]));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult result = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget);

        Assert.Multiple(() =>
        {
            Assert.That(result.Candidates, Has.Count.EqualTo(1));
            Assert.That(result.Candidates[0].Tier, Is.EqualTo(TradeTargetSignificanceTier.TierB));
            Assert.That(result.Candidates[0].ConstituentSourceIds, Has.Count.EqualTo(2));
            Assert.That(result.SelectedTerminal, Is.Not.Null);
            Assert.That(result.IsAdmissible, Is.True);
        });
    }

    [Test]
    public void Build_IsDeterministic_AcrossIdenticalReplayedInputs()
    {
        LiquidityPool pool = Pool(LiquiditySide.BuySide, lower: 106m, upper: 106.4m, quality: 0.9m, prominence: 0.9m);
        SwingPoint swing = Swing(SwingType.High, 103.0m, At.AddMinutes(-30));
        StructuralEvidencePacket evidence = Packet(
            buy: true,
            atr: 1m,
            context: Snapshot(Context, pools: [pool]),
            setup: Snapshot(Setup, swings: [swing]),
            trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult first = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.PartialThenRunner);
        TargetMapResult second = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.PartialThenRunner);

        Assert.Multiple(() =>
        {
            Assert.That(second.ReasonCode, Is.EqualTo(first.ReasonCode));
            Assert.That(second.Candidates.Select(item => item.CandidateId),
                Is.EqualTo(first.Candidates.Select(item => item.CandidateId)));
            Assert.That(second.SelectedTerminal?.CandidateId, Is.EqualTo(first.SelectedTerminal?.CandidateId));
            Assert.That(second.SelectedCheckpoint?.CandidateId, Is.EqualTo(first.SelectedCheckpoint?.CandidateId));
            Assert.That(second.Plan?.PlannedR, Is.EqualTo(first.Plan?.PlannedR));
        });
    }

    [Test]
    public void LongAndShort_AreSymmetric()
    {
        LiquidityPool buyPool = Pool(LiquiditySide.BuySide, lower: 106m, upper: 106.4m, quality: 0.7m, prominence: 0.65m);
        StructuralEvidencePacket buyEvidence = Packet(
            buy: true, atr: 1m,
            context: Snapshot(Context, pools: [buyPool]), setup: Snapshot(Setup), trigger: Snapshot(Trigger));
        LiquidityPool sellPool = Pool(LiquiditySide.SellSide, lower: 93.6m, upper: 94.0m, quality: 0.7m, prominence: 0.65m);
        StructuralEvidencePacket sellEvidence = Packet(
            buy: false, atr: 1m,
            context: Snapshot(Context, pools: [sellPool]), setup: Snapshot(Setup), trigger: Snapshot(Trigger));

        var builder = new TargetMapBuilder(Options());
        TargetMapResult buyResult = builder.Build(buyEvidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget);
        TargetMapResult sellResult = builder.Build(sellEvidence, PriceActionDirection.Bearish, entry: 100m, stop: 102m, TradeExitPolicy.FixedStructuralTarget);

        Assert.Multiple(() =>
        {
            Assert.That(buyResult.IsAdmissible, Is.True);
            Assert.That(sellResult.IsAdmissible, Is.True);
            Assert.That(sellResult.SelectedTerminal!.TargetR, Is.EqualTo(buyResult.SelectedTerminal!.TargetR));
            Assert.That(sellResult.SelectedTerminal.DistanceAtr, Is.EqualTo(buyResult.SelectedTerminal.DistanceAtr));
        });
    }

    [Test]
    public void Projection_IsOnlyUsedForManagedExpansion_WhenNoTerminalCandidateExists()
    {
        StructuralEvidencePacket evidence = Packet(
            buy: true, atr: 1m,
            context: Snapshot(Context), setup: Snapshot(Setup), trigger: Snapshot(Trigger));
        var builder = new TargetMapBuilder(Options());

        TargetMapResult fixedResult = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.FixedStructuralTarget);
        TargetMapResult partialResult = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.PartialThenRunner);
        TargetMapResult expansionResult = builder.Build(evidence, PriceActionDirection.Bullish, entry: 100m, stop: 98m, TradeExitPolicy.ManagedExpansion);

        Assert.Multiple(() =>
        {
            Assert.That(fixedResult.IsAdmissible, Is.False);
            Assert.That(fixedResult.Candidates.Any(item => item.SourceKind == TradeTargetSourceKind.RMultipleProjection), Is.False);
            Assert.That(partialResult.IsAdmissible, Is.False);
            Assert.That(partialResult.Candidates.Any(item => item.SourceKind == TradeTargetSourceKind.RMultipleProjection), Is.False);
            Assert.That(expansionResult.IsAdmissible, Is.True);
            Assert.That(expansionResult.SelectedTerminal!.SourceKind, Is.EqualTo(TradeTargetSourceKind.RMultipleProjection));
            Assert.That(expansionResult.SelectedTerminal.Role, Is.EqualTo(TradeTargetRole.Projection));
        });
    }

    private static StructuralEvidencePacket Packet(
        bool buy,
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
        SupplyDemand = new SupplyDemandPacket([], []),
        Liquidity = new LiquidityPacket([], [], []),
        TriggerEvidence = new TriggerPacket([], []),
        Indicators = new IndicatorConfirmationPacket(
            atr, null, CciAnalysisSnapshot.Empty, null, RsiAnalysisSnapshot.Empty, StochRsiSnapshot.Empty,
            BollingerAnalysisSnapshot.Empty, AdxAnalysisSnapshot.Empty, null)
    };

    private static AnalysisSnapshot Snapshot(
        BarInterval interval,
        IReadOnlyList<SwingPoint>? swings = null,
        IReadOnlyList<LiquidityPool>? pools = null,
        IReadOnlyList<SupplyDemandZone>? zones = null) => new()
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
            Zones = zones ?? [],
            ActiveZones = zones ?? [],
            RecentEvents = [],
            Quality = new SupplyDemandAnalysisQuality
            {
                AtrReady = true,
                ActiveZoneCount = zones?.Count ?? 0,
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

    private static SwingPoint Swing(SwingType type, decimal price, DateTimeOffset confirmedAt) => new()
    {
        PivotTime = confirmedAt.AddMinutes(-5),
        ConfirmedAt = confirmedAt,
        Price = price,
        Type = type,
        Strength = 3
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

    private static SupplyDemandZone Zone(SupplyDemandZoneType type, decimal lower, decimal upper, decimal quality, int touches) => new()
    {
        ZoneId = Guid.NewGuid(),
        Instrument = Instrument,
        Interval = Context,
        Type = type,
        Pattern = type == SupplyDemandZoneType.Demand ? SupplyDemandPattern.DropBaseRally : SupplyDemandPattern.RallyBaseDrop,
        ProximalPrice = lower,
        DistalPrice = upper,
        BaseStartedAt = At.AddHours(-1),
        BaseEndedAt = At.AddMinutes(-45),
        DepartureStartedAt = At.AddMinutes(-30),
        ConfirmedAt = At.AddHours(-1),
        AvailableAt = At.AddHours(-1),
        State = SupplyDemandZoneState.ConfirmedFresh,
        BaseCandleCount = 2,
        TouchCount = touches,
        DepartureAtr = 2m,
        DepartureEfficiency = 0.8m,
        BaseCompactness = 0.8m,
        ImbalanceRatio = 0.7m,
        PenetrationRatio = 0m,
        FreshnessScore = 1m,
        QualityScore = quality,
        BrokeStructure = true,
        HasFairValueGap = false,
        BoundaryMode = ZoneBoundaryMode.FullWickRange,
        SourceZoneIds = [],
        SnapshotVersion = 1,
        ProfileHash = "sd"
    };
}
