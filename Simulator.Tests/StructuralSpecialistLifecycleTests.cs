using Agent.Models;
using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using NUnit.Framework;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralSpecialistLifecycleTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void BreakRetest_PreservesPoolIdentityAcrossArmedBreakAndRetestStages()
    {
        var playbook = new LiquidityBreakRetestPlaybook(new StructuralConfluenceStrategyOptions());
        LiquidityPool active = Pool(LiquiditySide.BuySide, LiquidityPoolState.Active);
        PlaybookEvaluation armed = playbook.Evaluate(
            Evidence(active, [], []), new PlaybookRuntimeState());

        LiquidityPool broken = active with { State = LiquidityPoolState.AcceptedBreak };
        LiquidityEvent acceptedBreak = Event(
            broken, LiquidityEventType.AcceptedBreak, Now.AddMinutes(-10), 1.11m);
        PlaybookEvaluation catalyst = playbook.Evaluate(
            Evidence(broken, [acceptedBreak], []), new PlaybookRuntimeState());

        LiquidityEvent retest = Event(
            broken, LiquidityEventType.Retest, Now, 1.1005m);
        PlaybookEvaluation awaitingTrigger = playbook.Evaluate(
            Evidence(broken, [acceptedBreak, retest], []), new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(armed.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.Armed));
            Assert.That(catalyst.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.CatalystObserved));
            Assert.That(awaitingTrigger.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.AwaitingTrigger));
            Assert.That(catalyst.SetupId, Is.EqualTo(armed.SetupId));
            Assert.That(awaitingTrigger.SetupId, Is.EqualTo(armed.SetupId));
            Assert.That(awaitingTrigger.MandatoryGates.Single(item => item.Name == "Cci")
                .LimitsConfidenceFloor, Is.False);
            Assert.That(awaitingTrigger.MandatoryGates.Single(item => item.Name == "Expansion")
                .LimitsConfidenceFloor, Is.False);
        });
    }

    [Test]
    public void BreakRetest_PreservesOlderRetestedHypothesisWhenNewPoolBreaks()
    {
        var playbook = new LiquidityBreakRetestPlaybook(new StructuralConfluenceStrategyOptions());
        LiquidityPool retestedPool = Pool(
            LiquiditySide.BuySide,
            LiquidityPoolState.AcceptedBreak,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
        LiquidityPool newerPool = Pool(
            LiquiditySide.BuySide,
            LiquidityPoolState.AcceptedBreak,
            Guid.Parse("33333333-3333-3333-3333-333333333333"));
        LiquidityEvent olderBreak = Event(
            retestedPool, LiquidityEventType.AcceptedBreak, Now.AddMinutes(-10), 1.11m);
        LiquidityEvent retest = Event(
            retestedPool, LiquidityEventType.Retest, Now.AddMinutes(-5), 1.1005m);
        LiquidityEvent newerBreak = Event(
            newerPool, LiquidityEventType.AcceptedBreak, Now, 1.11m);

        PlaybookEvaluation evaluation = playbook.Evaluate(
            Evidence([retestedPool, newerPool], [olderBreak, retest, newerBreak], []),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.PrimaryPoolId, Is.EqualTo(retestedPool.PoolId));
            Assert.That(evaluation.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.AwaitingTrigger));
            Assert.That(evaluation.SupportingEvidence, Does.Contain("LiquidityRetestHeld"));
            Assert.That(evaluation.ReasonCode, Is.Not.EqualTo("StructuralRetestMissing"));
        });
    }

    [Test]
    public void BreakRetest_UsesCausalLowerTimeframeContinuationWithoutExactLevelIdentity()
    {
        var playbook = new LiquidityBreakRetestPlaybook(new StructuralConfluenceStrategyOptions());
        LiquidityPool pool = Pool(
            LiquiditySide.BuySide,
            LiquidityPoolState.AcceptedBreak) with
        {
            OriginatedAt = Now.AddHours(-4),
            ConfirmedAt = Now.AddHours(-3),
            AvailableAt = Now.AddHours(-3)
        };
        LiquidityEvent acceptedBreak = Event(
            pool, LiquidityEventType.AcceptedBreak, Now.AddMinutes(-10), 1.11m);
        LiquidityEvent retest = Event(
            pool, LiquidityEventType.Retest, Now.AddMinutes(-5), 1.1005m);
        var displacement = new PriceActionEvent
        {
            EventId = "post-retest-displacement",
            Type = PriceActionEventType.BullishDisplacement,
            Direction = PriceActionDirection.Bullish,
            ConfirmedAt = Now,
            ConfirmedSequence = 10,
            // A 5m displacement references its close, not the narrow 15m liquidity pool.
            ReferenceLevel = 1.1100m,
            Strength = 75m,
            Confidence = 80m,
            ReasonCode = "BullishDisplacementConfirmed",
            Explanation = "Test"
        };

        PlaybookEvaluation evaluation = playbook.Evaluate(
            Evidence(pool, [acceptedBreak, retest], [], [displacement]),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.MandatoryGates.Single(item => item.Name == "Trigger").Passed,
                Is.True);
            Assert.That(evaluation.TriggerEvent?.EventId, Is.EqualTo(displacement.EventId));
            Assert.That(evaluation.TriggerQuality, Is.EqualTo(displacement.Confidence));
        });
    }

    [Test]
    public void BreakRetest_StartsTriggerWindowAtRetestAndExpiresAfterResponseWindow()
    {
        var playbook = new LiquidityBreakRetestPlaybook(new StructuralConfluenceStrategyOptions());
        LiquidityPool pool = Pool(
            LiquiditySide.BuySide,
            LiquidityPoolState.AcceptedBreak) with
        {
            OriginatedAt = Now.AddHours(-4),
            ConfirmedAt = Now.AddHours(-3),
            AvailableAt = Now.AddHours(-3)
        };
        LiquidityEvent acceptedBreak = Event(
            pool, LiquidityEventType.AcceptedBreak, Now.AddHours(-2), 1.11m);

        PlaybookEvaluation beforeRetest = playbook.Evaluate(
            Evidence(pool, [acceptedBreak], []), new PlaybookRuntimeState());

        LiquidityEvent retest = Event(
            pool, LiquidityEventType.Retest, Now.AddMinutes(-5), 1.1005m);
        DateTimeOffset afterResponseWindow = Now.AddMinutes(60);
        PlaybookEvaluation expired = playbook.Evaluate(
            Evidence(
                pool, [acceptedBreak, retest], [],
                availableAt: afterResponseWindow),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(beforeRetest.Lifecycle,
                Is.EqualTo(StructuralSetupLifecycle.CatalystObserved));
            Assert.That(beforeRetest.MandatoryGates
                .Single(item => item.Name == "TriggerWindow").Passed, Is.True);
            Assert.That(expired.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.Expired));
            Assert.That(expired.ReasonCode, Is.EqualTo("StructuralTriggerWindowExpired"));
            Assert.That(expired.ExpiresAt, Is.EqualTo(Now.AddMinutes(55)));
        });
    }

    [Test]
    public void SweepReversal_RequiresShiftAndPreservesPoolIdentityAcrossStages()
    {
        var playbook = new LiquiditySweepReversalPlaybook(new StructuralConfluenceStrategyOptions());
        LiquidityPool active = Pool(LiquiditySide.SellSide, LiquidityPoolState.Active);
        PlaybookEvaluation armed = playbook.Evaluate(
            Evidence(active, [], []), new PlaybookRuntimeState());

        LiquidityPool swept = active with { State = LiquidityPoolState.Swept };
        LiquiditySweepEvent sweep = Sweep(swept, structureShiftConfirmed: false);
        PlaybookEvaluation catalyst = playbook.Evaluate(
            Evidence(swept, [], [sweep]), new PlaybookRuntimeState());
        PlaybookEvaluation awaitingTrigger = playbook.Evaluate(
            Evidence(swept, [], [sweep with { StructureShiftConfirmed = true }]),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(armed.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.Armed));
            Assert.That(catalyst.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.CatalystObserved));
            Assert.That(catalyst.MandatoryGates.Single(item => item.Name == "StructureShift").Passed,
                Is.False);
            Assert.That(awaitingTrigger.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.AwaitingTrigger));
            Assert.That(awaitingTrigger.MandatoryGates.Single(item => item.Name == "StructureShift").Passed,
                Is.True);
            Assert.That(catalyst.SetupId, Is.EqualTo(armed.SetupId));
            Assert.That(awaitingTrigger.SetupId, Is.EqualTo(armed.SetupId));
            Assert.That(awaitingTrigger.MandatoryGates
                .Single(item => item.Name == "SupplyDemandConfluence")
                .LimitsConfidenceFloor, Is.False);
            Assert.That(awaitingTrigger.MandatoryGates.Single(item => item.Name == "Cci")
                .LimitsConfidenceFloor, Is.False);
        });
    }

    [Test]
    public void SweepReversal_ExpiresWhenItsTriggerResponseWindowCloses()
    {
        var playbook = new LiquiditySweepReversalPlaybook(
            new StructuralConfluenceStrategyOptions());
        LiquidityPool pool = Pool(LiquiditySide.SellSide, LiquidityPoolState.Swept);
        LiquiditySweepEvent sweep = Sweep(pool, structureShiftConfirmed: true);

        PlaybookEvaluation expired = playbook.Evaluate(
            Evidence(
                pool, [], [sweep],
                availableAt: Now.AddMinutes(60)),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(expired.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.Expired));
            Assert.That(expired.ReasonCode, Is.EqualTo("StructuralTriggerWindowExpired"));
            Assert.That(expired.ExpiresAt, Is.EqualTo(Now.AddMinutes(55)));
        });
    }

    [Test]
    public void SupplyDemand_StartsTriggerWindowAtReactionNotZoneFormation()
    {
        var options = new StructuralConfluenceStrategyOptions
        {
            SupplyDemandPullback = new SupplyDemandPullbackOptions
            {
                RequireTrendAlignment = false
            }
        };
        var playbook = new SupplyDemandPullbackPlaybook(options);
        SupplyDemandZone zone = Zone();

        PlaybookEvaluation armed = playbook.Evaluate(
            Evidence(
                [], [], [],
                supplyDemand: new SupplyDemandPacket([zone], [])),
            new PlaybookRuntimeState());

        SupplyDemandZoneEvent reaction = ZoneReaction(zone, Now.AddMinutes(-5));
        PlaybookEvaluation expired = playbook.Evaluate(
            Evidence(
                [], [], [],
                availableAt: Now.AddMinutes(60),
                supplyDemand: new SupplyDemandPacket([zone], [reaction])),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(armed.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.Armed));
            Assert.That(armed.CatalystAt, Is.Null);
            Assert.That(armed.ExpiresAt, Is.Null);
            Assert.That(expired.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.Expired));
            Assert.That(expired.ReasonCode, Is.EqualTo("StructuralTriggerWindowExpired"));
            Assert.That(expired.ExpiresAt, Is.EqualTo(Now.AddMinutes(55)));
        });
    }

    private static LiquidityPool Pool(
        LiquiditySide side,
        LiquidityPoolState state,
        Guid? poolId = null) => new()
    {
        PoolId = poolId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Instrument = Instrument,
        Interval = BarInterval.Minutes(15),
        Side = side,
        Type = side == LiquiditySide.BuySide
            ? LiquidityPoolType.EqualHighs
            : LiquidityPoolType.EqualLows,
        LowerPrice = 1.0995m,
        UpperPrice = 1.1005m,
        ReferencePrice = 1.1000m,
        OriginatedAt = Now.AddHours(-2),
        ConfirmedAt = Now.AddHours(-1),
        AvailableAt = Now.AddHours(-1),
        State = state,
        SourcePointCount = 2,
        TouchCount = 1,
        DistinctTouchCount = 1,
        EqualnessScore = 0.8m,
        VisibilityScore = 0.8m,
        CompressionScore = 0.7m,
        ProminenceScore = 0.8m,
        FreshnessScore = 0.8m,
        QualityScore = 0.8m,
        SourcePoolIds = [],
        SnapshotVersion = 1,
        ProfileHash = "test"
    };

    private static LiquidityEvent Event(
        LiquidityPool pool,
        LiquidityEventType type,
        DateTimeOffset at,
        decimal price) => new()
    {
        EventId = Guid.NewGuid(),
        PoolId = pool.PoolId,
        EventType = type,
        StateBefore = type == LiquidityEventType.AcceptedBreak
            ? LiquidityPoolState.Touched
            : LiquidityPoolState.AcceptedBreak,
        StateAfter = LiquidityPoolState.AcceptedBreak,
        Price = price,
        OccurredAt = at,
        AvailableAt = at,
        SnapshotVersion = 2
    };

    private static LiquiditySweepEvent Sweep(
        LiquidityPool pool,
        bool structureShiftConfirmed) => new()
    {
        SweepId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        PoolId = pool.PoolId,
        SweepStartedAt = Now.AddMinutes(-10),
        ConfirmedAt = Now.AddMinutes(-5),
        AvailableAt = Now.AddMinutes(-5),
        ExtremePrice = 1.0950m,
        PenetrationAtr = 0.2m,
        ClosedBackInside = true,
        ClosedBackBeyondOriginSide = true,
        DisplacementConfirmed = true,
        StructureShiftConfirmed = structureShiftConfirmed,
        RejectionStrength = 0.7m,
        QualityScore = 0.8m,
        SnapshotVersion = 2
    };

    private static SupplyDemandZone Zone() => new()
    {
        ZoneId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Instrument = Instrument,
        Interval = BarInterval.Minutes(15),
        Type = SupplyDemandZoneType.Demand,
        Pattern = SupplyDemandPattern.DropBaseRally,
        ProximalPrice = 1.1005m,
        DistalPrice = 1.0995m,
        BaseStartedAt = Now.AddHours(-3),
        BaseEndedAt = Now.AddHours(-2.75),
        DepartureStartedAt = Now.AddHours(-2.5),
        ConfirmedAt = Now.AddHours(-2),
        AvailableAt = Now.AddHours(-2),
        State = SupplyDemandZoneState.Tested,
        BaseCandleCount = 2,
        TouchCount = 1,
        DistinctTouchCount = 1,
        DepartureAtr = 1.5m,
        DepartureEfficiency = 0.8m,
        BaseCompactness = 0.8m,
        ImbalanceRatio = 0.8m,
        PenetrationRatio = 0.2m,
        FreshnessScore = 0.8m,
        QualityScore = 0.8m,
        BrokeStructure = true,
        HasFairValueGap = true,
        BoundaryMode = ZoneBoundaryMode.BodyToExtreme,
        SourceZoneIds = [],
        SnapshotVersion = 1,
        ProfileHash = "test"
    };

    private static SupplyDemandZoneEvent ZoneReaction(
        SupplyDemandZone zone,
        DateTimeOffset at) => new()
    {
        EventId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        ZoneId = zone.ZoneId,
        EventType = SupplyDemandZoneEventType.Touched,
        StateBefore = SupplyDemandZoneState.Approached,
        StateAfter = SupplyDemandZoneState.Tested,
        Price = 1.1000m,
        OccurredAt = at,
        AvailableAt = at,
        SnapshotVersion = 2
    };

    private static StructuralEvidencePacket Evidence(
        LiquidityPool pool,
        IReadOnlyList<LiquidityEvent> events,
        IReadOnlyList<LiquiditySweepEvent> sweeps,
        IReadOnlyList<PriceActionEvent>? triggerEvents = null,
        DateTimeOffset? availableAt = null) =>
        Evidence([pool], events, sweeps, triggerEvents, availableAt);

    private static StructuralEvidencePacket Evidence(
        IReadOnlyList<LiquidityPool> pools,
        IReadOnlyList<LiquidityEvent> events,
        IReadOnlyList<LiquiditySweepEvent> sweeps,
        IReadOnlyList<PriceActionEvent>? triggerEvents = null,
        DateTimeOffset? availableAt = null,
        SupplyDemandPacket? supplyDemand = null)
    {
        DateTimeOffset evaluatedAt = availableAt ?? Now;
        var indicators = new IndicatorSnapshot { Atr = 0.01m };
        AnalysisSnapshot context = Analysis(BarInterval.Hours(1), indicators, evaluatedAt);
        AnalysisSnapshot setup = Analysis(BarInterval.Minutes(15), indicators, evaluatedAt);
        AnalysisSnapshot trigger = Analysis(BarInterval.Minutes(5), indicators, evaluatedAt);
        return new StructuralEvidencePacket
        {
            Instrument = Instrument,
            AvailableAt = evaluatedAt,
            ExecutableSpread = 0.0001m,
            Context = context,
            Setup = setup,
            Trigger = trigger,
            AdditionalContexts = [],
            ContextEvidence = new StructuralContextEvidence(
                EvidenceAlignment.Neutral, EvidenceAlignment.Neutral, 50m, 50m),
            SupplyDemand = supplyDemand ?? new SupplyDemandPacket([], []),
            Liquidity = new LiquidityPacket(pools, events, sweeps),
            TriggerEvidence = new TriggerPacket(triggerEvents ?? [], []),
            Indicators = new IndicatorConfirmationPacket(
                0.01m, null, CciAnalysisSnapshot.Empty, null, RsiAnalysisSnapshot.Empty,
                StochRsiSnapshot.Empty, BollingerAnalysisSnapshot.Empty,
                AdxAnalysisSnapshot.Empty, null)
        };
    }

    private static AnalysisSnapshot Analysis(
        BarInterval interval,
        IndicatorSnapshot indicators,
        DateTimeOffset availableAt) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = availableAt,
        Version = 2,
        LatestCandle = TestCandles.Create(
            Instrument,
            availableAt - TimeSpan.FromSeconds(BarIntervalParser.ApproximateSeconds(interval)),
            interval, 1.1050m, 1.1120m, 1.0990m, 1.1100m),
        Indicators = indicators,
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
