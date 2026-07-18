using DBManager.Abstractions;

namespace DBManager.Abstractions.Management;

public sealed record ManagementStateDetail
{
    public required Guid PositionId { get; init; }
    public required ManagementStage Stage { get; init; }
    public required decimal MfeR { get; init; }
    public required decimal MaeR { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required long Version { get; init; }
}

public sealed record CheckpointDetail
{
    public required Guid CheckpointId { get; init; }
    public required long Sequence { get; init; }
    public required string ContentHash { get; init; }
    public required string PayloadJson { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record OperatorCommandDetail
{
    public required Guid OperatorCommandId { get; init; }
    public required OperatorCommandStatus Status { get; init; }
    public required string ResultJson { get; init; }
}

public sealed record SafetyEventRecorded
{
    public required DurableResult Result { get; init; }
    public long? EventId { get; init; }
}
