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
        lock (_sync)
        {
            if (!_states.TryGetValue(snapshot.Id, out state!))
            {
                state = new JobWriteState();
                _states[snapshot.Id] = state;
            }

            if (snapshot.Revision < state.LastAcceptedRevision)
                return;

            state.LastAcceptedRevision = snapshot.Revision;
            state.Pending = snapshot;
            state.Terminal |= terminal;
        }

        if (terminal)
        {
            await FlushAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Fire a single worker if idle; it will loop while newer pending exists.
        _ = Task.Run(() => WorkerAsync(snapshot.Id), CancellationToken.None);
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
            finally
            {
                lock (_sync)
                {
                    if (_states.TryGetValue(id, out JobWriteState? state))
                    {
                        state.Writing = false;
                        if (state.Pending is null && state.Terminal)
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
            // Observed via next Get/terminal flush; non-terminal progress failures are non-fatal.
        }
    }

    private sealed class JobWriteState
    {
        public long LastAcceptedRevision;
        public SimulationJobSnapshot? Pending;
        public bool Writing;
        public bool Terminal;
    }
}
