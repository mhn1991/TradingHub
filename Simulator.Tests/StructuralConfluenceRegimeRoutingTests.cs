using Agent.Models;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralConfluenceRegimeRoutingTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Trigger = BarInterval.Minutes(5);
    private static readonly BarInterval Setup = BarInterval.Minutes(15);
    private static readonly BarInterval Context = BarInterval.Hours(1);
    private static readonly DateTimeOffset At = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RoutingEnabled_Compression_BlocksStructuralEntryAndPersistsPolicy()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        RecordingPlaybook[] playbooks = AllReadyPlaybooks();
        var agent = new StructuralConfluenceAgent(options, playbooks);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Compression())));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe), decision.Reason);
            Assert.That(decision.ReasonCode, Is.EqualTo("RegimeBlocked:Compression"));
            Assert.That(decision.RegimeLabel, Is.EqualTo(MarketRegime.Compression));
            Assert.That(decision.RegimeConfidence, Is.EqualTo(80m));
            Assert.That(decision.RegimePolicyId, Is.EqualTo(nameof(MarketRegime.Compression)));
            Assert.That(decision.RegimeRiskMultiplier, Is.Zero);
            Assert.That(playbooks, Has.All.Property(nameof(RecordingPlaybook.EvaluationCount)).EqualTo(1));
            Assert.That(decision.StructuralPlaybookDiagnostics, Has.Count.EqualTo(4));
            Assert.That(decision.StructuralPlaybookDiagnostics,
                Has.All.Property(nameof(StructuralPlaybookDiagnostic.Outcome))
                    .EqualTo(StructuralPlaybookOutcome.EntryBlocked));
            Assert.That(decision.StructuralPlaybookDiagnostics,
                Has.All.Property(nameof(StructuralPlaybookDiagnostic.IsEntryEligible)).False);
        });
    }

    [TestCase(MarketRegime.TrendingUp, IndicatorConfluencePlaybook.StableId, 2)]
    [TestCase(MarketRegime.Range, LiquiditySweepReversalPlaybook.StableId, 1)]
    [TestCase(MarketRegime.BreakoutExpansionUp, LiquidityBreakRetestPlaybook.StableId, 1)]
    [TestCase(MarketRegime.BreakoutExpansionDown, LiquidityBreakRetestPlaybook.StableId, 1)]
    public async Task RoutingEnabled_EntryProfileSelectsOnlyEligiblePlaybooks(
        MarketRegime regime,
        string expectedPlaybookId,
        int expectedEligibleCount)
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        RecordingPlaybook[] playbooks = AllReadyPlaybooks();
        var agent = new StructuralConfluenceAgent(options, playbooks);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(regime))));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
            Assert.That(decision.PlaybookId, Is.EqualTo(expectedPlaybookId));
            Assert.That(playbooks, Has.All.Property(nameof(RecordingPlaybook.EvaluationCount)).EqualTo(1));
            Assert.That(decision.StructuralPlaybookDiagnostics.Count(item => item.IsEntryEligible),
                Is.EqualTo(expectedEligibleCount));
            Assert.That(decision.StructuralPlaybookDiagnostics.Single(item => item.IsSelected).PlaybookId,
                Is.EqualTo(expectedPlaybookId));
            Assert.That(decision.StructuralPlaybookDiagnostics
                    .Where(item => !item.IsEntryEligible)
                    .Select(item => item.Outcome),
                Has.All.EqualTo(StructuralPlaybookOutcome.RoutedOut));
        });
    }

    [Test]
    public async Task RoutingEnabled_RangeBoundaryRemainsExclusiveToSweepDespiteLegacyFlag()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true },
            SupplyDemandPullback = new SupplyDemandPullbackOptions
            {
                AllowRangeBoundaryContext = true
            }
        };
        RecordingPlaybook[] playbooks = AllReadyPlaybooks();
        var agent = new StructuralConfluenceAgent(options, playbooks);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(MarketRegime.Range))));

        Assert.Multiple(() =>
        {
            Assert.That(decision.PlaybookId, Is.EqualTo(LiquiditySweepReversalPlaybook.StableId));
            Assert.That(decision.StructuralPlaybookDiagnostics.Count(item => item.IsEntryEligible), Is.EqualTo(1));
        });
    }

    [TestCase(MarketRegime.TrendingUp, PriceActionDirection.Bullish)]
    [TestCase(MarketRegime.TrendingDown, PriceActionDirection.Bearish)]
    public async Task RoutingEnabled_MatureBreakRetestIsRoutedOutOfTrendTransitionByDefault(
        MarketRegime trendRegime,
        PriceActionDirection alignedDirection)
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        PlaybookEvaluation matureBreakRetest = ReadyEvaluation(
            LiquidityBreakRetestPlaybook.StableId,
            95m) with
        {
            Direction = alignedDirection,
            PrimaryPoolId = Guid.Parse("8ec80e4b-221b-4afe-a6d2-7d0b06b2115f"),
            SupportingEvidence = ["AcceptedLiquidityBreak", "LiquidityRetestHeld"]
        };
        RecordingPlaybook[] playbooks =
        [
            new(matureBreakRetest.PlaybookId, matureBreakRetest),
            new(SupplyDemandPullbackPlaybook.StableId,
                ReadyEvaluation(SupplyDemandPullbackPlaybook.StableId, 70m)),
            new(IndicatorConfluencePlaybook.StableId,
                ReadyEvaluation(IndicatorConfluencePlaybook.StableId, 90m))
        ];
        var agent = new StructuralConfluenceAgent(options, playbooks);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(trendRegime))));

        StructuralPlaybookDiagnostic diagnostic = decision.StructuralPlaybookDiagnostics
            .Single(item => item.PlaybookId == LiquidityBreakRetestPlaybook.StableId);
        Assert.Multiple(() =>
        {
            Assert.That(decision.PlaybookId, Is.Not.EqualTo(LiquidityBreakRetestPlaybook.StableId));
            Assert.That(diagnostic.IsEntryEligible, Is.False);
            Assert.That(diagnostic.Outcome, Is.EqualTo(StructuralPlaybookOutcome.RoutedOut));
        });
    }

    [Test]
    public async Task RoutingEnabled_MatureBreakRetestSurvivesTrendTransitionWhenExplicitlyAllowed()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true },
            AllowBreakRetestAfterBreakoutTransition = true
        };
        PlaybookEvaluation matureBreakRetest = ReadyEvaluation(
            LiquidityBreakRetestPlaybook.StableId,
            95m) with
        {
            PrimaryPoolId = Guid.Parse("8ec80e4b-221b-4afe-a6d2-7d0b06b2115f"),
            SupportingEvidence = ["AcceptedLiquidityBreak", "LiquidityRetestHeld"]
        };
        RecordingPlaybook[] playbooks =
        [
            new(matureBreakRetest.PlaybookId, matureBreakRetest),
            new(SupplyDemandPullbackPlaybook.StableId,
                ReadyEvaluation(SupplyDemandPullbackPlaybook.StableId, 70m)),
            new(IndicatorConfluencePlaybook.StableId,
                ReadyEvaluation(IndicatorConfluencePlaybook.StableId, 90m))
        ];
        var agent = new StructuralConfluenceAgent(options, playbooks);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(MarketRegime.TrendingUp))));

        StructuralPlaybookDiagnostic diagnostic = decision.StructuralPlaybookDiagnostics
            .Single(item => item.PlaybookId == LiquidityBreakRetestPlaybook.StableId);
        Assert.Multiple(() =>
        {
            Assert.That(decision.PlaybookId, Is.EqualTo(LiquidityBreakRetestPlaybook.StableId));
            Assert.That(diagnostic.IsEntryEligible, Is.True);
            Assert.That(diagnostic.Outcome, Is.EqualTo(StructuralPlaybookOutcome.Selected));
        });
    }

    [Test]
    public async Task RoutingEnabled_MatureBreakRetestCannotCrossIntoOpposingTrend()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = true }
        };
        PlaybookEvaluation matureBearishBreakRetest = ReadyEvaluation(
            LiquidityBreakRetestPlaybook.StableId,
            95m) with
        {
            Direction = PriceActionDirection.Bearish,
            PrimaryPoolId = Guid.Parse("e209de94-4a37-45f0-a69f-ac40f94e87fb"),
            SupportingEvidence = ["AcceptedLiquidityBreak", "LiquidityRetestHeld"]
        };
        RecordingPlaybook[] playbooks =
        [
            new(matureBearishBreakRetest.PlaybookId, matureBearishBreakRetest),
            new(IndicatorConfluencePlaybook.StableId,
                ReadyEvaluation(IndicatorConfluencePlaybook.StableId, 90m))
        ];
        var agent = new StructuralConfluenceAgent(options, playbooks);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(MarketRegime.TrendingUp))));

        StructuralPlaybookDiagnostic diagnostic = decision.StructuralPlaybookDiagnostics
            .Single(item => item.PlaybookId == LiquidityBreakRetestPlaybook.StableId);
        Assert.Multiple(() =>
        {
            Assert.That(decision.PlaybookId, Is.EqualTo(IndicatorConfluencePlaybook.StableId));
            Assert.That(diagnostic.IsEntryEligible, Is.False);
            Assert.That(diagnostic.Outcome, Is.EqualTo(StructuralPlaybookOutcome.RoutedOut));
        });
    }

    [Test]
    public async Task RoutingDisabled_AllPlaybooksRemainEntryEligible()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = false }
        };
        var agent = new StructuralConfluenceAgent(options, AllReadyPlaybooks());

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(MarketRegime.Range))));

        Assert.Multiple(() =>
        {
            Assert.That(decision.PlaybookId, Is.EqualTo(IndicatorConfluencePlaybook.StableId));
            Assert.That(decision.StructuralPlaybookDiagnostics,
                Has.All.Property(nameof(StructuralPlaybookDiagnostic.IsEntryEligible)).EqualTo(true));
            Assert.That(decision.RegimeEntryProfileId, Is.Null);
        });
    }

    [TestCase(true, 4)]
    [TestCase(false, 3)]
    public async Task ConstructedPlaybooks_RespectIndicatorConfluenceEnabledOption(
        bool indicatorConfluenceEnabled,
        int expectedPlaybookCount)
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            IndicatorConfluence = new IndicatorConfluenceOptions { Enabled = indicatorConfluenceEnabled }
        };
        // Single-arg constructor: exercises the real CreatePlaybooks factory instead of the
        // RecordingPlaybook fixtures used elsewhere in this file, so the assertion actually
        // proves the disabled playbook is absent from the constructed collection - not just
        // routed out afterward.
        var agent = new StructuralConfluenceAgent(options);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context)));

        Assert.Multiple(() =>
        {
            Assert.That(decision.StructuralPlaybookDiagnostics, Has.Count.EqualTo(expectedPlaybookCount));
            Assert.That(
                decision.StructuralPlaybookDiagnostics.Any(item => item.PlaybookId == IndicatorConfluencePlaybook.StableId),
                Is.EqualTo(indicatorConfluenceEnabled));
        });
    }

    [Test]
    public async Task Diagnostics_RecordEveryDistinctFailedMandatoryGate()
    {
        StructuralConfluenceStrategyOptions options = new()
        {
            TriggerInterval = Trigger,
            SetupInterval = Setup,
            ContextInterval = Context,
            MarketRegime = new MarketRegimePolicyOptions { Enabled = false }
        };
        PlaybookEvaluation evaluation = ReadyEvaluation(
            LiquidityBreakRetestPlaybook.StableId,
            50m) with
        {
            Lifecycle = StructuralSetupLifecycle.AwaitingTrigger,
            IsReady = false,
            Geometry = null,
            ReasonCode = "FirstGateFailed",
            MandatoryGates =
            [
                new MandatoryGate("first", false, 0m, "FirstGateFailed"),
                new MandatoryGate("passed", true, 100m, "PassedGate"),
                new MandatoryGate("duplicate", false, 0m, "FirstGateFailed"),
                new MandatoryGate("second", false, 0m, "SecondGateFailed")
            ]
        };
        var agent = new StructuralConfluenceAgent(
            options,
            [new RecordingPlaybook(evaluation.PlaybookId, evaluation)]);

        AgentDecision decision = await agent.EvaluateAsync(MarketContext(
            Snapshot(Trigger),
            Snapshot(Setup),
            Snapshot(Context, Regime(MarketRegime.BreakoutExpansionUp))));

        StructuralPlaybookDiagnostic diagnostic = decision.StructuralPlaybookDiagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Outcome, Is.EqualTo(StructuralPlaybookOutcome.NotReady));
            Assert.That(diagnostic.Direction, Is.EqualTo(evaluation.Direction));
            Assert.That(diagnostic.Confidence, Is.EqualTo(evaluation.Confidence));
            Assert.That(diagnostic.MandatoryQualityFloor, Is.EqualTo(evaluation.MandatoryQualityFloor));
            Assert.That(diagnostic.SupportingEvidence, Is.EqualTo(evaluation.SupportingEvidence));
            Assert.That(diagnostic.ConflictingEvidence, Is.EqualTo(evaluation.ConflictingEvidence));
            Assert.That(
                diagnostic.FailedGateReasonCodes,
                Is.EqualTo(new[] { "FirstGateFailed", "SecondGateFailed" }));
            Assert.That(diagnostic.PrimaryBlockingReasonCode, Is.EqualTo("FirstGateFailed"));
        });
    }

    [Test]
    public void StateStore_ReadyCandidateIsConsumedOnlyWhenSelected()
    {
        var store = new PlaybookStateStore();
        PlaybookEvaluation evaluation = ReadyEvaluation(IndicatorConfluencePlaybook.StableId, 90m);

        Assert.That(store.TryAdvance(Instrument, evaluation.PlaybookId, At, 1, evaluation, false), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(store.Get(Instrument, evaluation.PlaybookId).LastEvaluation, Is.SameAs(evaluation));
            Assert.That(store.Get(Instrument, evaluation.PlaybookId).LastReadySetupId, Is.Null);
            Assert.That(store.Get(Instrument, evaluation.PlaybookId).LastReadyCatalystAt, Is.Null);
        });

        Assert.That(store.TryAdvance(Instrument, evaluation.PlaybookId, At.AddMinutes(5), 2, evaluation, true), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(store.Get(Instrument, evaluation.PlaybookId).LastReadySetupId, Is.EqualTo(evaluation.SetupId));
            Assert.That(store.Get(Instrument, evaluation.PlaybookId).LastReadyCatalystAt, Is.EqualTo(evaluation.CatalystAt));
        });
    }

    [Test]
    public void StateStore_PreservesArmedAtWhileSameSetupAdvancesThroughLifecycle()
    {
        var store = new PlaybookStateStore();
        PlaybookEvaluation armed = ReadyEvaluation(LiquiditySweepReversalPlaybook.StableId, 70m) with
        {
            Lifecycle = StructuralSetupLifecycle.Armed,
            IsReady = false,
            CatalystAt = null,
            Geometry = null,
            ReasonCode = "StructuralAwaitingLiquiditySweep"
        };
        PlaybookEvaluation catalyst = armed with
        {
            Lifecycle = StructuralSetupLifecycle.CatalystObserved,
            CatalystAt = At.AddMinutes(5),
            ReasonCode = "StructuralReversalShiftMissing"
        };

        Assert.That(store.TryAdvance(Instrument, armed.PlaybookId, At, 1, armed), Is.True);
        Assert.That(store.TryAdvance(
            Instrument, catalyst.PlaybookId, At.AddMinutes(5), 2, catalyst), Is.True);

        PlaybookRuntimeState state = store.Get(Instrument, armed.PlaybookId);
        Assert.Multiple(() =>
        {
            Assert.That(state.SetupId, Is.EqualTo(armed.SetupId));
            Assert.That(state.ArmedAt, Is.EqualTo(At));
            Assert.That(state.Lifecycle, Is.EqualTo(StructuralSetupLifecycle.CatalystObserved));
        });
    }

    [Test]
    public void ResolveAgentDefinition_CopiesRuntimeRegimeRoutingIntoDefaultAndOverride()
    {
        var routing = new MarketRegimePolicyOptions
        {
            Enabled = true,
            MinimumRegimeConfidence = 55m
        };
        var request = new BacktestRequest
        {
            Instrument = Instrument,
            From = At.AddDays(-1),
            To = At,
            Strategies = [TradingAgentTypeIds.StructuralConfluence],
            Runtime = new BacktestRuntimeOptions { MarketRegimeRouting = routing }
        };
        var definitionOverride = new TradingAgentDefinition
        {
            Kind = TradingAgentKind.StructuralConfluence,
            StructuralConfluence = new StructuralConfluenceStrategyOptions()
        };

        TradingAgentDefinition defaultDefinition =
            request.ResolveAgentDefinition(TradingAgentTypeIds.StructuralConfluence);
        TradingAgentDefinition overlaidDefinition =
            request.ResolveAgentDefinition(TradingAgentTypeIds.StructuralConfluence, definitionOverride);

        Assert.Multiple(() =>
        {
            Assert.That(defaultDefinition.StructuralConfluence!.MarketRegime, Is.SameAs(routing));
            Assert.That(overlaidDefinition.StructuralConfluence!.MarketRegime, Is.SameAs(routing));
        });
    }

    private static MarketRegimeSnapshot Compression() => new()
    {
        Regime = MarketRegime.Compression,
        Confidence = 80m,
        ConfirmedAt = At,
        AgeCandles = 3,
        Contributions = [],
        ReasonCode = "TestCompression",
        IsTradeable = true
    };

    private static MarketRegimeSnapshot Regime(MarketRegime regime) => new()
    {
        Regime = regime,
        Confidence = 80m,
        ConfirmedAt = At,
        AgeCandles = 3,
        Contributions = [],
        ReasonCode = $"Test{regime}",
        IsTradeable = true
    };

    private static RecordingPlaybook[] AllReadyPlaybooks() =>
    [
        new(LiquidityBreakRetestPlaybook.StableId, ReadyEvaluation(LiquidityBreakRetestPlaybook.StableId, 50m)),
        new(LiquiditySweepReversalPlaybook.StableId, ReadyEvaluation(LiquiditySweepReversalPlaybook.StableId, 60m)),
        new(SupplyDemandPullbackPlaybook.StableId, ReadyEvaluation(SupplyDemandPullbackPlaybook.StableId, 70m)),
        new(IndicatorConfluencePlaybook.StableId, ReadyEvaluation(IndicatorConfluencePlaybook.StableId, 90m))
    ];

    private static PlaybookEvaluation ReadyEvaluation(string playbookId, decimal quality) => new()
    {
        PlaybookId = playbookId,
        Version = "test-v1",
        Direction = PriceActionDirection.Bullish,
        Lifecycle = StructuralSetupLifecycle.CandidateProduced,
        SetupId = $"setup:{playbookId}",
        CatalystAt = At,
        MandatoryGates = [new MandatoryGate("test", true, quality, "TestGatePassed")],
        GeometryQuality = quality,
        Confidence = quality,
        ReasonCode = "TestCandidateReady",
        IsReady = true,
        Geometry = new StructuralGeometry
        {
            IsValid = true,
            Entry = 1m,
            Stop = 0.99m,
            Target = 1.02m,
            RewardRisk = 2m,
            Quality = quality,
            ReasonCode = "TestGeometryValid"
        }
    };

    private static AnalysisSnapshot Snapshot(
        BarInterval interval,
        MarketRegimeSnapshot? regime = null) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = At,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument,
            At.AddSeconds(-BarIntervalParser.ApproximateSeconds(interval)),
            interval,
            1m,
            1.01m,
            0.99m,
            1m),
        Indicators = new IndicatorSnapshot { Atr = 0.01m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        MarketRegime = regime ?? MarketRegimeSnapshot.Unknown,
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };

    private static AgentMarketContext MarketContext(params AnalysisSnapshot[] snapshots) => new()
    {
        Instrument = Instrument,
        Timestamp = At,
        Analysis = new MultiTimeframeAnalysis(
            Instrument,
            At,
            snapshots.ToDictionary(snapshot => snapshot.Interval)),
        Account = new AccountSnapshot
        {
            AccountId = "test",
            Currency = "USD",
            Balance = 100_000m,
            Available = 100_000m,
            CanTrade = true
        },
        Positions = [],
        OpenOrders = []
    };

    private sealed class RecordingPlaybook(string playbookId, PlaybookEvaluation evaluation) : IStructuralPlaybook
    {
        public string PlaybookId { get; } = playbookId;
        public string Version => evaluation.Version;
        public int EvaluationCount { get; private set; }

        public PlaybookEvaluation Evaluate(
            StructuralEvidencePacket evidence,
            PlaybookRuntimeState state)
        {
            EvaluationCount++;
            return evaluation;
        }
    }
}
