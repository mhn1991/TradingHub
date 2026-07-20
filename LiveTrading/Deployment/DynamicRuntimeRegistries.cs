using System.Collections.Concurrent;
using Brokers.Models;
using LiveTrading.Configuration;
using TradingPolicies;

namespace LiveTrading.Deployment;

public readonly record struct BrokerInstrumentRuntimeKey(Guid BrokerAccountId, long InstrumentId);

public readonly record struct AnalysisRuntimeKey(long InstrumentId, string AnnotationProfileHash);

public sealed record RuntimeReferenceState(int ConsumerCount, bool Ready, DateTimeOffset? ReadyAt);

/// <summary>Reference-counted account/instrument market feeds. The broker adapter owns the stream;
/// this registry owns its sharing and lifetime.</summary>
public sealed class MarketSubscriptionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<BrokerInstrumentRuntimeKey, HashSet<string>> _consumers = [];

    public RuntimeReferenceState Acquire(BrokerInstrumentRuntimeKey key, string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        lock (_sync)
        {
            if (!_consumers.TryGetValue(key, out HashSet<string>? consumers))
                _consumers[key] = consumers = [];
            consumers.Add(consumerId);
            return new RuntimeReferenceState(consumers.Count, true, DateTimeOffset.UtcNow);
        }
    }

    public int Release(BrokerInstrumentRuntimeKey key, string consumerId)
    {
        lock (_sync)
        {
            if (!_consumers.TryGetValue(key, out HashSet<string>? consumers))
                return 0;
            consumers.Remove(consumerId);
            if (consumers.Count == 0)
                _consumers.Remove(key);
            return consumers.Count;
        }
    }

    public RuntimeReferenceState Get(BrokerInstrumentRuntimeKey key)
    {
        lock (_sync)
            return new RuntimeReferenceState(_consumers.GetValueOrDefault(key)?.Count ?? 0,
                _consumers.ContainsKey(key), null);
    }
}

/// <summary>Shares immutable analysis snapshots only when instrument and full profile hash match.</summary>
public sealed class AnalysisRuntimeRegistry
{
    private sealed class Entry(IReadOnlySet<BarInterval> intervals)
    {
        public readonly HashSet<string> Consumers = [];
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = intervals;
        public bool Ready { get; set; }
        public DateTimeOffset? ReadyAt { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<AnalysisRuntimeKey, Entry> _entries = [];

    public RuntimeReferenceState Acquire(AnalysisRuntimeKey key, IReadOnlySet<BarInterval> intervals,
        string consumerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.AnnotationProfileHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerId);
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
                _entries[key] = entry = new Entry(intervals);
            else if (!entry.RequiredIntervals.SetEquals(intervals))
                throw new InvalidOperationException("Analysis profile hash was reused with different intervals.");
            entry.Consumers.Add(consumerId);
            return State(entry);
        }
    }

    public void MarkReady(AnalysisRuntimeKey key, DateTimeOffset readyAt)
    {
        lock (_sync)
        {
            Entry entry = _entries.GetValueOrDefault(key)
                ?? throw new KeyNotFoundException($"Analysis runtime '{key}' is not registered.");
            entry.Ready = true;
            entry.ReadyAt = readyAt;
        }
    }

    public int Release(AnalysisRuntimeKey key, string consumerId)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
                return 0;
            entry.Consumers.Remove(consumerId);
            if (entry.Consumers.Count == 0)
                _entries.Remove(key);
            return entry.Consumers.Count;
        }
    }

    public RuntimeReferenceState Get(AnalysisRuntimeKey key)
    {
        lock (_sync)
            return _entries.TryGetValue(key, out Entry? entry)
                ? State(entry)
                : new RuntimeReferenceState(0, false, null);
    }

    private static RuntimeReferenceState State(Entry entry) =>
        new(entry.Consumers.Count, entry.Ready, entry.ReadyAt);
}

