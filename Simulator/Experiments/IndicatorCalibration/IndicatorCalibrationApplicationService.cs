using System.Collections.Concurrent;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;
using Simulator.Services;

namespace Simulator.Experiments.IndicatorCalibration;

internal static class IndicatorCalibrationRunStateExtensions
{
    public static bool IsTerminal(this IndicatorCalibrationRunState state) => state is
        IndicatorCalibrationRunState.Completed or IndicatorCalibrationRunState.Failed or IndicatorCalibrationRunState.Cancelled;
}

/// <summary>
/// In-process implementation of <see cref="IIndicatorCalibrationApplicationService"/> (blueprint
/// §17.1, §19 Phase 6). Each <see cref="StartAsync"/> call runs the full search in a background
/// <see cref="Task"/> tracked in memory; <see cref="GetAsync"/>/<see cref="ListAsync"/> primarily
/// poll that in-memory state, falling back to the persisted ledger when a run isn't tracked in
/// this process (e.g. after a restart).
///
/// Pause/resume (blueprint §14) does not rely on the orchestrator checkpointing individual search
/// steps - instead, <see cref="PauseAsync"/> cancels the run and <see cref="ResumeAsync"/>
/// re-invokes the same deterministic <see cref="IndicatorCalibrationOrchestrator"/> with the same
/// request. <c>IndicatorCalibrationOrchestrator.RunAsync</c> is a pure function of its inputs
/// (proven by <c>IndicatorCalibrationOrchestratorLeakageTests.IdenticalRequestAndScoring_ProducesIdenticalOrchestrationResult</c>),
/// so re-running it always reproduces the identical decision - what makes resume actually save
/// real work is that each strategy adapter's real backtest evaluator is backed by a persistent,
/// on-disk candidate cache keyed by calibration id (see
/// <c>Persistence.FileCalibrationCandidateCache</c>), so any evaluation already completed before
/// the pause is served from cache rather than re-run. Only the evaluation that happened to be
/// in flight at the moment of cancellation is lost, not the whole run.
/// </summary>
public sealed class IndicatorCalibrationApplicationService : IIndicatorCalibrationApplicationService, IAsyncDisposable
{
    private readonly IBacktestApplicationService _backtests;
    private readonly ICalibrationArtifactRepository _artifacts;
    private readonly IIndicatorCalibrationLedgerRepository _ledgers;
    private readonly ICalibrationPromotionEventRepository _promotionEvents;
    private readonly IReadOnlyDictionary<string, IIndicatorCalibrationStrategyAdapter> _adapters;
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);
    private readonly int _maximumCoordinatePasses;

    public IndicatorCalibrationApplicationService(
        IBacktestApplicationService backtests,
        ICalibrationArtifactRepository artifacts,
        IIndicatorCalibrationLedgerRepository ledgers,
        IReadOnlyList<IIndicatorCalibrationStrategyAdapter> adapters,
        int maximumCoordinatePasses = 3,
        ICalibrationPromotionEventRepository? promotionEvents = null)
    {
        _backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _ledgers = ledgers ?? throw new ArgumentNullException(nameof(ledgers));
        ArgumentNullException.ThrowIfNull(adapters);
        if (maximumCoordinatePasses < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCoordinatePasses));
        _adapters = adapters.ToDictionary(adapter => adapter.StrategyId, StringComparer.Ordinal);
        _maximumCoordinatePasses = maximumCoordinatePasses;
        _promotionEvents = promotionEvents ?? new FileCalibrationPromotionEventRepository();
    }

    public Task<CalibrationBudgetPreview> PreviewAsync(
        IndicatorCalibrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        IIndicatorCalibrationStrategyAdapter adapter = ResolveAdapter(request.StrategyId);
        return Task.FromResult(adapter.Preview(request, _maximumCoordinatePasses));
    }

    public async Task<IndicatorCalibrationRunSummary> StartAsync(
        IndicatorCalibrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        IIndicatorCalibrationStrategyAdapter adapter = ResolveAdapter(request.StrategyId);

        CalibrationBudgetPreview preview = adapter.Preview(request, _maximumCoordinatePasses);
        if (preview.ExceedsBudget && request.Budget.OverflowPolicy == CalibrationBudgetOverflowPolicy.Reject)
        {
            throw new InvalidOperationException(
                $"Budget preview ({preview.TotalPlannedEvaluations} planned evaluations) exceeds the " +
                "configured limit and OverflowPolicy is Reject.");
        }

        // A single id serves as both the calibration id and the ledger id - this is what lets
        // ResumeAsync find a run's persisted ledger after a process restart using only the id a
        // caller already has, with no separate id-mapping table to keep consistent.
        string calibrationId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ledger = new IndicatorCalibrationExperimentLedger
        {
            LedgerId = calibrationId,
            Revision = 1,
            Request = request,
            BudgetPreview = preview,
            Entries = [],
            CurrentStage = IndicatorCalibrationStage.NotStarted,
            IsCancelled = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _ledgers.SaveAsync(ledger, cancellationToken).ConfigureAwait(false);

        var summary = new IndicatorCalibrationRunSummary
        {
            CalibrationId = calibrationId,
            StrategyId = request.StrategyId,
            Instrument = request.Instrument.Value,
            State = IndicatorCalibrationRunState.Queued,
            CreatedAt = now
        };
        var entry = new RunEntry(summary, request, ledger);
        if (!_runs.TryAdd(calibrationId, entry))
            throw new InvalidOperationException("Failed to register the calibration run.");

        _ = Task.Run(() => ExecuteAsync(entry, adapter));
        return summary;
    }

    private async Task ExecuteAsync(RunEntry entry, IIndicatorCalibrationStrategyAdapter adapter)
    {
        entry.UpdateSummary(current => current with { State = IndicatorCalibrationRunState.Running });
        CancellationToken cancellationToken = entry.Cancellation.Token;
        try
        {
            (IndicatorCalibrationOrchestrationResult result, IndicatorCalibrationArtifact artifact) = await adapter.RunAsync(
                entry.Request, _backtests, entry.CalibrationId, entry.Ledger.LedgerId, entry.Ledger.ComputeChecksum(),
                _maximumCoordinatePasses, cancellationToken).ConfigureAwait(false);

            CalibrationArtifactMetadata metadata = await _artifacts.StoreIndicatorParametersAsync(
                artifact, description: $"Calibration run {entry.CalibrationId}", cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            entry.Result = result;
            entry.Artifact = artifact;
            entry.Ledger = await SaveLedgerAsync(entry.Ledger, IndicatorCalibrationStage.Completed, isCancelled: false)
                .ConfigureAwait(false);

            entry.UpdateSummary(current => current with
            {
                State = IndicatorCalibrationRunState.Completed,
                Outcome = result.Outcome,
                CompletedAt = DateTimeOffset.UtcNow,
                ArtifactId = metadata.Id
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool paused = entry.IsPauseRequested;
            entry.Ledger = await SaveLedgerAsync(
                entry.Ledger, paused ? IndicatorCalibrationStage.Paused : IndicatorCalibrationStage.Cancelled,
                isCancelled: !paused).ConfigureAwait(false);
            entry.UpdateSummary(current => current with
            {
                State = paused ? IndicatorCalibrationRunState.Paused : IndicatorCalibrationRunState.Cancelled,
                CompletedAt = paused ? null : DateTimeOffset.UtcNow
            });
        }
        catch (Exception exception)
        {
            entry.Ledger = await SaveLedgerAsync(entry.Ledger, IndicatorCalibrationStage.Failed, isCancelled: false)
                .ConfigureAwait(false);
            entry.UpdateSummary(current => current with
            {
                State = IndicatorCalibrationRunState.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                FailureReason = exception.Message
            });
        }
    }

    private async Task<IndicatorCalibrationExperimentLedger> SaveLedgerAsync(
        IndicatorCalibrationExperimentLedger ledger, IndicatorCalibrationStage stage, bool isCancelled)
    {
        IndicatorCalibrationExperimentLedger updated = ledger with
        {
            Revision = ledger.Revision + 1,
            CurrentStage = stage,
            IsCancelled = isCancelled,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _ledgers.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
        return updated;
    }

    public async Task<IndicatorCalibrationRunDetails?> GetAsync(
        string calibrationId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(calibrationId, out RunEntry? entry))
        {
            return new IndicatorCalibrationRunDetails
            {
                Summary = entry.Summary,
                Request = entry.Request,
                Result = entry.Result,
                Artifact = entry.Artifact
            };
        }

        // Not tracked in this process (e.g. a restart since it last ran) - fall back to whatever
        // was last persisted. Best-effort: outcome/artifact id are only known once a run actually
        // completes and are not reconstructed here.
        IndicatorCalibrationExperimentLedger? ledger = await _ledgers.GetAsync(calibrationId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
            return null;
        return new IndicatorCalibrationRunDetails
        {
            Summary = new IndicatorCalibrationRunSummary
            {
                CalibrationId = calibrationId,
                StrategyId = ledger.Request.StrategyId,
                Instrument = ledger.Request.Instrument.Value,
                State = ToRunState(ledger.CurrentStage),
                CreatedAt = ledger.CreatedAt
            },
            Request = ledger.Request
        };
    }

    private static IndicatorCalibrationRunState ToRunState(IndicatorCalibrationStage stage) => stage switch
    {
        IndicatorCalibrationStage.Completed => IndicatorCalibrationRunState.Completed,
        IndicatorCalibrationStage.Failed => IndicatorCalibrationRunState.Failed,
        IndicatorCalibrationStage.Cancelled => IndicatorCalibrationRunState.Cancelled,
        IndicatorCalibrationStage.Paused => IndicatorCalibrationRunState.Paused,
        _ => IndicatorCalibrationRunState.Paused // Anything mid-run when this process last saw it is treated as resumable, not lost.
    };

    public Task<IReadOnlyList<IndicatorCalibrationRunSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IndicatorCalibrationRunSummary>>(
            _runs.Values.Select(entry => entry.Summary).OrderByDescending(summary => summary.CreatedAt).ToArray());

    /// <summary>
    /// Cancels the in-flight run and marks it <see cref="IndicatorCalibrationRunState.Paused"/>
    /// rather than <see cref="IndicatorCalibrationRunState.Cancelled"/>. Whatever real backtest
    /// evaluation was in flight at the moment of cancellation is discarded (not resumed mid-step) -
    /// only already-completed evaluations are preserved, via the persistent candidate cache each
    /// strategy adapter keys by calibration id. That is the honest limit of this design: pause is
    /// "stop soon and keep everything finished so far," not a live mid-step suspend.
    /// </summary>
    public Task PauseAsync(string calibrationId, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(calibrationId, out RunEntry? entry))
            throw new KeyNotFoundException($"Calibration run '{calibrationId}' is not active in this process.");
        if (entry.Summary.State is not (IndicatorCalibrationRunState.Queued or IndicatorCalibrationRunState.Running))
        {
            throw new InvalidOperationException(
                $"Only a queued or running calibration can be paused (current state: {entry.Summary.State}).");
        }
        entry.RequestPause();
        entry.Cancellation.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes a paused run. Re-invokes the same deterministic
    /// <see cref="IndicatorCalibrationOrchestrator"/> with the same request - already proven to
    /// produce the identical decision output for identical inputs - through a persistent candidate
    /// cache keyed by <paramref name="calibrationId"/>, so only evaluations that were never
    /// completed before the pause actually run again (blueprint §14). Also resumes a run this
    /// process lost track of (e.g. after a restart) by reloading its request from the persisted
    /// ledger, provided that ledger was not already saved as genuinely Completed.
    /// </summary>
    public async Task ResumeAsync(string calibrationId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(calibrationId, out RunEntry? entry))
        {
            if (entry.Summary.State != IndicatorCalibrationRunState.Paused)
            {
                throw new InvalidOperationException(
                    $"Only a paused calibration can be resumed (current state: {entry.Summary.State}).");
            }
            IIndicatorCalibrationStrategyAdapter resumedAdapter = ResolveAdapter(entry.Request.StrategyId);
            entry.PrepareForResume();
            entry.UpdateSummary(current => current with { State = IndicatorCalibrationRunState.Queued, CompletedAt = null });
            _ = Task.Run(() => ExecuteAsync(entry, resumedAdapter));
            return;
        }

        IndicatorCalibrationExperimentLedger? ledger = await _ledgers.GetAsync(calibrationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Calibration run '{calibrationId}' was not found.");
        if (ledger.CurrentStage == IndicatorCalibrationStage.Completed)
            throw new InvalidOperationException($"Calibration run '{calibrationId}' already completed and cannot be resumed.");
        if (ledger.CurrentStage == IndicatorCalibrationStage.Cancelled)
            throw new InvalidOperationException($"Calibration run '{calibrationId}' was cancelled and cannot be resumed.");

        IIndicatorCalibrationStrategyAdapter adapter = ResolveAdapter(ledger.Request.StrategyId);
        var summary = new IndicatorCalibrationRunSummary
        {
            CalibrationId = calibrationId,
            StrategyId = ledger.Request.StrategyId,
            Instrument = ledger.Request.Instrument.Value,
            State = IndicatorCalibrationRunState.Queued,
            CreatedAt = ledger.CreatedAt
        };
        var resumedEntry = new RunEntry(summary, ledger.Request, ledger);
        _runs[calibrationId] = resumedEntry;
        _ = Task.Run(() => ExecuteAsync(resumedEntry, adapter));
    }

    public async Task CancelAsync(string calibrationId, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(calibrationId, out RunEntry? entry))
        {
            if (entry.Summary.State.IsTerminal())
                return;

            if (entry.Summary.State == IndicatorCalibrationRunState.Paused)
            {
                // Nothing is actively running to observe a cancellation token here - a paused run's
                // background task already exited. Cancel it directly so it can no longer be resumed.
                entry.Ledger = await SaveLedgerAsync(entry.Ledger, IndicatorCalibrationStage.Cancelled, isCancelled: true)
                    .ConfigureAwait(false);
                entry.UpdateSummary(current => current with
                {
                    State = IndicatorCalibrationRunState.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow
                });
                return;
            }

            entry.Cancellation.Cancel();
            return;
        }

        // Not tracked in this process (e.g. a restart since it last ran - same situation GetAsync
        // and ResumeAsync already handle). There is no live background task in this process to
        // cancel a token for, so the persisted ledger is the only thing that can be marked - but
        // that is sufficient: ResumeAsync's ledger-reload path refuses to resume a Cancelled ledger,
        // which is what actually stops an unattended nightly run from being picked back up.
        IndicatorCalibrationExperimentLedger? ledger = await _ledgers.GetAsync(calibrationId, cancellationToken).ConfigureAwait(false);
        if (ledger is null || ledger.CurrentStage is IndicatorCalibrationStage.Completed or
            IndicatorCalibrationStage.Cancelled or IndicatorCalibrationStage.Failed)
        {
            return;
        }
        await SaveLedgerAsync(ledger, IndicatorCalibrationStage.Cancelled, isCancelled: true).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndicatorCalibrationPendingApproval>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CalibrationArtifactMetadata> metadata = await _artifacts
            .ListAsync(CalibrationArtifactType.IndicatorParameters, take: 500, cancellationToken).ConfigureAwait(false);
        var pending = new List<IndicatorCalibrationPendingApproval>();
        foreach (CalibrationArtifactMetadata item in metadata)
        {
            if (item.PromotionStatus != CalibrationPromotionStatus.PendingReview)
                continue;
            IndicatorCalibrationArtifact? artifact = await _artifacts
                .GetIndicatorParametersAsync(item.Id, cancellationToken).ConfigureAwait(false);
            if (artifact is not null)
                pending.Add(new IndicatorCalibrationPendingApproval { ArtifactId = item.Id, Artifact = artifact });
        }
        return pending;
    }

    public async Task<CalibrationPromotionEvent> ApproveAsync(
        IndicatorCalibrationApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (Guid artifactId, IndicatorCalibrationArtifact artifact) = await ResolveArtifactForReviewAsync(
            request.ArtifactId, request.CalibrationId, "approve", cancellationToken).ConfigureAwait(false);
        if (artifact.Outcome != CalibrationOutcome.Improved)
        {
            throw new InvalidOperationException(
                $"Only an artifact with Outcome = Improved may be approved (this one is {artifact.Outcome}).");
        }
        await _artifacts.UpdatePromotionStatusAsync(artifactId, CalibrationPromotionStatus.Approved, cancellationToken)
            .ConfigureAwait(false);

        var promotionEvent = new CalibrationPromotionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            ArtifactId = artifactId.ToString("N"),
            ArtifactChecksum = artifact.ResolvedCandidateConfigurationHash,
            ApprovedBy = request.ApprovedBy,
            ApprovedAt = DateTimeOffset.UtcNow,
            TargetEnvironment = request.TargetEnvironment,
            ReviewNotes = request.ReviewNotes,
            ReplacesArtifactId = request.ReplacesArtifactId
        };
        promotionEvent.Validate();
        await _promotionEvents.SaveAsync(promotionEvent, cancellationToken).ConfigureAwait(false);
        if (_runs.TryGetValue(request.CalibrationId ?? string.Empty, out RunEntry? entry))
            entry.UpdateSummary(current => current with { ArtifactId = artifactId });
        return promotionEvent;
    }

    public async Task RejectAsync(
        IndicatorCalibrationRejectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (Guid artifactId, _) = await ResolveArtifactForReviewAsync(
            request.ArtifactId, request.CalibrationId, "reject", cancellationToken).ConfigureAwait(false);
        await _artifacts.UpdatePromotionStatusAsync(artifactId, CalibrationPromotionStatus.Rejected, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the artifact an approve/reject request refers to - directly via
    /// <see cref="IndicatorCalibrationApprovalRequest.ArtifactId"/>/<see cref="IndicatorCalibrationRejectionRequest.ArtifactId"/>
    /// when supplied (works regardless of whether the originating run is tracked in this process),
    /// otherwise via the in-memory run tracked by <c>calibrationId</c> (only works while this
    /// process still remembers that run).
    /// </summary>
    private async Task<(Guid ArtifactId, IndicatorCalibrationArtifact Artifact)> ResolveArtifactForReviewAsync(
        Guid? requestedArtifactId, string? calibrationId, string action, CancellationToken cancellationToken)
    {
        if (requestedArtifactId is { } artifactId)
        {
            IndicatorCalibrationArtifact artifact = await _artifacts.GetIndicatorParametersAsync(artifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Indicator-calibration artifact '{artifactId}' does not exist.");
            return (artifactId, artifact);
        }

        if (string.IsNullOrEmpty(calibrationId))
            throw new ArgumentException($"Either ArtifactId or CalibrationId is required to {action}.");
        if (!_runs.TryGetValue(calibrationId, out RunEntry? entry) || entry.Artifact is null)
            throw new KeyNotFoundException($"Calibration run '{calibrationId}' has no artifact to {action}.");
        Guid resolvedId = entry.Summary.ArtifactId
            ?? throw new InvalidOperationException("Artifact was not persisted for this run.");
        return (resolvedId, entry.Artifact);
    }

    private IIndicatorCalibrationStrategyAdapter ResolveAdapter(string strategyId) =>
        _adapters.TryGetValue(strategyId, out IIndicatorCalibrationStrategyAdapter? adapter)
            ? adapter
            : throw new KeyNotFoundException($"No calibration strategy adapter is registered for '{strategyId}'.");

    public ValueTask DisposeAsync()
    {
        foreach (RunEntry entry in _runs.Values)
        {
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private sealed class RunEntry(
        IndicatorCalibrationRunSummary summary, IndicatorCalibrationRequest request, IndicatorCalibrationExperimentLedger ledger)
    {
        private readonly object _sync = new();
        private IndicatorCalibrationRunSummary _summary = summary;
        private IndicatorCalibrationExperimentLedger _ledger = ledger;

        public string CalibrationId => _summary.CalibrationId;
        public IndicatorCalibrationRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; private set; } = new();
        public bool IsPauseRequested { get; private set; }
        public IndicatorCalibrationOrchestrationResult? Result { get; set; }
        public IndicatorCalibrationArtifact? Artifact { get; set; }

        public IndicatorCalibrationRunSummary Summary
        {
            get { lock (_sync) return _summary; }
        }

        public IndicatorCalibrationExperimentLedger Ledger
        {
            get { lock (_sync) return _ledger; }
            set { lock (_sync) _ledger = value; }
        }

        public void UpdateSummary(Func<IndicatorCalibrationRunSummary, IndicatorCalibrationRunSummary> update)
        {
            lock (_sync)
                _summary = update(_summary);
        }

        public void RequestPause() => IsPauseRequested = true;

        /// <summary>Prepares this entry to be re-executed: a fresh, uncancelled token, pause flag cleared.</summary>
        public void PrepareForResume()
        {
            Cancellation.Dispose();
            Cancellation = new CancellationTokenSource();
            IsPauseRequested = false;
        }
    }
}
