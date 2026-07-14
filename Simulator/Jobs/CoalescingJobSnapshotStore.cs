using Simulator.Models;

namespace Simulator.Jobs;

/// <summary>
/// Ensures at most one persistence write per job at a time. Newer snapshots supersede pending ones.
/// Terminal snapshots are always flushed and awaited.
/// </summary>
public sealed class CoalescingJobSnapshotStore
{
    private readonly ISimulationJobRepository _repository;
    private readonly Dictionary<Guid, JobWriteState> _states = [];
    private readonly object _sync = new();

    public CoalescingJobSnapshotStore(ISimulationJobRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task PublishAsync(
        SimulationJobSnapshot snapshot,
        bool terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        JobWriteState state;
        Exception? previousError;
        lock (_sync)
        {
            if (!_states.TryGetValue(snapshot.Id, out state!))
            {
                state = new JobWriteState();
                _states[snapshot.Id] = state;
            }

            if (snapshot.Revision < state.LastAcceptedRevision)
                return;

            previousError = state.LastError;
            state.LastError = null;
            state.LastAcceptedRevision = snapshot.Revision;
            state.Pending = snapshot;
            state.Terminal |= terminal;
            if (state.Worker is null || state.Worker.IsCompleted)
            {
                state.Worker = WorkerAsync(snapshot.Id);
            }
        }

        if (terminal)
        {
            await FlushAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            Exception? terminalError = previousError ?? GetLastError(snapshot.Id);
            if (terminalError is not null)
            {
                throw new InvalidOperationException(
                    $"Snapshot persistence for terminal job '{snapshot.Id}' observed an earlier failure.",
                    terminalError);
            }
            return;
        }

        if (previousError is not null)
            throw new InvalidOperationException(
                $"The previous snapshot persistence operation for job '{snapshot.Id}' failed.",
                previousError);

    }

    public async Task FlushAsync(Guid id, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            SimulationJobSnapshot? pending = null;
            lock (_sync)
            {
                if (!_states.TryGetValue(id, out JobWriteState? state))
                    return;
                if (state.Writing)
                {
                    // Wait outside lock via spin of the in-flight write.
                }
                else if (state.Pending is not null)
                {
                    pending = state.Pending;
                    state.Pending = null;
                    state.Writing = true;
                }
                else
                {
                    return;
                }
            }

            if (pending is null)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _repository.SaveAsync(pending, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (_states.TryGetValue(id, out JobWriteState? state))
                        state.LastError = exception;
                }
                throw;
            }
            finally
            {
                lock (_sync)
                {
                    if (_states.TryGetValue(id, out JobWriteState? state))
                    {
                        state.Writing = false;
                        if (state.Pending is null && state.Terminal && state.LastError is null)
                            _states.Remove(id);
                    }
                }
            }
        }
    }

    private async Task WorkerAsync(Guid id)
    {
        try
        {
            await FlushAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The error is retained in JobWriteState by FlushAsync and observed by
            // the next actor publication or terminal flush.
        }
    }

    private Exception? GetLastError(Guid id)
    {
        lock (_sync)
            return _states.TryGetValue(id, out JobWriteState? state) ? state.LastError : null;
    }

    private sealed class JobWriteState
    {
        public long LastAcceptedRevision;
        public SimulationJobSnapshot? Pending;
        public bool Writing;
        public bool Terminal;
        public Task? Worker;
        public Exception? LastError;
    }
}
