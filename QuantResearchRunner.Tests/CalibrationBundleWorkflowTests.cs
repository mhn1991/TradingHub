using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearch.Training.Pipeline;
using Simulator.Calibration;
using Simulator.Models;
using Simulator.Services;
using TradingPolicies;

namespace QuantResearchRunner.Tests;

/// <summary>
/// Exercises the Phase 2 promotion/approval workflow: a successful training run must produce
/// only a PendingReview candidate (never an auto-approved profile), and only an explicit
/// ApproveAsync call materializes a real, persisted, ApprovedForDemo <see cref="TradingPolicyProfile"/>.
/// </summary>
[TestFixture]
public sealed class CalibrationBundleWorkflowTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RunAndProposeAsync_SuccessfulTraining_ProducesPendingReviewCandidateOnly()
    {
        (CalibrationBundleWorkflow workflow, ICalibrationBundleApprovalStore approvals, ITradingPolicyProfileStore profiles, _) = Build();

        CalibrationBundlePromotionResult result = await workflow.RunAndProposeAsync(BasePromotionRequest());

        Assert.That(result.Training.Success, Is.True, result.Training.FailureReason);
        Assert.That(result.Candidate, Is.Not.Null);
        Assert.That(result.Candidate!.Status, Is.EqualTo(CalibrationBundleCandidateStatus.PendingReview));
        Assert.That(result.Candidate.ProposedProfile.Status, Is.EqualTo(TradingPolicyProfileStatus.Research));

        // Nothing is approved/persisted yet - only the pending candidate exists.
        IReadOnlyList<TradingPolicyProfile> persisted = await profiles.ListAsync();
        Assert.That(persisted, Is.Empty);
    }

    [Test]
    public async Task ApproveAsync_MaterializesApprovedForDemoProfile_AtRevisionOne()
    {
        (CalibrationBundleWorkflow workflow, ICalibrationBundleApprovalStore approvals, ITradingPolicyProfileStore profiles, _) = Build();
        CalibrationBundlePromotionResult result = await workflow.RunAndProposeAsync(BasePromotionRequest());
        Guid candidateId = result.Candidate!.Id;

        CalibrationBundleCandidate approved = await approvals.ApproveAsync(candidateId, "alice");

        Assert.That(approved.Status, Is.EqualTo(CalibrationBundleCandidateStatus.Approved));
        Assert.That(approved.ApprovedProfileRevision, Is.EqualTo(1));

        TradingPolicyProfile? persisted = await profiles.GetAsync(approved.ApprovedProfileId!.Value, approved.ApprovedProfileRevision!.Value);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.Status, Is.EqualTo(TradingPolicyProfileStatus.ApprovedForDemo));

        TradingPolicyProfile? latest = await profiles.GetLatestApprovedAsync("improved");
        Assert.That(latest, Is.Not.Null);
        Assert.That(latest!.Revision, Is.EqualTo(1));
    }

    [Test]
    public async Task ApproveAsync_SecondBundleForSameStrategy_GetsRevisionTwo()
    {
        (CalibrationBundleWorkflow workflow, ICalibrationBundleApprovalStore approvals, ITradingPolicyProfileStore profiles, _) = Build();

        CalibrationBundlePromotionResult first = await workflow.RunAndProposeAsync(BasePromotionRequest());
        await approvals.ApproveAsync(first.Candidate!.Id, "alice");

        CalibrationBundlePromotionResult second = await workflow.RunAndProposeAsync(BasePromotionRequest());
        CalibrationBundleCandidate secondApproved = await approvals.ApproveAsync(second.Candidate!.Id, "bob");

        Assert.That(secondApproved.ApprovedProfileRevision, Is.EqualTo(2));
    }

    [Test]
    public void ApproveAsync_ANonPendingCandidate_Throws()
    {
        (CalibrationBundleWorkflow workflow, ICalibrationBundleApprovalStore approvals, _, _) = Build();

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await approvals.ApproveAsync(Guid.NewGuid(), "alice"));
    }

    [Test]
    public async Task RejectAsync_LeavesNoProfilePersisted()
    {
        (CalibrationBundleWorkflow workflow, ICalibrationBundleApprovalStore approvals, ITradingPolicyProfileStore profiles, _) = Build();
        CalibrationBundlePromotionResult result = await workflow.RunAndProposeAsync(BasePromotionRequest());

        CalibrationBundleCandidate rejected = await approvals.RejectAsync(result.Candidate!.Id, "alice", "insufficient sample size");

        Assert.That(rejected.Status, Is.EqualTo(CalibrationBundleCandidateStatus.Rejected));
        Assert.That(await profiles.ListAsync(), Is.Empty);

        // Rejecting again must fail - it's no longer PendingReview.
        Assert.ThrowsAsync<InvalidOperationException>(async () => await approvals.RejectAsync(result.Candidate.Id, "bob", "duplicate"));
    }

    [Test]
    public async Task RunAndProposeAsync_DerivesAgentOptions_FromTrainingRuntime_NotBareDefaults()
    {
        // AGENT-01 regression: the promoted profile's AgentOptions must match the timeframes
        // implied by Training.Runtime (the same settings that produced the training data), not
        // a bare `new ProgressiveStrategyOptions()` default. RecommendedSimulationDefaults'
        // TrendInterval (2h) deliberately differs from ProgressiveStrategyOptions' own bare
        // default (1h), so this proves the derivation is real, not coincidental.
        (CalibrationBundleWorkflow workflow, _, _, _) = Build();
        CalibrationBundlePromotionRequest request = BasePromotionRequest();

        CalibrationBundlePromotionResult result = await workflow.RunAndProposeAsync(request);

        Assert.That(result.Candidate, Is.Not.Null, result.IncompatibilityReason);
        ProgressiveStrategyOptions expected = request.Training.Runtime.ResolveProgressiveStrategyOptions(
            request.Training.Quantity,
            request.Training.MinimumRewardRisk,
            request.Training.PriceActionConfirmation,
            request.Training.MinimumPriceActionConfidence,
            request.Training.RejectStrongOpposingPriceAction);
        Assert.Multiple(() =>
        {
            Assert.That(
                result.Candidate!.ProposedProfile.AgentOptions.TrendInterval,
                Is.EqualTo(expected.TrendInterval));
            Assert.That(
                result.Candidate.ProposedProfile.AgentOptions.TrendInterval,
                Is.EqualTo(BarInterval.Hours(2)),
                "RecommendedSimulationDefaults.StrategyTimeframes.TrendInterval, not ProgressiveStrategyOptions' own bare 1h default.");
            Assert.That(
                result.Candidate.ProposedProfile.AgentOptions.EntryInterval,
                Is.EqualTo(expected.EntryInterval));
        });
    }

    private static CalibrationBundlePromotionRequest BasePromotionRequest() => new()
    {
        Training = new CalibrationTrainingRequest
        {
            Instruments = [Instrument],
            Strategies = ["improved"],
            From = Start,
            To = Start.AddDays(90),
            Runtime = new BacktestRuntimeOptions(),
            Folds = 5,
            Embargo = TimeSpan.FromHours(6)
        },
        AgentKind = ProgressiveAgentKind.Improved,
        StrategyVersion = "improved-v1"
    };

    private static (CalibrationBundleWorkflow Workflow, ICalibrationBundleApprovalStore Approvals, ITradingPolicyProfileStore Profiles, FakeBacktestApplicationService Backtests) Build()
    {
        var backtests = new FakeBacktestApplicationService(_ => BuildTrades(80));
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        var profiles = new FileTradingPolicyProfileStore(TempDirectory());
        var approvals = new FileCalibrationBundleApprovalStore(TempDirectory(), artifacts, profiles);
        var pipeline = new CalibrationTrainingPipeline(backtests, artifacts);
        var workflow = new CalibrationBundleWorkflow(pipeline, artifacts, approvals);
        return (workflow, approvals, profiles, backtests);
    }

    private static IReadOnlyList<SimulatedTradeRecord> BuildTrades(int count)
    {
        var trades = new List<SimulatedTradeRecord>();
        var random = new Random(7);
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset opened = Start.AddDays(i);
            trades.Add(new SimulatedTradeRecord
            {
                StrategyId = "improved",
                StrategyName = "Improved Progressive",
                SetupId = $"setup-{i:D4}",
                PositionId = $"position-{i:D4}",
                Instrument = Instrument,
                Side = OrderSide.Buy,
                SetupStartedAt = opened,
                SignalCreatedAt = opened,
                OpenedAt = opened,
                ClosedAt = opened.AddHours(4),
                EntryRegime = MarketRegime.TrendingUp,
                EntryConfidence = 40m + (i % 12) * 5m,
                EntryMultiTimeframeAlignment = 0.5m,
                EntrySetupType = "Breakout",
                EntrySession = "London",
                EntryVolatilityBucket = "Normal",
                RMultiple = (decimal)(random.NextDouble() * 3 - 1),
                SetupReason = "fixture",
                MaximumFavourableExcursionR = 0.5m,
                MaximumAdverseExcursionR = -0.2m,
                ExcursionPath =
                [
                    new SimulatedTradePathPoint { BarsAfterEntry = 0, MfeR = 0.1m, MaeR = -0.1m },
                    new SimulatedTradePathPoint { BarsAfterEntry = 1, MfeR = 0.3m, MaeR = -0.15m }
                ]
            });
        }
        return trades;
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", "bundle-workflow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeBacktestApplicationService(Func<BacktestRequest, IReadOnlyList<SimulatedTradeRecord>> trades)
        : IBacktestApplicationService
    {
        public Task<ComparativeSimulationResult> RunToCompletionAsync(
            BacktestRequest request, IProgress<BacktestProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SimulatedTradeRecord> tradeList = trades(request);
            var result = new ComparativeSimulationResult
            {
                SimulationId = Guid.NewGuid(),
                InputStreamId = "fake",
                InputHash = "fake-hash",
                DataQuality = new MarketDataQualityReport
                {
                    CandleCount = tradeList.Count,
                    DuplicateCount = 0,
                    OutOfOrderCount = 0,
                    MissingIntervalCount = 0,
                    WeekendGapCount = 0,
                    SessionGapCount = 0,
                    IncompleteAggregateCount = 0,
                    FirstCandle = request.From,
                    LastCandle = request.To,
                    InputHash = "fake-hash"
                },
                Strategies =
                [
                    new StrategySimulationResult
                    {
                        StrategyId = request.Strategies[0],
                        StrategyName = request.Strategies[0],
                        Result = new SimulationResult
                        {
                            StartedAt = request.From,
                            EndedAt = request.To,
                            StartingBalance = request.StartingBalance,
                            FinalBalance = request.StartingBalance,
                            FinalEquity = request.StartingBalance,
                            UnrealizedProfitLoss = 0m,
                            NetProfit = 0m,
                            TotalCommission = 0m,
                            SubmittedOrders = tradeList.Count,
                            FilledOrders = tradeList.Count,
                            RejectedOrders = 0,
                            OpenPositions = [],
                            Ledger = [],
                            Trades = tradeList
                        },
                        Metrics = new StrategyWorkerMetrics
                        {
                            StrategyName = request.Strategies[0],
                            ProcessedFrames = tradeList.Count,
                            TotalProcessingTime = TimeSpan.Zero,
                            MaximumFrameProcessingTime = TimeSpan.Zero,
                            AverageFrameProcessingTime = TimeSpan.Zero,
                            BarrierWaitTime = TimeSpan.Zero,
                            PeakChannelOccupancy = 0
                        }
                    }
                ],
                OutputDirectory = request.OutputDirectory,
                TotalDuration = TimeSpan.Zero,
                ProcessedBaseCandles = tradeList.Count,
                FillModel = FillModel.MidpointPlusConfiguredSpread
            };
            return Task.FromResult(result);
        }

        public Task<SimulationJobHandle> StartAsync(BacktestRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SimulationJobSnapshot?> GetAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(int take = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PauseAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResumeAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
