using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearch.Training.Pipeline;
using Simulator.Calibration;
using Simulator.Models;
using Simulator.Services;
using TradingPolicies;

namespace QuantResearchRunner.Tests;

/// <summary>
/// Exercises the Phase 4 automatic-retraining trigger rules: disabled policies never run, an
/// absent or stale approved profile triggers a retrain, a fresh one doesn't, ForceRetrainOnStartup
/// overrides freshness, and a training failure never throws out of the scheduler (it must not be
/// able to take the host down).
/// </summary>
[TestFixture]
public sealed class LiveCalibrationTrainingSchedulerTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RunDueRetrainingAsync_DisabledPolicy_NeverTrains()
    {
        var backtests = new CountingFakeBacktestApplicationService(_ => BuildTrades(80));
        (LiveCalibrationTrainingScheduler scheduler, _, _) = Build(backtests, [BasePolicy() with { Enabled = false }]);

        await scheduler.RunDueRetrainingAsync(CancellationToken.None);

        Assert.That(backtests.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task RunDueRetrainingAsync_NoPriorApprovedProfile_Trains()
    {
        var backtests = new CountingFakeBacktestApplicationService(_ => BuildTrades(80));
        (LiveCalibrationTrainingScheduler scheduler, ICalibrationBundleApprovalStore approvals, _) = Build(backtests, [BasePolicy()]);

        await scheduler.RunDueRetrainingAsync(CancellationToken.None);

        Assert.That(backtests.CallCount, Is.GreaterThan(0));
        IReadOnlyList<CalibrationBundleCandidate> pending = await approvals.ListAsync(CalibrationBundleCandidateStatus.PendingReview);
        Assert.That(pending, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RunDueRetrainingAsync_FreshApprovedProfile_Skips()
    {
        var backtests = new CountingFakeBacktestApplicationService(_ => BuildTrades(80));
        (LiveCalibrationTrainingScheduler scheduler, ICalibrationBundleApprovalStore approvals, ITradingPolicyProfileStore profiles) =
            Build(backtests, [BasePolicy() with { MinimumArtifactAgeBeforeRetrain = TimeSpan.FromDays(7) }]);

        // Seed one already-approved, recent profile.
        await scheduler.RunDueRetrainingAsync(CancellationToken.None);
        int firstCallCount = backtests.CallCount;
        CalibrationBundleCandidate candidate = (await approvals.ListAsync(CalibrationBundleCandidateStatus.PendingReview)).Single();
        await approvals.ApproveAsync(candidate.Id, "alice");

        await scheduler.RunDueRetrainingAsync(CancellationToken.None);

        Assert.That(backtests.CallCount, Is.EqualTo(firstCallCount), "A fresh approved profile must not trigger another retrain.");
    }

    [Test]
    public async Task RunDueRetrainingAsync_ForceRetrainOnStartup_IgnoresFreshness()
    {
        var backtests = new CountingFakeBacktestApplicationService(_ => BuildTrades(80));
        CalibrationRetrainingPolicy policy = BasePolicy() with { MinimumArtifactAgeBeforeRetrain = TimeSpan.FromDays(365) };
        (LiveCalibrationTrainingScheduler scheduler, ICalibrationBundleApprovalStore approvals, _) = Build(backtests, [policy]);

        await scheduler.RunDueRetrainingAsync(CancellationToken.None);
        int firstCallCount = backtests.CallCount;
        CalibrationBundleCandidate candidate = (await approvals.ListAsync(CalibrationBundleCandidateStatus.PendingReview)).Single();
        await approvals.ApproveAsync(candidate.Id, "alice");

        // Same scheduler, but this policy instance forces a retrain regardless of freshness.
        (LiveCalibrationTrainingScheduler forcedScheduler, _, _) = Build(
            backtests, [policy with { ForceRetrainOnStartup = true }], approvals);

        await forcedScheduler.RunDueRetrainingAsync(CancellationToken.None);

        Assert.That(backtests.CallCount, Is.GreaterThan(firstCallCount));
    }

    [Test]
    public void RunDueRetrainingAsync_TrainingFailure_DoesNotThrow()
    {
        // Zero trades => the pipeline's setup stage fails ("no closed trades") - the scheduler
        // must swallow this, not propagate it.
        var backtests = new CountingFakeBacktestApplicationService(_ => []);
        (LiveCalibrationTrainingScheduler scheduler, _, _) = Build(backtests, [BasePolicy()]);

        Assert.DoesNotThrowAsync(async () => await scheduler.RunDueRetrainingAsync(CancellationToken.None));
    }

    private static CalibrationRetrainingPolicy BasePolicy() => new()
    {
        Enabled = true,
        StrategyId = "improved",
        StrategyVersion = "improved-v1",
        Instrument = Instrument.Value,
        MinimumArtifactAgeBeforeRetrain = TimeSpan.FromDays(7),
        TrainingWindowDays = 90,
        Folds = 5,
        EmbargoHours = 6
    };

    private static (LiveCalibrationTrainingScheduler Scheduler, ICalibrationBundleApprovalStore Approvals, ITradingPolicyProfileStore Profiles) Build(
        IBacktestApplicationService backtests,
        IReadOnlyList<CalibrationRetrainingPolicy> policies,
        ICalibrationBundleApprovalStore? sharedApprovals = null)
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        var profiles = new FileTradingPolicyProfileStore(TempDirectory());
        ICalibrationBundleApprovalStore approvals = sharedApprovals
            ?? new FileCalibrationBundleApprovalStore(TempDirectory(), artifacts, profiles);
        var pipeline = new CalibrationTrainingPipeline(backtests, artifacts);
        var workflow = new CalibrationBundleWorkflow(pipeline, artifacts, approvals);
        var timeProvider = new FixedTimeProvider(Now);
        var scheduler = new LiveCalibrationTrainingScheduler(policies, workflow, profiles, timeProvider);
        return (scheduler, approvals, profiles);
    }

    private static IReadOnlyList<SimulatedTradeRecord> BuildTrades(int count)
    {
        var trades = new List<SimulatedTradeRecord>();
        var random = new Random(11);
        DateTimeOffset start = Now.AddDays(-90);
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset opened = start.AddHours(i * 6);
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
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", "training-scheduler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CountingFakeBacktestApplicationService(Func<BacktestRequest, IReadOnlyList<SimulatedTradeRecord>> trades)
        : IBacktestApplicationService
    {
        public int CallCount { get; private set; }

        public Task<ComparativeSimulationResult> RunToCompletionAsync(
            BacktestRequest request, IProgress<BacktestProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
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
