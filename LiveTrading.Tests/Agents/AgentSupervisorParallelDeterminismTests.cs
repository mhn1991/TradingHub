using Agent.Models;
using Brokers.Models;
using LiveTrading.Actors;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

/// <summary>
/// Multi-agent architecture Phase 6: <see cref="AgentSupervisor.ApplyAsync"/> now dispatches
/// eligible agent instances concurrently, bounded by a configurable limit, then submits any
/// resulting candidates to the epoch coordinator in fixed (not completion) order. These tests
/// prove that concurrency level and per-agent delay cannot change which candidates a decision
/// epoch ends up with, or their (coordinator-sorted) final order.
/// </summary>
[TestFixture]
public sealed class AgentSupervisorParallelDeterminismTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;

    private static LiveTradingPolicyBundle BuildBundle()
    {
        var featurePolicy = new RuntimeFeaturePolicy
        {
            AnnotationOptions = new(),
            MarketRegimeRouting = new(),
            ValueLocationEvidence = new(),
            CurrencyStrengthEvidence = new(),
            RsiBollingerSignals = new(),
            DmiConfirmationEnabled = true,
            CurrencyStrength = new(),
            SetupCalibration = new()
        };
        return new LiveTradingPolicyBundle
        {
            PolicyBundleId = Guid.NewGuid(),
            Revision = 1,
            StrategyVersion = "v1",
            FeatureSchemaHash = featurePolicy.ComputeHash(),
            FeaturePolicy = featurePolicy,
            PositionSizing = new(),
            AdaptiveRisk = new(),
            CorrelationRisk = new PortfolioManager.Correlation.CorrelationRiskOptions(),
            PortfolioRisk = new(),
            TradingConditions = new(),
            AccountSafety = new(),
            LegacyManagement = new(),
            RegimeManagement = new TradeManager.RegimeManagementOptions(),
            ImprovedManagement = new(),
            ConfigurationHash = "test-configuration-hash",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentInstanceState BuildInstance(FakeTradingAgent agent, string strategyId) => new()
    {
        Key = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, strategyId, Guid.Empty, 1),
        Runtime = new StrategyDecisionRuntime
        {
            Agent = agent,
            Pipeline = new SafeTradingPipeline(agent, new ShadowExecutionCoordinator()),
            StrategyVersion = "v1",
            FeaturePolicyHash = "hash",
            SetupCalibrationId = null,
            MetaModelVersion = null
        },
        PolicyBundle = BuildBundle(),
        Assignment = new LiveStrategyAssignment { StrategyId = strategyId, PolicyBundleId = "bundle-1" }
    };

    private static FakeTradingAgent BuildAgent(string strategyId, int delayMilliseconds) => new()
    {
        RequiredIntervals = new HashSet<BarInterval> { M1 },
        TriggerInterval = M1,
        Decide = context =>
        {
            if (delayMilliseconds > 0)
                Thread.Sleep(delayMilliseconds);
            return new AgentDecision
            {
                DecisionId = $"decision-{strategyId}",
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                Confidence = 0.75m,
                CreatedAt = context.Timestamp,
                Reason = "test buy signal",
                ReferencePrice = 1.1002m,
                StopLossPrice = 1.0950m,
                TakeProfitPrice = 1.1100m
            };
        }
    };

    private static async Task<LiveDecisionEpochBatch> RunOnceAsync(
        IReadOnlyList<string> strategyIds, int maxConcurrentEvaluations, string slowStrategyId)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var epochCoordinator = new LiveDecisionEpochCoordinator(
            [Instrument], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        var supervisor = new AgentSupervisor(
            new FakeBrokerClient(), epochCoordinator, clock, NullLogger<AgentSupervisor>.Instance,
            maxConcurrentEvaluations, TimeSpan.FromSeconds(10));

        foreach (string strategyId in strategyIds)
        {
            int delay = strategyId == slowStrategyId ? 40 : 0;
            supervisor.Register(BuildInstance(BuildAgent(strategyId, delay), strategyId));
        }

        DateTimeOffset now = clock.GetUtcNow();
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate update = AgentTestSupport.Update(
            analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 1m, CancellationToken.None);

        return await epochCoordinator.ClosedEpochs.ReadAsync();
    }

    [Test]
    public async Task ApplyAsync_SequentialAndParallelDispatch_ProduceIdenticalOrderedCandidates()
    {
        string[] strategyIds = ["strategy-a", "strategy-b", "strategy-c", "strategy-d", "strategy-e"];

        LiveDecisionEpochBatch sequentialBatch = await RunOnceAsync(strategyIds, maxConcurrentEvaluations: 1, slowStrategyId: "strategy-c");
        LiveDecisionEpochBatch parallelBatch = await RunOnceAsync(strategyIds, maxConcurrentEvaluations: 4, slowStrategyId: "strategy-c");

        string[] sequentialIds = sequentialBatch.OrderedCandidates.Select(c => c.DecisionId).ToArray();
        string[] parallelIds = parallelBatch.OrderedCandidates.Select(c => c.DecisionId).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(parallelIds, Has.Length.EqualTo(strategyIds.Length));
            Assert.That(parallelIds, Is.EqualTo(sequentialIds),
                "A slow agent's candidate must still land in the same sorted position regardless of dispatch concurrency.");
        });
    }

    [Test]
    public async Task ApplyAsync_RepeatedParallelRuns_AreDeterministic()
    {
        string[] strategyIds = ["strategy-a", "strategy-b", "strategy-c", "strategy-d", "strategy-e"];

        LiveDecisionEpochBatch first = await RunOnceAsync(strategyIds, maxConcurrentEvaluations: 4, slowStrategyId: "strategy-a");
        LiveDecisionEpochBatch second = await RunOnceAsync(strategyIds, maxConcurrentEvaluations: 4, slowStrategyId: "strategy-e");

        string[] firstIds = first.OrderedCandidates.Select(c => c.DecisionId).ToArray();
        string[] secondIds = second.OrderedCandidates.Select(c => c.DecisionId).ToArray();

        Assert.That(secondIds, Is.EqualTo(firstIds),
            "Which agent happens to be slow must not change the final deterministic ordering.");
    }
}
