using DBManager.Abstractions.Decision;

namespace DBManager.Abstractions.Management;

/// <summary>
/// Records one management action and folds it into the <c>position_management_state</c>
/// projection, atomically. <see cref="BrokerCommandId"/>, when present, is the idempotency key —
/// replaying the same command id is a no-op rather than a duplicate stop/reduction
/// (section 30 Phase 5 acceptance: "close/reduce/stop actions are idempotent").
/// </summary>
public sealed record RecordManagementEvent
{
    public required Guid PositionId { get; init; }
    public required Guid ManagementPolicyRevisionId { get; init; }
    public required TriggerInterval EvaluationClock { get; init; }
    public required ManagementActionType ActionType { get; init; }
    public required string ReasonCode { get; init; }
    public decimal? ReferenceBid { get; init; }
    public decimal? ReferenceAsk { get; init; }
    public required decimal CurrentR { get; init; }
    public required decimal MfeR { get; init; }
    public required decimal MaeR { get; init; }
    public required decimal MfePrice { get; init; }
    public required decimal MaePrice { get; init; }
    public decimal? PreviousStop { get; init; }
    public decimal? RequestedStop { get; init; }
    public decimal? ConfirmedStop { get; init; }
    public decimal? RequestedReduction { get; init; }
    public decimal? ConfirmedReduction { get; init; }
    public Guid? BrokerCommandId { get; init; }
    public required string DetailsJson { get; init; }
    public required ManagementStage Stage { get; init; }
}

/// <summary>
/// No event ID here — <c>safety_events.event_id</c> is a server-generated identity column
/// (section 7.8), not a client-supplied key. <see cref="SafetyEventRecorded"/> returns it.
/// </summary>
public sealed record RecordSafetyEvent
{
    public required Guid DeploymentId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required OperationsSeverity Severity { get; init; }
    public required SafetySource Source { get; init; }
    public required SafetyDirective Directive { get; init; }
    public required string ReasonCode { get; init; }
    public long? InstrumentId { get; init; }
    public Guid? OrderId { get; init; }
    public Guid? PositionId { get; init; }
    public required string AutomaticActionJson { get; init; }
}

public sealed record ResolveSafetyEvent
{
    public required long EventId { get; init; }
    public required string ResolvedBy { get; init; }
}

/// <summary>
/// <see cref="IdempotencyKey"/> makes operator commands naturally idempotent and, combined with
/// the durable record itself, fully audited (section 30 Phase 5 acceptance).
/// </summary>
public sealed record RecordOperatorCommand
{
    public required Guid OperatorCommandId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Guid DeploymentId { get; init; }
    public required string OperatorIdentity { get; init; }
    public required OperatorCommandType CommandType { get; init; }
    public string? TargetId { get; init; }
    public required string Reason { get; init; }
}

public sealed record CompleteOperatorCommand
{
    public required Guid OperatorCommandId { get; init; }
    public required OperatorCommandStatus Status { get; init; }
    public required string ResultJson { get; init; }
}

public sealed record SaveCheckpoint
{
    public required Guid CheckpointId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required CheckpointType CheckpointType { get; init; }
    public required long Sequence { get; init; }
    public required string ContentHash { get; init; }
    public required string PayloadJson { get; init; }
}

public sealed record RecordAccountSnapshot
{
    public required Guid BrokerAccountId { get; init; }
    public required AccountSnapshotTrigger Trigger { get; init; }
    public required decimal Balance { get; init; }
    public required decimal Equity { get; init; }
    public required decimal UnrealisedPnl { get; init; }
    public required decimal RealisedPnl { get; init; }
    public required decimal MarginUsed { get; init; }
    public required decimal MarginAvailable { get; init; }
    public decimal? MarginCloseoutRatio { get; init; }
}
