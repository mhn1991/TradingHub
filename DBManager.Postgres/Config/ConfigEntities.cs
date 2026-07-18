using DBManager.Abstractions.Config;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Config;

public sealed class PolicyProfileEntity
{
    public Guid PolicyId { get; set; }
    public required string StrategyId { get; set; }
    public required string StrategyVersion { get; set; }
    public required string FeatureSchemaHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public string? Description { get; set; }
}

/// <summary>Immutable once inserted — no code path updates <see cref="PolicyDocumentJson"/>.</summary>
public sealed class PolicyRevisionEntity
{
    public Guid PolicyRevisionId { get; set; }
    public Guid PolicyId { get; set; }
    public int Revision { get; set; }
    public required string ConfigurationHash { get; set; }
    public PolicyRevisionStatus Status { get; set; }
    public required string PolicyDocumentJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>Append-only promotion history for a policy revision (section 7.2 prose).</summary>
public sealed class PolicyPromotionEventEntity
{
    public long EventId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public PolicyRevisionStatus FromStatus { get; set; }
    public PolicyRevisionStatus ToStatus { get; set; }
    public required string ActorIdentity { get; set; }
    public string? Reason { get; set; }
}

public sealed class CalibrationArtifactEntity
{
    public Guid ArtifactId { get; set; }
    public ArtifactRole ArtifactType { get; set; }
    public required string StrategyId { get; set; }
    public required string StrategyVersion { get; set; }
    public required string FeatureSchemaHash { get; set; }
    public required string ContentHash { get; set; }
    public required string StorageUri { get; set; }
    public CalibrationArtifactStatus Status { get; set; }
    public DateTimeOffset TrainingFrom { get; set; }
    public DateTimeOffset TrainingTo { get; set; }
    public DateTimeOffset ValidationFrom { get; set; }
    public DateTimeOffset ValidationTo { get; set; }
    public DateTimeOffset? TestFrom { get; set; }
    public DateTimeOffset? TestTo { get; set; }
    public long SampleCount { get; set; }
    public required string MetricsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
}

public sealed class PolicyArtifactEntity
{
    public Guid PolicyRevisionId { get; set; }
    public Guid ArtifactId { get; set; }
    public ArtifactRole Role { get; set; }
}

public sealed class DeploymentEntity
{
    public Guid DeploymentId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public required string HostInstanceId { get; set; }
    public ExecutionMode ExecutionMode { get; set; }
    public DeploymentStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public required string StartedBy { get; set; }
    public string? StopReason { get; set; }
    public required string DeploymentHash { get; set; }
}

/// <summary>Append-only activation/close history (section 13.4 "append activation event").</summary>
public sealed class DeploymentActivationEventEntity
{
    public long EventId { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string EventType { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public required string ActorIdentity { get; set; }
    public string? Reason { get; set; }
}

public sealed class DeploymentAssignmentEntity
{
    public Guid DeploymentId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public AgentMode AgentMode { get; set; }
    public bool Enabled { get; set; }
}

internal static class ConfigModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PolicyProfileEntity>(entity =>
        {
            entity.ToTable("policy_profiles", "config");
            entity.HasKey(e => e.PolicyId);
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.StrategyVersion).HasColumnType("text");
            entity.Property(e => e.FeatureSchemaHash).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnType("text");
            entity.Property(e => e.Description).HasColumnType("text");
        });

        modelBuilder.Entity<PolicyRevisionEntity>(entity =>
        {
            entity.ToTable("policy_revisions", "config");
            entity.HasKey(e => e.PolicyRevisionId);
            entity.HasIndex(e => new { e.PolicyId, e.Revision }).IsUnique();
            entity.Property(e => e.ConfigurationHash).HasColumnType("text");
            entity.HasIndex(e => e.ConfigurationHash).IsUnique();
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.PolicyDocumentJson).HasColumnName("policy_document").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnType("text");
            entity.Property(e => e.ApprovedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnType("text");
            entity.Property(e => e.RetiredAt).HasColumnType("timestamptz");
            entity.HasOne<PolicyProfileEntity>()
                .WithMany()
                .HasForeignKey(e => e.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PolicyPromotionEventEntity>(entity =>
        {
            entity.ToTable("policy_promotion_events", "config");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.FromStatus).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ToStatus).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ActorIdentity).HasColumnType("text");
            entity.Property(e => e.Reason).HasColumnType("text");
            entity.HasIndex(e => new { e.PolicyRevisionId, e.OccurredAt });
        });