public enum DynamicAgentRuntimeStatus
{
    Staged,
    Running,
    Paused,
    Draining,
    Stopped,
    Faulted
}

public sealed record DynamicAgentRuntimeState
{
    public required Guid DeploymentAgentId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required long InstrumentId { get; init; }
    public required StrategyActivationMode Mode { get; init; }
    public required ResolvedAgentPackage Package { get; init; }
    public required DynamicAgentRuntimeStatus Status { get; init; }
    public required long AppliedEpoch { get; init; }
    public string? FaultCode { get; init; }
}

/// <summary>Stages lifecycle mutations and applies them atomically at an explicit decision epoch.</summary>
public sealed class AgentRuntimeRegistry
{
    private enum MutationKind { Start, Pause, Resume, Drain, Stop, Replace, Fault }
    private sealed record Mutation(MutationKind Kind, DynamicAgentRuntimeState State);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, DynamicAgentRuntimeState> _active = [];
    private readonly ConcurrentQueue<Mutation> _pending = new();

    public void StageStart(DynamicAgentRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state.Package);
        if (!string.Equals(state.Package.PackageHash, state.Package.PackageHash.ToLowerInvariant(),
                StringComparison.Ordinal))
            throw new ArgumentException("Package hash must be canonical lower-case hex.", nameof(state));
        _pending.Enqueue(new Mutation(MutationKind.Start, state with { Status = DynamicAgentRuntimeStatus.Staged }));
    }

    public void StagePause(Guid id) => StageExisting(id, MutationKind.Pause);
    public void StageResume(Guid id) => StageExisting(id, MutationKind.Resume);
    public void StageDrain(Guid id) => StageExisting(id, MutationKind.Drain);
    public void StageStop(Guid id) => StageExisting(id, MutationKind.Stop);

    public void StageReplacement(Guid id, ResolvedAgentPackage replacement)
    {
        lock (_sync)
        {
            DynamicAgentRuntimeState current = Find(id);
            _pending.Enqueue(new Mutation(MutationKind.Replace,
                current with { Package = replacement, Status = DynamicAgentRuntimeStatus.Staged }));
        }
    }

    public void StageFault(Guid id, string faultCode)
    {
        lock (_sync)
        {
            DynamicAgentRuntimeState current = Find(id);
            _pending.Enqueue(new Mutation(MutationKind.Fault, current with { FaultCode = faultCode }));
        }
    }

    public IReadOnlyList<DynamicAgentRuntimeState> ApplyDecisionEpochBoundary(long epoch)
    {
        var changed = new List<DynamicAgentRuntimeState>();
        lock (_sync)
        {
            while (_pending.TryDequeue(out Mutation? mutation))
            {
                DynamicAgentRuntimeState next = mutation.State with
                {
                    Status = mutation.Kind switch
                    {
                        MutationKind.Start or MutationKind.Resume or MutationKind.Replace => DynamicAgentRuntimeStatus.Running,
                        MutationKind.Pause => DynamicAgentRuntimeStatus.Paused,
                        MutationKind.Drain => DynamicAgentRuntimeStatus.Draining,
                        MutationKind.Stop => DynamicAgentRuntimeStatus.Stopped,
                        MutationKind.Fault => DynamicAgentRuntimeStatus.Faulted,
                        _ => throw new ArgumentOutOfRangeException()
                    },
                    AppliedEpoch = epoch
                };
                _active[next.DeploymentAgentId] = next;
                changed.Add(next);
            }
        }
        return changed;
    }

    public DynamicAgentRuntimeState? Get(Guid id)
    {
        lock (_sync)
            return _active.GetValueOrDefault(id);
    }

    public IReadOnlyList<DynamicAgentRuntimeState> Snapshot()
    {
        lock (_sync)
            return _active.Values.OrderBy(x => x.DeploymentAgentId).ToArray();
    }

    private void StageExisting(Guid id, MutationKind kind)
    {
        lock (_sync)
            _pending.Enqueue(new Mutation(kind, Find(id)));
    }

    private DynamicAgentRuntimeState Find(Guid id) => _active.GetValueOrDefault(id)
        ?? throw new KeyNotFoundException($"Deployment agent '{id}' is not active.");
}

