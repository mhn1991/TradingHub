using System.Collections.Concurrent;
using Simulator.Models;

namespace Dashboard.Live;

/// <summary>
/// Lets <c>POST /api/simulations</c> return a job id immediately even when auto-calibration
/// training has to run first. Training used to happen inline in the request handler, which
/// blocked the HTTP connection for the full training duration (minutes, worse under load since
/// it competes for the same CPU as running simulations) with zero visibility - no job id existed
/// yet, so there was nothing to poll. This registers a placeholder id and snapshot up front, runs
/// training in the background against its own long-lived <see cref="CancellationTokenSource"/>
/// (deliberately not the request's own token, which is cancelled the moment the HTTP response is
/// sent), and once training resolves to a real simulation id, transparently forwards
/// <see cref="TryGetSnapshotAsync"/> to that job so callers can keep polling the same id they were
/// first given all the way through to completion.
/// </summary>
public sealed class PendingAutoCalibrationTracker
{
    private sealed class Entry
    {
        public required SimulationJobSnapshot Placeholder { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Guid? ResolvedSimulationId { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Guid Register(SimulationJobSnapshot placeholder, out CancellationToken cancellationToken)
    {
        PruneStaleEntries();
        var cts = new CancellationTokenSource();
        _entries[placeholder.Id] = new Entry { Placeholder = placeholder, Cts = cts };
        cancellationToken = cts.Token;
        return placeholder.Id;
    }

    public void Resolve(Guid placeholderId, Guid realSimulationId)
    {
        if (_entries.TryGetValue(placeholderId, out Entry? entry))
        {
            entry.ResolvedSimulationId = realSimulationId;
            entry.ResolvedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Fail(Guid placeholderId, string error)
    {
        if (_entries.TryGetValue(placeholderId, out Entry? entry))
        {
            entry.Error = error;
            entry.ResolvedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>True if this id names a placeholder that hasn't resolved to a real job yet -
    /// the caller should cancel the tracker's own token instead of calling through to the
    /// simulation service, since there is no service-side job to cancel.</summary>
    public bool TryCancelPending(Guid id, out SimulationJobSnapshot? cancelledSnapshot)
    {
        cancelledSnapshot = null;
        if (!_entries.TryGetValue(id, out Entry? entry) || entry.ResolvedSimulationId is not null)
            return false;

        entry.Cts.Cancel();
        entry.Error = "Cancelled before training completed.";
        entry.ResolvedAt = DateTimeOffset.UtcNow;
        cancelledSnapshot = entry.Placeholder with
        {
            Status = SimulationJobStatus.Cancelled,
            DataSourceStatus = "Cancelled",
            CompletedAt = entry.ResolvedAt,
            IsComplete = true
        };
        return true;
    }

    /// <summary>Resolves an id that might be a pending/failed/resolved placeholder, or an
    /// ordinary job id the tracker has never seen. Returns null only in the last case, so the
    /// caller can fall back to its normal "not found" handling.</summary>
    public async Task<SimulationJobSnapshot?> TryGetSnapshotAsync(
        Guid id,
        Func<Guid, CancellationToken, Task<SimulationJobSnapshot?>> resolveRealSnapshot,
        CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(id, out Entry? entry))
            return null;

        if (entry.ResolvedSimulationId is Guid realId)
            return await resolveRealSnapshot(realId, cancellationToken).ConfigureAwait(false);

        if (entry.Error is string error)
        {
            return entry.Placeholder with
            {
                Status = SimulationJobStatus.Failed,
                DataSourceStatus = "TrainingFailed",
                Error = error,
                CompletedAt = entry.ResolvedAt,
                IsComplete = true
            };
        }

        return entry.Placeholder;
    }

    public IReadOnlyList<SimulationJobSnapshot> ListPending()
    {
        return _entries.Values
            .Where(entry => entry.ResolvedSimulationId is null)
            .Select(entry => entry.Error is null
                ? entry.Placeholder
                : entry.Placeholder with { Status = SimulationJobStatus.Failed, Error = entry.Error })
            .ToArray();
    }

    private void PruneStaleEntries()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        foreach ((Guid id, Entry entry) in _entries)
        {
            if (entry.ResolvedAt is DateTimeOffset resolvedAt && resolvedAt < cutoff)
                _entries.TryRemove(id, out _);
        }
    }
}
