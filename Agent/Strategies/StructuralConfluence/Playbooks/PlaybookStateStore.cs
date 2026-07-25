using Brokers.Models;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public sealed class PlaybookStateStore(int capacity = 256)
{
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
        PlaybookEvaluation evaluation)
    {
        lock (_gate)
        {
            var key = (instrument, playbookId);
            PlaybookRuntimeState current = _states.GetValueOrDefault(key, new PlaybookRuntimeState());
            if (availableAt < current.LastAvailableAt ||
                (availableAt == current.LastAvailableAt && snapshotVersion <= current.LastSnapshotVersion))
                return false;

            _states[key] = current with
            {
                Lifecycle = evaluation.Lifecycle,
                SetupId = evaluation.SetupId,
                LastAvailableAt = availableAt,
                LastSnapshotVersion = snapshotVersion,
                ArmedAt = current.SetupId == evaluation.SetupId ? current.ArmedAt : availableAt,
                ExpiresAt = evaluation.ExpiresAt,
                LastEvaluation = evaluation,
                LastReadySetupId = evaluation.IsReady ? evaluation.SetupId : current.LastReadySetupId,
                LastReadyCatalystAt = evaluation.IsReady ? evaluation.CatalystAt : current.LastReadyCatalystAt
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
}