/// <summary>Process-local fast guard complementing the database's authoritative partial unique index.</summary>
public sealed class ExecutionOwnershipRegistry
{
    private readonly ConcurrentDictionary<BrokerInstrumentRuntimeKey, Guid> _owners = new();

    public bool TryAcquire(BrokerInstrumentRuntimeKey key, Guid deploymentAgentId, StrategyActivationMode mode,
        out Guid? conflictingOwner)
    {
        if (mode is StrategyActivationMode.ObserveOnly or StrategyActivationMode.Shadow)
        {
            conflictingOwner = null;
            return true;
        }
        Guid owner = _owners.GetOrAdd(key, deploymentAgentId);
        conflictingOwner = owner == deploymentAgentId ? null : owner;
        return conflictingOwner is null;
    }

    public bool IsAvailable(BrokerInstrumentRuntimeKey key, Guid? deploymentAgentId = null) =>
        !_owners.TryGetValue(key, out Guid owner) || owner == deploymentAgentId;

    public bool Release(BrokerInstrumentRuntimeKey key, Guid deploymentAgentId) =>
        _owners.TryRemove(new KeyValuePair<BrokerInstrumentRuntimeKey, Guid>(key, deploymentAgentId));

    public bool TryTransfer(BrokerInstrumentRuntimeKey key, Guid currentOwner, Guid replacementOwner)
    {
        if (!_owners.TryGetValue(key, out Guid owner))
            return _owners.TryAdd(key, replacementOwner);
        return owner == currentOwner && _owners.TryUpdate(key, replacementOwner, currentOwner);
    }
}

public sealed record PositionManagementPin(string PositionId, Guid DeploymentAgentId,
    Guid PolicyRevisionId, string PackageHash, bool IsFlat, bool IsReconciled);

/// <summary>Keeps position management pinned to the entry package until flat and reconciled.</summary>
public sealed class PositionManagementRegistry
{
    private readonly ConcurrentDictionary<string, PositionManagementPin> _positions =
        new(StringComparer.Ordinal);

    public PositionManagementPin Pin(string positionId, Guid deploymentAgentId, ResolvedAgentPackage package) =>
        _positions.AddOrUpdate(positionId,
            _ => new PositionManagementPin(positionId, deploymentAgentId, package.PolicyRevisionId,
                package.PackageHash, false, false),
            (_, current) => current.DeploymentAgentId == deploymentAgentId &&
                            current.PolicyRevisionId == package.PolicyRevisionId
                ? current
                : throw new InvalidOperationException($"Position '{positionId}' is already pinned."));

    public bool MarkFlatAndReconciled(string positionId, bool isFlat, bool isReconciled)
    {
        if (!_positions.TryGetValue(positionId, out PositionManagementPin? current))
            return false;
        PositionManagementPin next = current with { IsFlat = isFlat, IsReconciled = isReconciled };
        if (isFlat && isReconciled)
            return _positions.TryRemove(new KeyValuePair<string, PositionManagementPin>(positionId, current));
        return _positions.TryUpdate(positionId, next, current);
    }

    public bool HasManagedPositions(Guid deploymentAgentId) =>
        _positions.Values.Any(x => x.DeploymentAgentId == deploymentAgentId);

    public IReadOnlyList<PositionManagementPin> Snapshot() =>
        _positions.Values.OrderBy(x => x.PositionId, StringComparer.Ordinal).ToArray();
}

internal static class IntervalSetExtensions
{
    public static bool SetEquals(this IReadOnlySet<BarInterval> left, IReadOnlySet<BarInterval> right) =>
        left.Count == right.Count && left.All(right.Contains);
}
