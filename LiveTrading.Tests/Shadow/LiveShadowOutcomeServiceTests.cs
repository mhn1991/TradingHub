using Agent.Configuration;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using LiveTrading.Agents;
using LiveTrading.Actors;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using LiveTrading.Persistence;
using LiveTrading.Shadow.Outcomes;
using LiveTrading.Tests.Phase3;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using TradeManager;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Shadow;

[TestFixture]
public sealed class LiveShadowOutcomeServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly BarInterval ThesisInterval = BarInterval.Hours(1);
    private static readonly Guid ZoneId =
        Guid.Parse("99999999-1111-2222-3333-444444444444");

    [Test]
    public async Task ObserveOnly_PersistsDiagnosticsWithoutOpeningPaperPosition()
    {
        var persistence = new RecordingPersistence();
        var service = BuildService(persistence);

        ShadowAdmissionResult result = await service.AdmitAsync(
            Candidate(), StrategyActivationMode.ObserveOnly, Quote(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Admission.Disposition, Is.EqualTo(ShadowCandidateDisposition.Observed));
            Assert.That(result.Position, Is.Null);
            Assert.That(service.Snapshot.CandidateCount, Is.EqualTo(1));
            Assert.That(service.Snapshot.OpenPositionCount, Is.Zero);
            Assert.That(persistence.Streams, Is.EqualTo(new[] { "shadow-candidates" }).AsCollection);
        });
    }

    [Test]
    public async Task ShadowAdmission_UsesExecutableSideAndStableCandidateIdIsIdempotent()
    {
        var persistence = new RecordingPersistence();
        var service = BuildService(persistence, entrySlippageBasisPoints: 1m);
        LiveTradeCandidate candidate = Candidate();

        ShadowAdmissionResult admitted = await service.AdmitAsync(
            candidate, StrategyActivationMode.Shadow, Quote(), CancellationToken.None);
        ShadowAdmissionResult duplicate = await service.AdmitAsync(
            candidate, StrategyActivationMode.Shadow, Quote(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(admitted.Admission.Disposition, Is.EqualTo(ShadowCandidateDisposition.Admitted));
            Assert.That(admitted.Admission.EntryFill!.Price, Is.EqualTo(1.1002m * 1.0001m));
            Assert.That(admitted.Position!.PlaybookId, Is.EqualTo("breakout-retest"));
            Assert.That(duplicate.Admission.Disposition, Is.EqualTo(ShadowCandidateDisposition.Duplicate));
            Assert.That(service.Snapshot.CandidateCount, Is.EqualTo(1));
            Assert.That(service.Snapshot.OpenPositionCount, Is.EqualTo(1));
            Assert.That(persistence.Streams.Count(stream => stream == "shadow-candidates"), Is.EqualTo(1));
            Assert.That(persistence.Streams, Does.Contain("shadow-paper-fills"));
            Assert.That(persistence.Streams, Does.Contain("shadow-paper-positions"));
        });
    }

    [Test]
    public async Task CompletedCandle_WhenStopAndTargetTouch_AssumesStopFirst()
    {
        var persistence = new RecordingPersistence();
        var service = BuildService(persistence);
        await service.AdmitAsync(
            Candidate(), StrategyActivationMode.Shadow, Quote(), CancellationToken.None);

        await service.OnCompletedCandleAsync(new Candle
        {
            Instrument = Phase3TestData.Instrument,
            Interval = BarInterval.Minutes(1),
            OpenTime = Now,
            CloseTime = Now.AddMinutes(1),
            Prices = new Ohlc(1.1000m, 1.1110m, 1.0940m, 1.1050m),
            IsComplete = true
        }, CancellationToken.None);

        ShadowTradeOutcome outcome = persistence.Payloads.OfType<ShadowTradeOutcome>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(ShadowOutcomeState.Completed));
            Assert.That(outcome.ExitReason, Is.EqualTo(ShadowExitReason.StopAndTargetSameCandle));
            Assert.That(outcome.ExitFill!.Price, Is.EqualTo(1.0950m));
            Assert.That(outcome.NetProfitLoss, Is.LessThan(0m));
            Assert.That(service.Snapshot.OpenPositionCount, Is.Zero);
            Assert.That(service.Snapshot.CompletedCount, Is.EqualTo(1));
            Assert.That(persistence.Streams, Does.Contain("shadow-outcomes"));
        });
    }

    [Test]
    public async Task CompletedCandles_WithoutStateChange_DoNotPersistDetailedRows()
    {
        var persistence = new RecordingPersistence();
        var service = BuildService(persistence);
        ShadowAdmissionResult admission = await service.AdmitAsync(
            Candidate(), StrategyActivationMode.Shadow, Quote(), CancellationToken.None);
        int persistedAtAdmission = persistence.Streams.Count;
        decimal price = admission.Position!.EntryPrice;

        for (int index = 0; index < 1_000; index++)
        {
            DateTimeOffset openTime = Now.AddMinutes(index);
            await service.OnCompletedCandleAsync(new Candle
            {
                Instrument = Phase3TestData.Instrument,
                Interval = BarInterval.Minutes(1),
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(price, price, price, price),
                IsComplete = true
            }, CancellationToken.None);
        }

        Assert.Multiple(() =>
        {
            Assert.That(service.Snapshot.OpenPositionCount, Is.EqualTo(1));
            Assert.That(persistence.Streams, Has.Count.EqualTo(persistedAtAdmission));
            Assert.That(persistence.Streams, Does.Not.Contain("shadow-management"));
        });
    }

    [Test]
    public async Task Restore_ReopensValidPositionAndTerminallyRecordsInvalidPositionAsAmbiguous()
    {
        var originalPersistence = new RecordingPersistence();
        var original = BuildService(originalPersistence);
        LiveTradeCandidate candidate = Candidate();
        ShadowAdmissionResult admission = await original.AdmitAsync(
            candidate, StrategyActivationMode.Shadow, Quote(), CancellationToken.None);
        LiveShadowOutcomeCheckpoint checkpoint = original.Checkpoint with
        {
            OpenPositions =
            [
                admission.Position!,
                admission.Position! with
                {
                    PositionId = "invalid-position",
                    CandidateId = "invalid-candidate",
                    RemainingQuantity = 0m
                }
            ]
        };
        var recoveredPersistence = new RecordingPersistence();
        var recovered = BuildService(recoveredPersistence);

        await recovered.RestoreAsync(checkpoint, CancellationToken.None);
        ShadowAdmissionResult duplicate = await recovered.AdmitAsync(
            candidate, StrategyActivationMode.Shadow, Quote(), CancellationToken.None);

        ShadowTradeOutcome ambiguous = recoveredPersistence.Payloads
            .OfType<ShadowTradeOutcome>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(recovered.Snapshot.OpenPositionCount, Is.EqualTo(1));
            Assert.That(recovered.Snapshot.AmbiguousCount, Is.EqualTo(1));
            Assert.That(ambiguous.State, Is.EqualTo(ShadowOutcomeState.Ambiguous));
            Assert.That(ambiguous.ExitReason, Is.EqualTo(ShadowExitReason.RecoveryAmbiguity));
            Assert.That(duplicate.Admission.Disposition, Is.EqualTo(ShadowCandidateDisposition.Duplicate));
            Assert.That(recoveredPersistence.Streams, Is.EqualTo(new[] { "shadow-outcomes" }).AsCollection);
        });
    }

    [Test]
    public async Task CausalAnalysis_UsesEntryPinnedStructuralPolicyAndClosesInvalidatedZone()
    {
        var persistence = new RecordingPersistence();
        LiveTradingPolicyBundle policy = Phase3TestData.Policy() with
        {
            PolicyBundleId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Revision = 7,
            StrategyVersion = TradingAgentTypeIds.StructuralConfluence + "-v1",
            StructuralManagement = PositionManagementOptions.StructuralDefaults,
            ConfigurationHash = "structural-shadow-test"
        };
        var key = new AgentInstanceKey(
            AgentInstanceKey.DefaultDeploymentId,
            Phase3TestData.Instrument,
            TradingAgentTypeIds.StructuralConfluence,
            policy.PolicyBundleId,
            policy.Revision);
        var registry = new LivePolicyRegistry();
        registry.Register(key, policy, StrategyActivationMode.Shadow);
        var service = BuildService(persistence, policyRegistry: registry);
        LiveTradeCandidate candidate = Candidate() with
        {
            StrategyId = TradingAgentTypeIds.StructuralConfluence,
            AgentInstance = key,
            EntrySupplyDemandZoneId = ZoneId,
            EntrySupplyDemandZoneLowerPrice = 1.0940m,
            EntrySupplyDemandZoneUpperPrice = 1.0960m,
            EntrySupplyDemandZoneState = SupplyDemandZoneState.Tested,
            EntrySupplyDemandProfileHash = "sd-profile",
            OriginatingLiquidityPoolId = Guid.Parse("88888888-1111-2222-3333-444444444444"),
            OriginatingLiquiditySweepId = Guid.Parse("77777777-1111-2222-3333-444444444444"),
            OriginatingLiquidityProfileHash = "liquidity-profile",
            EntrySupplyDemandManagementEnabled = true,
            StructuralManagementPolicyRevision = "structural-confluence-v1"
        };

        ShadowAdmissionResult admission = await service.AdmitAsync(
            candidate, StrategyActivationMode.Shadow, Quote(), CancellationToken.None);
        await service.OnAnalysisAsync(InvalidatedZoneUpdate(), Quote(), CancellationToken.None);

        ShadowTradeOutcome outcome = persistence.Payloads.OfType<ShadowTradeOutcome>().Single();
        ShadowTradeManagementEvaluation evaluation = persistence.Payloads
            .OfType<ShadowTradeManagementEvaluation>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(admission.Position!.AgentInstance, Is.EqualTo(key));
            Assert.That(admission.Position.OriginatingLiquidityPoolId,
                Is.EqualTo(candidate.OriginatingLiquidityPoolId));
            Assert.That(outcome.ExitReason, Is.EqualTo(ShadowExitReason.StructuralInvalidation));
            Assert.That(outcome.Position.EntrySupplyDemandZoneId, Is.EqualTo(ZoneId));
            Assert.That(service.Snapshot.OpenPositionCount, Is.Zero);
            Assert.That(evaluation.Scope, Is.EqualTo(TradeManagementEvaluationScope.Thesis));
            Assert.That(evaluation.Recommendation.ExitReason,
                Is.EqualTo(TradeManagementExitReason.EntrySupplyDemandZoneInvalidated));
            Assert.That(evaluation.Applied, Is.True);
            Assert.That(evaluation.ConfigurationHash, Is.EqualTo("structural-shadow-test"));
        });
    }

    private static LiveShadowOutcomeService BuildService(
        RecordingPersistence persistence,
        decimal entrySlippageBasisPoints = 0m,
        ILivePolicyRegistry? policyRegistry = null) => new(
        new LiveShadowOutcomeOptions
        {
            EntrySlippageBasisPoints = entrySlippageBasisPoints,
            ExitSlippageBasisPoints = 0m,
            CommissionBasisPointsPerSide = 0m,
            FinancingBasisPointsPerDay = 0m
        },
        persistence,
        new FakeTimeProvider(Now),
        policyRegistry);

    private static LiveTradeCandidate Candidate() => Phase3TestData.Candidate() with
    {
        SuggestedQuantity = 1_000m,
        PlaybookId = "breakout-retest",
        PlaybookVersion = "1.0",
        PolicyBundleId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        PolicyRevision = 7,
        AnalysisProfileHash = "profile-hash"
    };

    private static LiveQuoteSnapshot Quote() => new()
    {
        Instrument = Phase3TestData.Instrument,
        Bid = 1.1000m,
        Ask = 1.1002m,
        BrokerTime = Now,
        ReceivedAt = Now,
        IsTradeable = true,
        IsStale = false
    };

    private static MarketAnalysisUpdate InvalidatedZoneUpdate()
    {
        AnalysisSnapshot analysis = new()
        {
            Instrument = Phase3TestData.Instrument,
            Interval = ThesisInterval,
            AvailableAt = Now,
            Version = 12,
            LatestCandle = new Candle
            {
                Instrument = Phase3TestData.Instrument,
                Interval = ThesisInterval,
                OpenTime = Now.AddHours(-1),
                CloseTime = Now,
                Prices = new Ohlc(1.0990m, 1.1010m, 1.0980m, 1.1000m),
                Volume = new MarketVolume(100m, VolumeKind.Unknown),
                IsComplete = true
            },
            Indicators = new IndicatorSnapshot { Atr = 0.0010m },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            SupplyDemand = new SupplyDemandAnalysisSnapshot
            {
                IsEnabled = true,
                ProfileHash = "sd-profile",
                SnapshotVersion = 12,
                AvailableAt = Now,
                Zones = [],
                ActiveZones = [],
                RecentEvents =
                [
                    new SupplyDemandZoneEvent
                    {
                        EventId = Guid.Parse("66666666-1111-2222-3333-444444444444"),
                        ZoneId = ZoneId,
                        EventType = SupplyDemandZoneEventType.Invalidated,
                        StateBefore = SupplyDemandZoneState.Tested,
                        StateAfter = SupplyDemandZoneState.Invalidated,
                        Price = 1.0940m,
                        OccurredAt = Now,
                        AvailableAt = Now,
                        SnapshotVersion = 12
                    }
                ],
                Quality = new SupplyDemandAnalysisQuality
                {
                    AtrReady = true,
                    ActiveZoneCount = 0,
                    SuppressedCandidateCount = 0,
                    LastEvaluatedAt = Now
                }
            },
            Confidence = new ConfidenceScore { Total = 1m, Contributions = [] }
        };
        return new MarketAnalysisUpdate
        {
            Instrument = Phase3TestData.Instrument,
            AvailableAt = Now,
            ClosedIntervals = new HashSet<BarInterval> { ThesisInterval },
            Analysis = new MultiTimeframeAnalysis(
                Phase3TestData.Instrument,
                Now,
                new Dictionary<BarInterval, AnalysisSnapshot> { [ThesisInterval] = analysis }),
            Health = new MarketDataHealthSnapshot
            {
                Instrument = Phase3TestData.Instrument,
                State = LiveMarketState.Ready,
                AsOf = Now,
                IsQuoteStale = false,
                ProcessedCandleCount = 1,
                DuplicateCandleCount = 0,
                GapDetectedCount = 0,
                RecentIssues = []
            },
            MarketSequence = 12
        };
    }

    private sealed class RecordingPersistence : ILiveTradingPersistence
    {
        public List<string> Streams { get; } = [];
        public List<object> Payloads { get; } = [];

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask AppendAsync<T>(
            string streamName,
            T payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Streams.Add(streamName);
            Payloads.Add(payload!);
            return ValueTask.CompletedTask;
        }

        public ValueTask SaveCheckpointAsync(
            LiveEngineCheckpoint checkpoint,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public Task<LiveEngineCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken) =>
            Task.FromResult<LiveEngineCheckpoint?>(null);
    }
}
