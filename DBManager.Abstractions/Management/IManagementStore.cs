namespace DBManager.Abstractions.Management;

/// <summary>Position management, safety, operator-command, checkpoint and account-snapshot persistence (Phase 5).</summary>
public interface IManagementStore
{
    Task<DurableResult> RecordManagementEventAsync(RecordManagementEvent command, CancellationToken cancellationToken);

    Task<ManagementStateDetail?> GetManagementStateAsync(Guid positionId, CancellationToken cancellationToken);

    Task<SafetyEventRecorded> RecordSafetyEventAsync(RecordSafetyEvent command, CancellationToken cancellationToken);

    Task<DurableResult> ResolveSafetyEventAsync(ResolveSafetyEvent command, CancellationToken cancellationToken);

    /// <summary>
    /// True if the latest unresolved safety event for this deployment carries a pausing directive
    /// (<see cref="SafetyDirective.PauseEntries"/> or <see cref="SafetyDirective.FlattenAll"/>).
    /// </summary>
    Task<bool> IsEntriesPausedAsync(Guid deploymentId, CancellationToken cancellationToken);

    Task<DurableResult> RecordOperatorCommandAsync(RecordOperatorCommand command, CancellationToken cancellationToken);

    Task<DurableResult> CompleteOperatorCommandAsync(CompleteOperatorCommand command, CancellationToken cancellationToken);

    Task<OperatorCommandDetail?> GetOperatorCommandAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<DurableResult> SaveCheckpointAsync(SaveCheckpoint command, CancellationToken cancellationToken);

    Task<CheckpointDetail?> LoadLatestCheckpointAsync(
        Guid deploymentId, CheckpointType checkpointType, CancellationToken cancellationToken);

    Task<DurableResult> RecordAccountSnapshotAsync(RecordAccountSnapshot command, CancellationToken cancellationToken);
}
