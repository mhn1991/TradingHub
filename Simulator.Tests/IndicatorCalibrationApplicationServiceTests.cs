using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;
using Simulator.Jobs;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// Exercises <see cref="IndicatorCalibrationApplicationService"/>'s Start/Get/Approve/Reject flow
/// end-to-end against real file-backed repositories, using a fake
/// <see cref="IIndicatorCalibrationStrategyAdapter"/> so the test doesn't need a real backtest
/// engine run - this suite is about the application-service plumbing (approval gating, promotion
/// events, artifact persistence), not the search algorithm itself (covered elsewhere).
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationApplicationServiceTests
{
    private const string StrategyId = "test-strategy-for-application-service";

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-calibration-app-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static IndicatorCalibrationRequest BuildRequest() => new()
    {
        StrategyId = StrategyId,
        ManifestVersion = "fake-manifest-v1",
        Instrument = new Brokers.Models.InstrumentKey("FX:EUR/USD"),
        TimeframeTopology = new TimeframeTopology
        {
            ExecutionInterval = Brokers.Models.BarInterval.Minutes(1),
            AnalysisBaseInterval = Brokers.Models.BarInterval.Minutes(1),
            SetupInterval = Brokers.Models.BarInterval.Minutes(15),
            ConfirmationIntervals = [],
            TrendIntervals = [Brokers.Models.BarInterval.Hours(1)],
            ManagementIntervals = [],
            AlignmentPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
            WarmupMinimumDays = 5
        },
        Timeline = new CalibrationTimeline
        {
            LearningFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LearningTo = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            ExternalHoldoutFrom = new DateTimeOffset(2025, 3, 5, 0, 0, 0, TimeSpan.Zero),
            ExternalHoldoutTo = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero),
            EmbargoDays = 2,
            WarmupDays = 5
        },
        InternalFoldCount = 3,
        RandomSeed = 1,
        Budget = new CalibrationEvaluationBudget
        {
            WarningEvaluationCount = 100,
            MaximumEvaluationCount = 1000,
            MaximumEvaluationsPerFold = 200,
            MaximumInteractionCombinationsPerGroup = 10,
            HardRuntimeLimit = TimeSpan.FromHours(1),
            OverflowPolicy = CalibrationBudgetOverflowPolicy.Reject
        },
        BaselineConfigurationHash = "baseline-hash"
    };

    private static BacktestEvaluationResult TrivialResult() => new()
    {
        TradeCount = 30,
        MedianExpectancyR = 0.2m,
        MaximumDrawdownR = 1m,
        ProfitFactor = 1.5m,
        DataQualityValid = true
    };

    private static IndicatorCalibrationOrchestrationResult BuildOrchestrationResult(CalibrationOutcome outcome)
    {
        var candidate = new CalibrationCandidate
        {
            NumericValues = new Dictionary<string, decimal>(StringComparer.Ordinal),
            AblationValues = new Dictionary<string, bool>(StringComparer.Ordinal)
        };
        var fold = new FoldCandidateSelectionResult
        {
            FoldId = 0,
            Candidate = candidate,
            SelectedStartingPointId = StartingPointIds.Default,
            State = FoldResultState.Improved,
            Reason = "test",
            TrainingScore = 0.2m,
            ValidationResult = TrivialResult(),
            ValidationScore = 0.2m,
            BaselineValidationResult = TrivialResult(),
            BaselineValidationScore = 0.1m
        };
        var aggregation = new CrossFoldAggregationResult
        {
            AggregatedCandidate = candidate,
            ParameterResults = [],
            TotalFolds = 1,
            ContributingFolds = 1,
            AcceptableFoldCount = 1,
            AcceptableFoldPercent = 100m,
            IsStable = true,
            Reason = "test"
        };
        return new IndicatorCalibrationOrchestrationResult
        {
            Folds = [new CalibrationInternalFold
            {
                FoldId = 0,
                TrainingFrom = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TrainingTo = new DateTimeOffset(2025, 1, 20, 0, 0, 0, TimeSpan.Zero),
                ValidationFrom = new DateTimeOffset(2025, 1, 22, 0, 0, 0, TimeSpan.Zero),
                ValidationTo = new DateTimeOffset(2025, 1, 28, 0, 0, 0, TimeSpan.Zero)
            }],
            FoldResults = [fold],
            Aggregation = aggregation,
            ExternalHoldoutBaselineResult = TrivialResult(),
            ExternalHoldoutBaselineScore = 0.1m,
            ExternalHoldoutCandidateResult = TrivialResult(),
            ExternalHoldoutCandidateScore = 0.2m,
            Outcome = outcome,
            Reason = "test outcome",
            TotalCandidatesEvaluated = 10
        };
    }

    private static IndicatorCalibrationArtifact BuildArtifact(CalibrationOutcome outcome) => new()
    {
        SchemaVersion = 1,
        CalibrationId = "app-service-test-calibration",
        StrategyId = StrategyId,
        StrategyImplementationVersion = "1.0",
        OptionsSchemaVersion = "fake-options-v1",
        ManifestVersion = "fake-manifest-v1",
        Scope = IndicatorCalibrationArtifact.InstrumentScope,
        Instrument = "FX:EUR/USD",
        TimeframeTopologyHash = "topology-hash",
        CandleDataIdentityHash = "candle-hash",
        BaselineConfigurationHash = "baseline-hash",
        ResolvedCandidateConfigurationHash = "resolved-hash",
        Overrides = [],
        AblationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal),
        Evidence = new CalibrationEvidenceSummary
        {
            FoldCount = 1,
            AcceptableFoldCount = 1,
            AcceptableFoldPercent = 100m,
            MedianValidationExpectancyR = 0.2m,
            MedianValidationDrawdownR = 1m,
            MedianValidationTradeCount = 30,
            TrainValidationDegradation = 0m,
            BaselineMedianExpectancyR = 0.1m,
            ImprovementOverBaseline = 0.1m,
            ExternalHoldoutExpectancyR = 0.2m,
            ExternalHoldoutDrawdownR = 1m,
            ExternalHoldoutTradeCount = 30,
            ExternalHoldoutBaselineExpectancyR = 0.1m,
            TotalCandidatesEvaluated = 10
        },
        Outcome = outcome,
        ExperimentLedgerId = "ledger-1",
        ExperimentLedgerChecksum = "checksum-1",
        PromotionStatus = CalibrationPromotionStatus.PendingReview,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeStrategyAdapter(CalibrationOutcome outcome, bool hangOnFirstCall = false) : IIndicatorCalibrationStrategyAdapter
    {
        private int _callCount;

        /// <summary>Signalled once the first (pausable) call has actually started, so a test can pause deterministically instead of racing a fixed delay.</summary>
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public string StrategyId => IndicatorCalibrationApplicationServiceTests.StrategyId;
        public string ManifestVersion => "fake-manifest-v1";

        public CalibrationBudgetPreview Preview(IndicatorCalibrationRequest request, int maximumCoordinatePasses) => new()
        {
            BaselineEvaluations = 1,
            SensitivityEvaluations = 1,
            StartingPointEvaluations = 1,
            MaximumCoordinatePasses = maximumCoordinatePasses,
            InteractionEvaluations = 0,
            RefinementEvaluations = 0,
            InternalFoldMultiplier = request.InternalFoldCount,
            ExternalHoldoutEvaluations = 2,
            EstimatedCacheHits = 0,
            ExpectedUncachedBacktests = 10,
            EstimatedCandleEvaluations = 100,
            EstimatedDurationLow = TimeSpan.FromSeconds(1),
            EstimatedDurationHigh = TimeSpan.FromSeconds(2),
            HardRuntimeLimit = request.Budget.HardRuntimeLimit,
            ExceedsBudget = false,
            AppliedOverflowPolicy = null,
            OverflowAdjustments = []
        };

        public async Task<(IndicatorCalibrationOrchestrationResult Result, IndicatorCalibrationArtifact Artifact)> RunAsync(
            IndicatorCalibrationRequest request,
            IBacktestApplicationService backtests,
            string calibrationId,
            string experimentLedgerId,
            string experimentLedgerChecksum,
            int maximumCoordinatePasses,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1 && hangOnFirstCall)
            {
                FirstCallStarted.TrySetResult();
                // Simulates a long-running real search: blocks until PauseAsync cancels this token,
                // exactly like an in-flight real backtest evaluation would observe cancellation.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            return (BuildOrchestrationResult(outcome), BuildArtifact(outcome));
        }
    }

    private static async Task<(IndicatorCalibrationApplicationService Service, ICalibrationArtifactRepository Artifacts, ICalibrationPromotionEventRepository PromotionEvents, IndicatorCalibrationRunSummary Summary)>
        StartAndWaitAsync(CalibrationOutcome outcome)
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        var ledgers = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        var promotionEvents = new FileCalibrationPromotionEventRepository(TempDirectory());
        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var service = new IndicatorCalibrationApplicationService(
            backtests, artifacts, ledgers, [new FakeStrategyAdapter(outcome)], maximumCoordinatePasses: 1, promotionEvents: promotionEvents);

        IndicatorCalibrationRunSummary summary = await service.StartAsync(BuildRequest());
        for (int i = 0; i < 100 && !summary.State.IsTerminal(); i++)
        {
            await Task.Delay(20);
            IndicatorCalibrationRunDetails? details = await service.GetAsync(summary.CalibrationId);
            summary = details!.Summary;
        }

        return (service, artifacts, promotionEvents, summary);
    }

    [Test]
    public async Task StartAsync_WithFakeAdapter_CompletesAndPersistsArtifact()
    {
        (IndicatorCalibrationApplicationService service, ICalibrationArtifactRepository artifacts, _, IndicatorCalibrationRunSummary summary) =
            await StartAndWaitAsync(CalibrationOutcome.Improved);

        Assert.That(summary.State, Is.EqualTo(IndicatorCalibrationRunState.Completed));
        Assert.That(summary.ArtifactId, Is.Not.Null);
        IndicatorCalibrationArtifact? stored = await artifacts.GetIndicatorParametersAsync(summary.ArtifactId!.Value);
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.PromotionStatus, Is.EqualTo(CalibrationPromotionStatus.PendingReview));
        await service.DisposeAsync();
    }

    [Test]
    public async Task ApproveAsync_ImprovedArtifact_FlipsStatusAndPersistsPromotionEvent()
    {
        (IndicatorCalibrationApplicationService service, ICalibrationArtifactRepository artifacts, ICalibrationPromotionEventRepository promotionEvents, IndicatorCalibrationRunSummary summary) =
            await StartAndWaitAsync(CalibrationOutcome.Improved);

        CalibrationPromotionEvent promotionEvent = await service.ApproveAsync(new IndicatorCalibrationApprovalRequest
        {
            CalibrationId = summary.CalibrationId,
            ApprovedBy = "test-reviewer",
            TargetEnvironment = "Demo",
            ReviewNotes = "Looks good."
        });

        IndicatorCalibrationArtifact? stored = await artifacts.GetIndicatorParametersAsync(summary.ArtifactId!.Value);
        IReadOnlyList<CalibrationPromotionEvent> persistedEvents = await promotionEvents.ListForArtifactAsync(promotionEvent.ArtifactId);

        Assert.Multiple(() =>
        {
            Assert.That(stored!.PromotionStatus, Is.EqualTo(CalibrationPromotionStatus.Approved));
            Assert.That(persistedEvents, Has.Count.EqualTo(1));
            Assert.That(persistedEvents[0].EventId, Is.EqualTo(promotionEvent.EventId));
            Assert.That(persistedEvents[0].ApprovedBy, Is.EqualTo("test-reviewer"));
        });
        await service.DisposeAsync();
    }

    [Test]
    public async Task ApproveAsync_NonImprovedArtifact_Throws()
    {
        (IndicatorCalibrationApplicationService service, _, _, IndicatorCalibrationRunSummary summary) =
            await StartAndWaitAsync(CalibrationOutcome.NoImprovement);

        Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(new IndicatorCalibrationApprovalRequest
        {
            CalibrationId = summary.CalibrationId,
            ApprovedBy = "test-reviewer",
            TargetEnvironment = "Demo",
            ReviewNotes = "Should not be approvable."
        }));
        await service.DisposeAsync();
    }

    [Test]
    public async Task RejectAsync_FlipsStatusToRejected()
    {
        (IndicatorCalibrationApplicationService service, ICalibrationArtifactRepository artifacts, _, IndicatorCalibrationRunSummary summary) =
            await StartAndWaitAsync(CalibrationOutcome.NoImprovement);

        // NoImprovement artifacts don't get an ArtifactId surfaced via Approve, but Reject only
        // needs the artifact id already recorded on the summary from StartAsync's own completion.
        await service.RejectAsync(new IndicatorCalibrationRejectionRequest
        {
            CalibrationId = summary.CalibrationId,
            RejectedBy = "test-reviewer",
            Reason = "Not good enough."
        });

        IndicatorCalibrationArtifact? stored = await artifacts.GetIndicatorParametersAsync(summary.ArtifactId!.Value);
        Assert.That(stored!.PromotionStatus, Is.EqualTo(CalibrationPromotionStatus.Rejected));
        await service.DisposeAsync();
    }

    [Test]
    public async Task PauseAsync_OnATerminalRun_Throws()
    {
        (IndicatorCalibrationApplicationService service, _, _, IndicatorCalibrationRunSummary summary) =
            await StartAndWaitAsync(CalibrationOutcome.Improved);
        Assert.ThrowsAsync<InvalidOperationException>(() => service.PauseAsync(summary.CalibrationId));
        await service.DisposeAsync();
    }

    [Test]
    public async Task ResumeAsync_OnANonPausedRun_Throws()
    {
        (IndicatorCalibrationApplicationService service, _, _, IndicatorCalibrationRunSummary summary) =
            await StartAndWaitAsync(CalibrationOutcome.Improved);
        Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeAsync(summary.CalibrationId));
        await service.DisposeAsync();
    }

    [Test]
    public async Task PauseAsync_ThenResumeAsync_CompletesTheRunOnASecondAdapterInvocation()
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        var ledgers = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var adapter = new FakeStrategyAdapter(CalibrationOutcome.Improved, hangOnFirstCall: true);
        var service = new IndicatorCalibrationApplicationService(
            backtests, artifacts, ledgers, [adapter], maximumCoordinatePasses: 1);

        IndicatorCalibrationRunSummary summary = await service.StartAsync(BuildRequest());
        await adapter.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.PauseAsync(summary.CalibrationId);
        IndicatorCalibrationRunSummary paused = await PollUntilAsync(service, summary.CalibrationId,
            s => s.State is IndicatorCalibrationRunState.Paused or IndicatorCalibrationRunState.Cancelled or IndicatorCalibrationRunState.Failed);
        Assert.That(paused.State, Is.EqualTo(IndicatorCalibrationRunState.Paused));
        Assert.That(adapter.CallCount, Is.EqualTo(1), "Pausing must not have let a second invocation start yet.");

        await service.ResumeAsync(summary.CalibrationId);
        IndicatorCalibrationRunSummary completed = await PollUntilAsync(service, summary.CalibrationId, s => s.State.IsTerminal());

        Assert.Multiple(() =>
        {
            Assert.That(completed.State, Is.EqualTo(IndicatorCalibrationRunState.Completed));
            Assert.That(adapter.CallCount, Is.EqualTo(2), "Resuming must have started exactly one more adapter invocation.");
        });
        await service.DisposeAsync();
    }

    [Test]
    public async Task CancelAsync_OnAPausedRun_TransitionsToCancelled_AndResumeThenThrows()
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        var ledgers = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var adapter = new FakeStrategyAdapter(CalibrationOutcome.Improved, hangOnFirstCall: true);
        var service = new IndicatorCalibrationApplicationService(
            backtests, artifacts, ledgers, [adapter], maximumCoordinatePasses: 1);

        IndicatorCalibrationRunSummary summary = await service.StartAsync(BuildRequest());
        await adapter.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.PauseAsync(summary.CalibrationId);
        await PollUntilAsync(service, summary.CalibrationId, s => s.State == IndicatorCalibrationRunState.Paused);

        await service.CancelAsync(summary.CalibrationId);
        IndicatorCalibrationRunDetails? details = await service.GetAsync(summary.CalibrationId);

        Assert.That(details!.Summary.State, Is.EqualTo(IndicatorCalibrationRunState.Cancelled));
        Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeAsync(summary.CalibrationId));
        await service.DisposeAsync();
    }

    private static async Task<IndicatorCalibrationRunSummary> PollUntilAsync(
        IIndicatorCalibrationApplicationService service, string calibrationId, Func<IndicatorCalibrationRunSummary, bool> predicate)
    {
        IndicatorCalibrationRunDetails? details = await service.GetAsync(calibrationId);
        for (int i = 0; i < 200 && !predicate(details!.Summary); i++)
        {
            await Task.Delay(20);
            details = await service.GetAsync(calibrationId);
        }
        return details!.Summary;
    }

    [Test]
    public async Task ApproveAsync_ByArtifactId_WorksEvenWhenTheOriginatingRunIsNotTrackedInMemory()
    {
        // Simulates reviewing a prior night's unattended calibration from a fresh process: a
        // brand-new IndicatorCalibrationApplicationService instance, sharing only the same
        // artifact/promotion-event directories a previous instance already wrote to.
        string artifactsDirectory = TempDirectory();
        string promotionEventsDirectory = TempDirectory();
        var producerArtifacts = new FileCalibrationArtifactRepository(artifactsDirectory);
        CalibrationArtifactMetadata metadata = await producerArtifacts.StoreIndicatorParametersAsync(
            BuildStandaloneArtifact(CalibrationOutcome.Improved));

        var reviewerArtifacts = new FileCalibrationArtifactRepository(artifactsDirectory);
        var reviewerLedgers = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        var reviewerPromotionEvents = new FileCalibrationPromotionEventRepository(promotionEventsDirectory);
        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var reviewerService = new IndicatorCalibrationApplicationService(
            backtests, reviewerArtifacts, reviewerLedgers, [new FakeStrategyAdapter(CalibrationOutcome.Improved)],
            promotionEvents: reviewerPromotionEvents);

        CalibrationPromotionEvent promotionEvent = await reviewerService.ApproveAsync(new IndicatorCalibrationApprovalRequest
        {
            ArtifactId = metadata.Id,
            ApprovedBy = "night-owl-reviewer",
            TargetEnvironment = "Demo",
            ReviewNotes = "Reviewed from a fresh process - the run itself is long gone."
        });

        IndicatorCalibrationArtifact? stored = await producerArtifacts.GetIndicatorParametersAsync(metadata.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.PromotionStatus, Is.EqualTo(CalibrationPromotionStatus.Approved));
            Assert.That(promotionEvent.ArtifactId, Is.EqualTo(metadata.Id.ToString("N")));
        });
        await reviewerService.DisposeAsync();
    }

    [Test]
    public async Task ListPendingApprovalsAsync_ReturnsOnlyPendingReviewArtifacts()
    {
        var artifacts = new FileCalibrationArtifactRepository(TempDirectory());
        IndicatorCalibrationArtifact pendingOneArtifact = BuildStandaloneArtifact(CalibrationOutcome.Improved);
        IndicatorCalibrationArtifact pendingTwoArtifact = BuildStandaloneArtifact(CalibrationOutcome.NoImprovement);
        IndicatorCalibrationArtifact approvedArtifact = BuildStandaloneArtifact(CalibrationOutcome.Improved);
        await artifacts.StoreIndicatorParametersAsync(pendingOneArtifact);
        await artifacts.StoreIndicatorParametersAsync(pendingTwoArtifact);
        CalibrationArtifactMetadata approvedMetadata = await artifacts.StoreIndicatorParametersAsync(approvedArtifact);
        await artifacts.UpdatePromotionStatusAsync(approvedMetadata.Id, CalibrationPromotionStatus.Approved);

        var ledgers = new FileIndicatorCalibrationLedgerRepository(TempDirectory());
        await using var backtests = new BacktestApplicationService(new FileSimulationJobRepository(TempDirectory()));
        var service = new IndicatorCalibrationApplicationService(
            backtests, artifacts, ledgers, [new FakeStrategyAdapter(CalibrationOutcome.Improved)]);

        IReadOnlyList<IndicatorCalibrationPendingApproval> pending = await service.ListPendingApprovalsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending, Has.Count.EqualTo(2));
            Assert.That(pending.Select(item => item.Artifact.CalibrationId),
                Is.EquivalentTo(new[] { pendingOneArtifact.CalibrationId, pendingTwoArtifact.CalibrationId }));
            Assert.That(pending.Select(item => item.ArtifactId), Has.All.Not.EqualTo(Guid.Empty),
                "Every pending entry must carry a real, usable artifact id.");
            Assert.That(pending.All(item => item.Artifact.PromotionStatus == CalibrationPromotionStatus.PendingReview), Is.True);
        });
        await service.DisposeAsync();
    }

    private static IndicatorCalibrationArtifact BuildStandaloneArtifact(CalibrationOutcome outcome) => new()
    {
        SchemaVersion = 1,
        CalibrationId = $"standalone-{Guid.NewGuid():N}",
        StrategyId = StrategyId,
        StrategyImplementationVersion = "1.0",
        OptionsSchemaVersion = "fake-options-v1",
        ManifestVersion = "fake-manifest-v1",
        Scope = IndicatorCalibrationArtifact.InstrumentScope,
        Instrument = "FX:EUR/USD",
        TimeframeTopologyHash = "topology-hash",
        CandleDataIdentityHash = "candle-hash",
        BaselineConfigurationHash = "baseline-hash",
        ResolvedCandidateConfigurationHash = "resolved-hash",
        Overrides = [],
        AblationOverrides = new Dictionary<string, bool>(StringComparer.Ordinal),
        Evidence = new CalibrationEvidenceSummary
        {
            FoldCount = 1,
            AcceptableFoldCount = 1,
            AcceptableFoldPercent = 100m,
            MedianValidationExpectancyR = 0.2m,
            MedianValidationDrawdownR = 1m,
            MedianValidationTradeCount = 30,
            TrainValidationDegradation = 0m,
            BaselineMedianExpectancyR = 0.1m,
            ImprovementOverBaseline = 0.1m,
            ExternalHoldoutExpectancyR = 0.2m,
            ExternalHoldoutDrawdownR = 1m,
            ExternalHoldoutTradeCount = 30,
            ExternalHoldoutBaselineExpectancyR = 0.1m,
            TotalCandidatesEvaluated = 10
        },
        Outcome = outcome,
        ExperimentLedgerId = "ledger-1",
        ExperimentLedgerChecksum = "checksum-1",
        PromotionStatus = CalibrationPromotionStatus.PendingReview,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
