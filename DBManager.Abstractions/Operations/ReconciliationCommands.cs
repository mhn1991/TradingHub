namespace DBManager.Abstractions.Operations;

public sealed record StartReconciliationRun
{
    public required Guid ReconciliationId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required ReconciliationTrigger Trigger { get; init; }
}

public sealed record CompleteReconciliationRun
{
    public required Guid ReconciliationId { get; init; }
    public required ReconciliationRunStatus Status { get; init; }
    public DateTimeOffset? BrokerSnapshotTime { get; init; }
    public required string SummaryJson { get; init; }
}

public sealed record RecordReconciliationDifference
{
    public required Guid DifferenceId { get; init; }
    public required Guid ReconciliationId { get; init; }
    public required ReconciliationDifferenceType DifferenceType { get; init; }
    public required ReconciliationSeverity Severity { get; init; }
    public long? InstrumentId { get; init; }
    public Guid? OrderId { get; init; }
    public Guid? PositionId { get; init; }
    public required string LocalValueJson { get; init; }
    public required string BrokerValueJson { get; init; }
}

/// <summary>
/// Section 13.3's repair transaction: insert difference (already recorded) → update projection →
/// append correction event → mark the difference resolved, all atomically. Every correction is an
/// explicit, audited call — there is no automatic-adoption code path.
/// </summary>
public sealed record ApplyReconciliationCorrection
{
    public required Guid DifferenceId { get; init; }
    public required ReconciliationAction Action { get; init; }
    public required string ResolvedBy { get; init; }
    public required string ResolutionNote { get; init; }
}
