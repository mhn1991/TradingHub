using DBManager.Abstractions.Management;
using DBManager.Abstractions.Operations;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Operations;

/// <summary>
/// Cursor advancement must occur in the same transaction as broker-event projection updates
/// (section 7.8 prose).
/// </summary>
public sealed class BrokerStreamCursorEntity
{
    public Guid BrokerAccountId { get; set; }
    public string? LastAppliedTransactionId { get; set; }
    public DateTimeOffset? LastAppliedBrokerTime { get; set; }
    public long ConnectionGeneration { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ReconciliationRunEntity
{
    public Guid ReconciliationId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public ReconciliationTrigger Trigger { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? BrokerSnapshotTime { get; set; }
    public ReconciliationRunStatus Status { get; set; }
    public bool EntriesPaused { get; set; }
    public string? SummaryJson { get; set; }
}

public sealed class ReconciliationDifferenceEntity
{
    public Guid DifferenceId { get; set; }
    public Guid ReconciliationId { get; set; }
    public ReconciliationDifferenceType DifferenceType { get; set; }
    public ReconciliationSeverity Severity { get; set; }
    public long? InstrumentId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? PositionId { get; set; }
    public required string LocalValueJson { get; set; }
    public required string BrokerValueJson { get; set; }
    public ReconciliationAction? Action { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}

/// <summary>
/// The visible projection only — the actual process-exclusion mechanism is a session-level
/// PostgreSQL advisory lock held on a dedicated connection (section 7.8).
/// </summary>
public sealed class AccountLeaseEntity
{
    public Guid BrokerAccountId { get; set; }
    public required string HostInstanceId { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public AccountLeaseStatus Status { get; set; }
}

public sealed class SafetyEventEntity
{
    public long EventId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public OperationsSeverity Severity { get; set; }
    public SafetySource Source { get; set; }
    public SafetyDirective Directive { get; set; }
    public required string ReasonCode { get; set; }
    public long? InstrumentId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? PositionId { get; set; }
    public required string AutomaticActionJson { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}

public sealed class OperatorCommandEntity
{
    public Guid OperatorCommandId { get; set; }
    public required string IdempotencyKey { get; set; }
    public Guid DeploymentId { get; set; }
    public required string OperatorIdentity { get; set; }
    public OperatorCommandType CommandType { get; set; }
    public string? TargetId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public OperatorCommandStatus Status { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class CheckpointEntity
{
    public Guid CheckpointId { get; set; }
    public Guid DeploymentId { get; set; }
    public CheckpointType CheckpointType { get; set; }
    public long Sequence { get; set; }
    public required string ContentHash { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AccountSnapshotEntity
{
    public long SnapshotId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public AccountSnapshotTrigger Trigger { get; set; }
    public DateTimeOffset SnapshotTime { get; set; }
    public decimal Balance { get; set; }
    public decimal Equity { get; set; }
    public decimal UnrealisedPnl { get; set; }
    public decimal RealisedPnl { get; set; }
    public decimal MarginUsed { get; set; }
    public decimal MarginAvailable { get; set; }
    public decimal? MarginCloseoutRatio { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

internal static class OperationsModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BrokerStreamCursorEntity>(entity =>
        {
            entity.ToTable("broker_stream_cursors", "operations");
            entity.HasKey(e => e.BrokerAccountId);
            entity.Property(e => e.LastAppliedBrokerTime).HasColumnType("timestamptz");
            entity.Property(e => e.LastHeartbeatAt).HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ReconciliationRunEntity>(entity =>
        {
            entity.ToTable("reconciliation_runs", "operations");
            entity.HasKey(e => e.ReconciliationId);
            entity.Property(e => e.Trigger).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.Property(e => e.BrokerSnapshotTime).HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.SummaryJson).HasColumnName("summary").HasColumnType("jsonb");
        });

        modelBuilder.Entity<ReconciliationDifferenceEntity>(entity =>
        {
            entity.ToTable("reconciliation_differences", "operations");
            entity.HasKey(e => e.DifferenceId);
            entity.Property(e => e.DifferenceType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Severity).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.LocalValueJson).HasColumnName("local_value").HasColumnType("jsonb");
            entity.Property(e => e.BrokerValueJson).HasColumnName("broker_value").HasColumnType("jsonb");
            entity.Property(e => e.Action).HasColumnType("smallint").HasConversion<short?>();
            entity.Property(e => e.ResolvedAt).HasColumnType("timestamptz");
            entity.HasOne<ReconciliationRunEntity>()
                .WithMany()
                .HasForeignKey(e => e.ReconciliationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountLeaseEntity>(entity =>
        {
            entity.ToTable("account_leases", "operations");
            entity.HasKey(e => e.BrokerAccountId);
            entity.Property(e => e.AcquiredAt).HasColumnType("timestamptz");
            entity.Property(e => e.HeartbeatAt).HasColumnType("timestamptz");
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
        });

        modelBuilder.Entity<SafetyEventEntity>(entity =>
        {
            entity.ToTable("safety_events", "operations");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.Severity).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Source).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Directive).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.AutomaticActionJson).HasColumnName("automatic_action").HasColumnType("jsonb");
            entity.Property(e => e.ResolvedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.DeploymentId, e.OccurredAt });
        });

        modelBuilder.Entity<OperatorCommandEntity>(entity =>
        {
            entity.ToTable("operator_commands", "operations");
            entity.HasKey(e => e.OperatorCommandId);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.Property(e => e.CommandType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RequestedAt).HasColumnType("timestamptz");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ResultJson).HasColumnName("result").HasColumnType("jsonb");
        });

        modelBuilder.Entity<CheckpointEntity>(entity =>
        {
            entity.ToTable("checkpoints", "operations");
            entity.HasKey(e => e.CheckpointId);
            entity.HasIndex(e => new { e.DeploymentId, e.CheckpointType, e.Sequence }).IsUnique();
            entity.Property(e => e.CheckpointType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AccountSnapshotEntity>(entity =>
        {
            entity.ToTable("account_snapshots", "operations");
            entity.HasKey(e => e.SnapshotId);
            entity.Property(e => e.SnapshotId).UseIdentityAlwaysColumn();
            entity.Property(e => e.Trigger).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.SnapshotTime).HasColumnType("timestamptz");
            entity.Property(e => e.Balance).HasColumnType("numeric(28,8)");
            entity.Property(e => e.Equity).HasColumnType("numeric(28,8)");
            entity.Property(e => e.UnrealisedPnl).HasColumnType("numeric(28,8)");
            entity.Property(e => e.RealisedPnl).HasColumnType("numeric(28,8)");
            entity.Property(e => e.MarginUsed).HasColumnType("numeric(28,8)");
            entity.Property(e => e.MarginAvailable).HasColumnType("numeric(28,8)");
            entity.Property(e => e.MarginCloseoutRatio).HasColumnType("numeric(18,10)");
            entity.Property(e => e.ReceivedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.BrokerAccountId, e.SnapshotTime });
        });
    }
}
