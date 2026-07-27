using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;
using Simulator.Jobs;

namespace Simulator.Experiments;

public sealed record SimulationExperimentApplicationServiceOptions
{
    public int QueueCapacity { get; init; } = 16;
    public int DispatcherCount { get; init; } = 2;
}

/// <summary>Durable parent orchestration around bounded learning/evaluation child work.</summary>
public sealed class SimulationExperimentApplicationService :
    ISimulationExperimentApplicationService,
    IAsyncDisposable
{
    private readonly ISimulationExperimentRepository _repository;
    private readonly ISimulationStrategyProfileStore _profiles;
    private readonly ISimulationExperimentExecutor _executor;
    private readonly ISimulationResourceGovernor _governor;
    private readonly Channel<Guid> _queue;
    private readonly ConcurrentDictionary<Guid, RunningExperiment> _running = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _initialization;
    private readonly Task[] _dispatchers;
    private bool _disposed;

    public SimulationExperimentApplicationService(
        ISimulationExperimentRepository repository,
        ISimulationStrategyProfileStore profiles,
        ISimulationExperimentExecutor executor,
        ISimulationResourceGovernor governor,
        SimulationExperimentApplicationServiceOptions? options = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _governor = governor ?? throw new ArgumentNullException(nameof(governor));
        SimulationExperimentApplicationServiceOptions resolved = options ?? new();
        if (resolved.QueueCapacity < 1 || resolved.DispatcherCount < 1)
            throw new ArgumentException("Experiment queue and dispatcher counts must be positive.", nameof(options));
        _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(resolved.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = resolved.DispatcherCount == 1,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _initialization = _repository.MarkInterruptedAsync();
        _dispatchers = Enumerable.Range(0, resolved.DispatcherCount)
            .Select(_ => Task.Run(() => DispatchAsync(_lifetime.Token)))
            .ToArray();
    }

    public async Task<SimulationExperimentHandle> StartAsync(
        SimulationExperimentRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var resolved = new List<(SimulationProfileRevisionReference Reference, SimulationStrategyProfile Profile, Guid RunId)>();
        foreach (SimulationProfileRevisionReference reference in request.Profiles)
        {
            SimulationStrategyProfile profile = await _profiles.ReadAsync(
                reference.ProfileId,
                reference.Revision,
                cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Profile {reference.ProfileId:N}/{reference.Revision} does not exist.");
            resolved.Add((reference, profile, Guid.NewGuid()));
        }

        Guid? baselineRunId = resolved.Where(item => item.Reference.IsBaseline)
            .Select(item => (Guid?)item.RunId)
            .SingleOrDefault();
        SimulationExperimentProfileRun[] profileRuns = resolved.Select(item => new SimulationExperimentProfileRun
        {
            ProfileRunId = item.RunId,
            Profile = SimulationStrategyProfileSnapshot.From(item.Profile),
            BaselineProfileRunId = item.Reference.IsBaseline || baselineRunId is null
                ? null
                : baselineRunId.Value.ToString("N")
        }).ToArray();

        var trainingWarmups = new Dictionary<Guid, AnalysisWarmupPlan>();
        var evaluationWarmups = new Dictionary<Guid, AnalysisWarmupPlan>();
        foreach (SimulationExperimentProfileRun run in profileRuns)
        {
            SimulationStrategyProfile profile = run.Profile.ResolvedProfile;
            trainingWarmups[run.ProfileRunId] = AnalysisWarmupPlanner.Plan(
                profile,
                request.Timeline.LearningFrom,
                request.Timeline.TrainingWarmupDays,
                profile.Runtime.Options.WarmupBaseCandleCount,
                request.AvailableDataFrom,
                request.AllowInsufficientWarmup);
            evaluationWarmups[run.ProfileRunId] = AnalysisWarmupPlanner.Plan(
                profile,
                request.Timeline.EvaluationFrom,
                request.Timeline.EvaluationWarmupDays,
                profile.Runtime.Options.WarmupBaseCandleCount,
                request.AvailableDataFrom,
                request.AllowInsufficientWarmup);
        }

        var manifest = new SimulationExperimentManifest
        {
            Timeline = request.Timeline,
            ProfileRuns = profileRuns,
            TrainingWarmups = trainingWarmups,
            EvaluationWarmups = evaluationWarmups,
            Parallelism = request.Parallelism,
            Ranking = request.Ranking,
            AnalysisCompatibilityKeys = profileRuns.ToDictionary(
                item => item.ProfileRunId,
                item => Hash(new
                {
                    Historical = HistoricalKey(item, request.Timeline, trainingWarmups[item.ProfileRunId]),
                    item.Profile.ResolvedProfile.Runtime.Options.AnalysisIntervals,
                    item.Profile.ResolvedProfile.Analysis
                })),
            HistoricalDataCompatibilityKeys = profileRuns.ToDictionary(
                item => item.ProfileRunId,
                item => HistoricalKey(item, request.Timeline, trainingWarmups[item.ProfileRunId]))
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid id = Guid.NewGuid();
        var snapshot = new SimulationExperimentSnapshot
        {
            Id = id,
            Revision = 1,
            Name = request.Name.Trim(),
            State = SimulationExperimentState.Queued,
            Stage = SimulationExperimentStage.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Manifest = manifest,
            Profiles = profileRuns.Select(run => new SimulationProfileRunProgress
            {
                ProfileRunId = run.ProfileRunId,
                ProfileName = run.Profile.ResolvedProfile.Name,
                Stage = SimulationExperimentStage.Queued,
                ProgressPercent = 0m
            }).ToArray(),
            Warnings = trainingWarmups.Values.Concat(evaluationWarmups.Values)
                .Any(item => !item.HasSufficientData)
                ? ["DiagnosticOnlyInsufficientWarmup"]
                : []
        };
        var running = new RunningExperiment(snapshot, _repository, _governor);
        if (!_running.TryAdd(id, running))
            throw new InvalidOperationException("Failed to register the experiment.");
        await _repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
        return new SimulationExperimentHandle(id, SimulationExperimentState.Queued);
    }

    public async Task<SimulationExperimentSnapshot?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _running.TryGetValue(id, out RunningExperiment? running)
            ? running.Snapshot
            : await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SimulationExperimentSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _repository.ListAsync(take, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        RunningExperiment running = await RequireRunningAsync(id, cancellationToken).ConfigureAwait(false);
        if (running.Snapshot.IsTerminal || running.Snapshot.State == SimulationExperimentState.Paused)
            return;
        running.PauseGate.Pause();
        await _executor.PauseChildrenAsync(id, cancellationToken).ConfigureAwait(false);
        await running.UpdateAsync(item => item with
        {
            State = SimulationExperimentState.Paused,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_running.TryGetValue(id, out RunningExperiment? running))
        {
            if (running.Snapshot.State != SimulationExperimentState.Paused)
                throw new InvalidOperationException("Only a paused experiment can be resumed in-process.");
            running.PauseGate.Resume();
            await _executor.ResumeChildrenAsync(id, cancellationToken).ConfigureAwait(false);
            await running.UpdateAsync(item => item with
            {
                State = SimulationExperimentState.Running,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        SimulationExperimentSnapshot persisted = await _repository.GetAsync(id, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Experiment '{id}' was not found.");
        if (persisted.State != SimulationExperimentState.Interrupted)
            throw new InvalidOperationException("Only an interrupted experiment can be resumed after restart.");
        var resumed = new RunningExperiment(persisted with
        {
            Revision = persisted.Revision + 1,
            State = SimulationExperimentState.Queued,
            UpdatedAt = DateTimeOffset.UtcNow,
            Warnings = persisted.Warnings.Append("ExplicitlyResumedAfterRestart")
                .Distinct(StringComparer.Ordinal).ToArray()
        }, _repository, _governor);
        if (!_running.TryAdd(id, resumed))
            throw new InvalidOperationException("Experiment was resumed concurrently.");
        await _repository.SaveAsync(resumed.Snapshot, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_running.TryGetValue(id, out RunningExperiment? running))
        {
            if (running.Snapshot.IsTerminal)
                return;
            running.PauseGate.Resume();
            running.Cancellation.Cancel();
            await _executor.CancelChildrenAsync(id, cancellationToken).ConfigureAwait(false);
            return;
        }

        SimulationExperimentSnapshot persisted = await _repository.GetAsync(id, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Experiment '{id}' was not found.");
        if (persisted.IsTerminal)
            return;
        await _repository.SaveAsync(persisted with
        {
            Revision = persisted.Revision + 1,
            State = SimulationExperimentState.Cancelled,
            Stage = SimulationExperimentStage.Cancelled,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RunningExperiment> RequireRunningAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_running.TryGetValue(id, out RunningExperiment? running))
            return running;
        SimulationExperimentSnapshot? persisted = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (persisted is null)
            throw new KeyNotFoundException($"Experiment '{id}' was not found.");
        throw new InvalidOperationException($"Experiment '{id}' is not active in this process.");
    }

    private async Task DispatchAsync(CancellationToken serviceToken)
    {
        await _initialization.ConfigureAwait(false);
        await foreach (Guid id in _queue.Reader.ReadAllAsync(serviceToken).ConfigureAwait(false))
        {
            if (!_running.TryGetValue(id, out RunningExperiment? running) || running.Snapshot.IsTerminal)
                continue;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceToken, running.Cancellation.Token);
            try
            {
                await ExecuteAsync(running, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await running.UpdateAsync(item => item with
                {
                    State = SimulationExperimentState.Cancelled,
                    Stage = SimulationExperimentStage.Cancelled,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await running.UpdateAsync(item => item with
                {
                    State = SimulationExperimentState.Failed,
                    Stage = SimulationExperimentStage.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    FailureReason = exception.Message
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (running.Snapshot.IsTerminal)
                {
                    _running.TryRemove(id, out _);
                    running.Dispose();
                }
            }
        }
    }

    private async Task ExecuteAsync(RunningExperiment running, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable parentPermit = await _governor.AcquireExperimentAsync(cancellationToken)
            .ConfigureAwait(false);
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SetStageAsync(running, SimulationExperimentStage.ResolvingProfiles, cancellationToken).ConfigureAwait(false);

        SimulationExperimentExecutionContext context = new()
        {
            ExperimentId = running.Snapshot.Id,
            Manifest = running.Snapshot.Manifest,
            WaitIfPausedAsync = () => running.PauseGate.WaitIfPausedAsync(cancellationToken),
            ReportProfileProgress = running.ReportProgress
        };

        if (!string.Equals(running.Snapshot.Manifest.DatasetStatus, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            await SetStageAsync(running, SimulationExperimentStage.PreparingDatasets, cancellationToken).ConfigureAwait(false);
            SimulationDatasetPreparationResult dataset = await _executor.PrepareDatasetsAsync(context, cancellationToken)
                .ConfigureAwait(false);
            await running.UpdateAsync(item => item with
            {
                Manifest = item.Manifest with
                {
                    DatasetStatus = "Ready",
                    DatasetId = dataset.DatasetId,
                    DatasetHash = dataset.DatasetHash
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        await MarkCommonStageAsync(running, SimulationExperimentStage.TrainingWarmup, cancellationToken)
            .ConfigureAwait(false);
        await RunLearningAsync(running, context, cancellationToken).ConfigureAwait(false);
        await MarkCommonStageAsync(running, SimulationExperimentStage.FreezingArtifacts, cancellationToken)
            .ConfigureAwait(false);
        ValidateFrozenArtifacts(running.Snapshot);
        await MarkCommonStageAsync(running, SimulationExperimentStage.EmbargoReady, cancellationToken)
            .ConfigureAwait(false);
        await MarkCommonStageAsync(running, SimulationExperimentStage.EvaluationWarmup, cancellationToken)
            .ConfigureAwait(false);
        await RunEvaluationAsync(running, context, cancellationToken).ConfigureAwait(false);

        await SetStageAsync(running, SimulationExperimentStage.Aggregating, cancellationToken).ConfigureAwait(false);
        SimulationExperimentComparisonSummary comparison = SimulationExperimentRanker.Build(running.Snapshot);
        await running.UpdateAsync(item => item with
        {
            State = SimulationExperimentState.Completed,
            Stage = SimulationExperimentStage.Completed,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Comparison = comparison,
            Profiles = item.Profiles.Select(profile => profile with
            {
                Stage = SimulationExperimentStage.Completed,
                ProgressPercent = 100m,
                CompletedStages = AddStage(profile.CompletedStages, SimulationExperimentStage.Completed)
            }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunLearningAsync(
        RunningExperiment running,
        SimulationExperimentExecutionContext context,
        CancellationToken cancellationToken)
    {
        await SetStageAsync(running, SimulationExperimentStage.Learning, cancellationToken).ConfigureAwait(false);
        await RunProfilesAsync(running, SimulationExperimentStage.Learning, async (run, token) =>
        {
            SimulationProfileLearningResult result = await _executor.LearnAsync(context, run, token)
                .ConfigureAwait(false);
            await running.UpdateProfileAsync(run.ProfileRunId, profile => profile with
            {
                Stage = SimulationExperimentStage.Learning,
                ProgressPercent = 100m,
                LearningJobId = result.ChildJobId,
                Artifacts = result.Artifacts,
                StatusDetail = result.Detail,
                CompletedStages = AddStage(profile.CompletedStages, SimulationExperimentStage.Learning)
            }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunEvaluationAsync(
        RunningExperiment running,
        SimulationExperimentExecutionContext context,
        CancellationToken cancellationToken)
    {
        await SetStageAsync(running, SimulationExperimentStage.Evaluating, cancellationToken).ConfigureAwait(false);
        await RunProfilesAsync(running, SimulationExperimentStage.Evaluating, async (run, token) =>
        {
            SimulationProfileRunProgress current = running.Snapshot.Profiles.Single(item => item.ProfileRunId == run.ProfileRunId);
            SimulationProfileEvaluationResult result = await _executor.EvaluateAsync(
                context,
                run,
                current.Artifacts,
                token).ConfigureAwait(false);
            await running.UpdateProfileAsync(run.ProfileRunId, profile => profile with
            {
                Stage = SimulationExperimentStage.Evaluating,
                ProgressPercent = 100m,
                EvaluationJobId = result.ChildJobId,
                Result = result.Comparison,
                StatusDetail = result.Detail,
                CompletedStages = AddStage(profile.CompletedStages, SimulationExperimentStage.Evaluating)
            }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunProfilesAsync(
        RunningExperiment running,
        SimulationExperimentStage completedStage,
        Func<SimulationExperimentProfileRun, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        int limit = Math.Min(
            running.Snapshot.Manifest.Parallelism.MaxProfileGroups,
            running.Snapshot.Manifest.ProfileRuns.Count);
        using var local = new SemaphoreSlim(limit, limit);
        Task[] tasks = running.Snapshot.Manifest.ProfileRuns.Select(async run =>
        {
            SimulationProfileRunProgress current = running.Snapshot.Profiles.Single(item => item.ProfileRunId == run.ProfileRunId);
            if (current.CompletedStages.Contains(completedStage))
                return;
            await local.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await running.WaitAsync(cancellationToken).ConfigureAwait(false);
                await using IAsyncDisposable permit = await _governor.AcquireProfileGroupAsync(
                    1,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await operation(run, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                local.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task MarkCommonStageAsync(
        RunningExperiment running,
        SimulationExperimentStage stage,
        CancellationToken cancellationToken)
    {
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        await running.UpdateAsync(item => item with
        {
            State = SimulationExperimentState.Running,
            Stage = stage,
            StartedAt = item.StartedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResourceUsage = _governor.GetUsage(),
            Profiles = item.Profiles.Select(profile => profile with
            {
                Stage = stage,
                ProgressPercent = 0m,
                CompletedStages = AddStage(profile.CompletedStages, stage)
            }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetStageAsync(
        RunningExperiment running,
        SimulationExperimentStage stage,
        CancellationToken cancellationToken)
    {
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        await running.UpdateAsync(item => item with
        {
            State = SimulationExperimentState.Running,
            Stage = stage,
            StartedAt = item.StartedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResourceUsage = _governor.GetUsage(),
            Profiles = item.Profiles.Select(profile => profile with
            {
                Stage = stage,
                ProgressPercent = 0m
            }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateFrozenArtifacts(SimulationExperimentSnapshot snapshot)
    {
        foreach (SimulationExperimentProfileRun run in snapshot.Manifest.ProfileRuns)
        {
            ExperimentCalibrationMode mode = run.Profile.ResolvedProfile.Calibration.Mode;
            SimulationProfileRunProgress progress = snapshot.Profiles.Single(item => item.ProfileRunId == run.ProfileRunId);
            if (mode != ExperimentCalibrationMode.Disabled && progress.Artifacts.Count == 0)
                throw new InvalidOperationException($"Profile '{progress.ProfileName}' has no frozen calibration artifacts.");
            if (progress.Artifacts.Any(item => item.ArtifactId == Guid.Empty || string.IsNullOrWhiteSpace(item.ContentHash)))
                throw new InvalidOperationException($"Profile '{progress.ProfileName}' has an invalid artifact manifest.");
        }
    }

    private static IReadOnlyList<SimulationExperimentStage> AddStage(
        IReadOnlyList<SimulationExperimentStage> stages,
        SimulationExperimentStage stage) => stages.Contains(stage) ? stages : stages.Append(stage).ToArray();

    private static string HistoricalKey(
        SimulationExperimentProfileRun run,
        SimulationExperimentTimeline timeline,
        AnalysisWarmupPlan warmup) => Hash(new
    {
        run.Profile.ResolvedProfile.Runtime.Options.SourceKind,
        Instrument = run.Profile.ResolvedProfile.Instrument.Value,
        run.Profile.ResolvedProfile.Runtime.Options.ExecutionInterval,
        From = warmup.StreamFrom,
        timeline.EvaluationTo
    });

    private static string Hash(object value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_dispatchers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (RunningExperiment running in _running.Values)
        {
            running.Cancellation.Cancel();
            running.Dispose();
        }
        _lifetime.Dispose();
    }

    private sealed class RunningExperiment : IDisposable
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _persistence = new(1, 1);
        private readonly ISimulationExperimentRepository _repository;
        private readonly ISimulationResourceGovernor _governor;
        private SimulationExperimentSnapshot _snapshot;

        public RunningExperiment(
            SimulationExperimentSnapshot snapshot,
            ISimulationExperimentRepository repository,
            ISimulationResourceGovernor governor)
        {
            _snapshot = snapshot;
            _repository = repository;
            _governor = governor;
        }

        public SimulationExperimentSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public CancellationTokenSource Cancellation { get; } = new();
        public AsyncPauseGate PauseGate { get; } = new();

        public ValueTask WaitAsync(CancellationToken cancellationToken) =>
            PauseGate.WaitIfPausedAsync(cancellationToken);

        public void ReportProgress(Guid runId, decimal percent, string? detail, Guid? childJobId)
        {
            lock (_sync)
            {
                SimulationExperimentSnapshot current = _snapshot;
                _snapshot = current with
                {
                    Revision = current.Revision + 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ResourceUsage = _governor.GetUsage(),
                    Profiles = current.Profiles.Select(profile => profile.ProfileRunId == runId
                        ? profile with
                        {
                            ProgressPercent = Math.Clamp(percent, 0m, 100m),
                            StatusDetail = detail,
                            LearningJobId = profile.Stage == SimulationExperimentStage.Learning
                                ? childJobId ?? profile.LearningJobId
                                : profile.LearningJobId,
                            EvaluationJobId = profile.Stage == SimulationExperimentStage.Evaluating
                                ? childJobId ?? profile.EvaluationJobId
                                : profile.EvaluationJobId
                        }
                        : profile).ToArray()
                };
            }
        }

        public Task UpdateProfileAsync(
            Guid runId,
            Func<SimulationProfileRunProgress, SimulationProfileRunProgress> update,
            CancellationToken cancellationToken) => UpdateAsync(snapshot => snapshot with
        {
            Profiles = snapshot.Profiles.Select(profile => profile.ProfileRunId == runId ? update(profile) : profile)
                .ToArray()
        }, cancellationToken);

        public async Task UpdateAsync(
            Func<SimulationExperimentSnapshot, SimulationExperimentSnapshot> update,
            CancellationToken cancellationToken)
        {
            SimulationExperimentSnapshot next;
            lock (_sync)
            {
                SimulationExperimentSnapshot current = _snapshot;
                next = update(current) with { Revision = current.Revision + 1 };
                _snapshot = next;
            }
            await _persistence.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _repository.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _persistence.Release();
            }
        }

        public void Dispose()
        {
            Cancellation.Dispose();
            _persistence.Dispose();
        }
    }
}
