namespace DBManager.Abstractions.Operations;

/// <summary>Section 27: "A backup is not valid until it has been restored and verified."</summary>
public enum RestoreTestStatus
{
    NotTested,
    Passed,
    Failed
}

public sealed record RecordBackupStarted
{
    public required Guid BackupId { get; init; }
    public required string FilePath { get; init; }
}

public sealed record RecordBackupCompleted
{
    public required Guid BackupId { get; init; }
    public required string PostgreSqlVersion { get; init; }
    public required string SchemaVersion { get; init; }
    public required string Checksum { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset RetentionExpiresAt { get; init; }
}

public sealed record RecordBackupFailed
{
    public required Guid BackupId { get; init; }
    public required string FailureReason { get; init; }
}

public sealed record RecordRestoreTest
{
    public required Guid BackupId { get; init; }
    public required RestoreTestStatus Status { get; init; }
    public string? FailureDetail { get; init; }
}

public sealed record BackupRecordDetail
{
    public required Guid BackupId { get; init; }
    public required string FilePath { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Checksum { get; init; }
    public required RestoreTestStatus RestoreTestStatus { get; init; }
    public DateTimeOffset? RestoreTestedAt { get; init; }
    public DateTimeOffset? RetentionExpiresAt { get; init; }
}

public interface IBackupStore
{
    Task RecordBackupStartedAsync(RecordBackupStarted command, CancellationToken cancellationToken);

    Task RecordBackupCompletedAsync(RecordBackupCompleted command, CancellationToken cancellationToken);

    Task RecordBackupFailedAsync(RecordBackupFailed command, CancellationToken cancellationToken);

    Task RecordRestoreTestAsync(RecordRestoreTest command, CancellationToken cancellationToken);

    /// <summary>The most recent backup that has actually been restored and verified (section 27).</summary>
    Task<BackupRecordDetail?> GetLatestVerifiedBackupAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<BackupRecordDetail>> GetExpiredBackupsAsync(CancellationToken cancellationToken);
}
