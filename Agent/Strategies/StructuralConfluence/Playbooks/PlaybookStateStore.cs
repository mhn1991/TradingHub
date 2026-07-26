using Brokers.Models;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public sealed class PlaybookStateStore(int capacity = 256)
{
    private const int TerminalIdentityCapacity = 64;
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Dictionary<(InstrumentKey Instrument, string Playbook), PlaybookRuntimeState> _states = [];
    private readonly object _gate = new();

    public PlaybookRuntimeState Get(InstrumentKey instrument, string playbookId)
    {
        lock (_gate)
            return _states.GetValueOrDefault((instrument, playbookId), new PlaybookRuntimeState());
    }

    public bool TryAdvance(
        InstrumentKey instrument,
        string playbookId,
        DateTimeOffset availableAt,
        long snapshotVersion,
        PlaybookEvaluation evaluation,
        bool markReadyAsSignaled = true)
    {
        lock (_gate)
        {
            var key = (instrument, playbookId);
            PlaybookRuntimeState current = _states.GetValueOrDefault(key, new PlaybookRuntimeState());
            if (availableAt < current.LastAvailableAt ||
                (availableAt == current.LastAvailableAt && snapshotVersion <= current.LastSnapshotVersion))
                return false;

            IReadOnlyList<string> terminalCatalysts = current.TerminalCatalystIdentities;
            if (IsTerminal(evaluation.Lifecycle))
                terminalCatalysts = AppendTerminalIdentity(
                    terminalCatalysts, evaluation.CatalystIdentity);
            if (CurrentCatalystWasSuperseded(current, evaluation))
                terminalCatalysts = AppendTerminalIdentity(
                    terminalCatalysts, current.LastEvaluation!.CatalystIdentity);

            _states[key] = current with
            {
                Lifecycle = evaluation.Lifecycle,
                SetupId = evaluation.SetupId,
                LastAvailableAt = availableAt,
                LastSnapshotVersion = snapshotVersion,
                ArmedAt = current.SetupId == evaluation.SetupId ? current.ArmedAt : availableAt,
                ExpiresAt = evaluation.ExpiresAt,
                LastEvaluation = evaluation,
                LastReadySetupId = evaluation.IsReady && markReadyAsSignaled
                    ? evaluation.SetupId
                    : current.LastReadySetupId,
                LastReadyCatalystAt = evaluation.IsReady && markReadyAsSignaled
                    ? evaluation.CatalystAt
                    : current.LastReadyCatalystAt,
                LastReadyCatalystIdentity = evaluation.IsReady && markReadyAsSignaled
                    ? evaluation.CatalystIdentity
                    : current.LastReadyCatalystIdentity,
                TerminalCatalystIdentities = terminalCatalysts
            };

            if (_states.Count > _capacity)
            {
                var oldest = _states.OrderBy(item => item.Value.LastAvailableAt)
                    .ThenBy(item => item.Key.Instrument.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Key.Playbook, StringComparer.Ordinal)
                    .First().Key;
                _states.Remove(oldest);
            }

            return true;
        }
    }

    private static bool IsTerminal(Agent.Models.StructuralSetupLifecycle lifecycle) =>
        lifecycle is Agent.Models.StructuralSetupLifecycle.Invalidated or
            Agent.Models.StructuralSetupLifecycle.Expired;

    private static bool CurrentCatalystWasSuperseded(
        PlaybookRuntimeState current,
        PlaybookEvaluation next) =>
        current.LastEvaluation is
        {
            CatalystIdentity: not null,
            Lifecycle: Agent.Models.StructuralSetupLifecycle.Armed or
                Agent.Models.StructuralSetupLifecycle.CatalystObserved or
                Agent.Models.StructuralSetupLifecycle.AwaitingTrigger or
                Agent.Models.StructuralSetupLifecycle.CandidateProduced
        } previous &&
        next.CatalystIdentity is not null &&
        !string.Equals(
            previous.CatalystIdentity, next.CatalystIdentity, StringComparison.Ordinal);

    private static IReadOnlyList<string> AppendTerminalIdentity(
        IReadOnlyList<string> identities,
        string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity) || identities.Contains(identity, StringComparer.Ordinal))
            return identities;

        return identities.Count < TerminalIdentityCapacity
            ? [.. identities, identity]
            : [.. identities.Skip(1), identity];
    }
}