        modelBuilder.Entity<CalibrationArtifactEntity>(entity =>
        {
            entity.ToTable("calibration_artifacts", "config");
            entity.HasKey(e => e.ArtifactId);
            entity.Property(e => e.ArtifactType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.StrategyVersion).HasColumnType("text");
            entity.Property(e => e.FeatureSchemaHash).HasColumnType("text");
            entity.Property(e => e.ContentHash).HasColumnType("text");
            entity.HasIndex(e => e.ContentHash).IsUnique();
            entity.Property(e => e.StorageUri).HasColumnType("text");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.TrainingFrom).HasColumnType("timestamptz");
            entity.Property(e => e.TrainingTo).HasColumnType("timestamptz");
            entity.Property(e => e.ValidationFrom).HasColumnType("timestamptz");
            entity.Property(e => e.ValidationTo).HasColumnType("timestamptz");
            entity.Property(e => e.TestFrom).HasColumnType("timestamptz");
            entity.Property(e => e.TestTo).HasColumnType("timestamptz");
            entity.Property(e => e.MetricsJson).HasColumnName("metrics").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnType("text");
        });

        modelBuilder.Entity<PolicyArtifactEntity>(entity =>
        {
            entity.ToTable("policy_artifacts", "config");
            entity.HasKey(e => new { e.PolicyRevisionId, e.Role });
            entity.Property(e => e.Role).HasColumnType("smallint").HasConversion<short>();
            entity.HasOne<PolicyRevisionEntity>()
                .WithMany()
                .HasForeignKey(e => e.PolicyRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CalibrationArtifactEntity>()
                .WithMany()
                .HasForeignKey(e => e.ArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentEntity>(entity =>
        {
            entity.ToTable("deployments", "config");
            entity.HasKey(e => e.DeploymentId);
            entity.Property(e => e.HostInstanceId).HasColumnType("text");
            entity.Property(e => e.ExecutionMode).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StoppedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StartedBy).HasColumnType("text");
            entity.Property(e => e.StopReason).HasColumnType("text");
            entity.Property(e => e.DeploymentHash).HasColumnType("text");
            entity.HasIndex(e => e.BrokerAccountId)
                .HasFilter("status = 0")
                .IsUnique()
                .HasDatabaseName("ix_deployments_one_active_per_account");
            entity.HasOne<Reference.BrokerAccountEntity>()
                .WithMany()
                .HasForeignKey(e => e.BrokerAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PolicyRevisionEntity>()
                .WithMany()
                .HasForeignKey(e => e.PolicyRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentActivationEventEntity>(entity =>
        {
            entity.ToTable("deployment_activation_events", "config");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.EventType).HasColumnType("text");
            entity.Property(e => e.ActorIdentity).HasColumnType("text");
            entity.Property(e => e.Reason).HasColumnType("text");
            entity.HasIndex(e => new { e.DeploymentId, e.OccurredAt });
        });

        modelBuilder.Entity<DeploymentAssignmentEntity>(entity =>
        {
            entity.ToTable("deployment_assignments", "config");
            entity.HasKey(e => new { e.DeploymentId, e.InstrumentId, e.StrategyId });
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.AgentMode).HasColumnType("smallint").HasConversion<short>();
            entity.HasOne<DeploymentEntity>()
                .WithMany()
                .HasForeignKey(e => e.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Reference.InstrumentEntity>()
                .WithMany()
                .HasForeignKey(e => e.InstrumentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
