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
    public string? PayloadJson { get; set; }
    public long ContentSizeBytes { get; set; }
    public required string MediaType { get; set; }
    public int SerializerVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
}

public sealed class CalibrationArtifactStatusEventEntity
{
    public long EventId { get; set; }
    public Guid ArtifactId { get; set; }
    public CalibrationArtifactStatus FromStatus { get; set; }
    public CalibrationArtifactStatus ToStatus { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string ActorIdentity { get; set; }
}

public sealed class CalibrationBundleCandidateEntity
{
    public Guid CandidateId { get; set; }
    public Guid SetupArtifactId { get; set; }
    public Guid MetaModelArtifactId { get; set; }
    public Guid ManagementArtifactId { get; set; }
    public required string ProposedProfileJson { get; set; }
    public short Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? ApprovedProfileId { get; set; }
    public int? ApprovedProfileRevision { get; set; }
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
    public Guid? PolicyRevisionId { get; set; }
    public required string HostInstanceId { get; set; }
    public string Environment { get; set; } = "unknown";
    public ExecutionMode ExecutionMode { get; set; }
    public DeploymentStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? PreparingAt { get; set; }
    public DateTimeOffset? WarmingUpAt { get; set; }
    public DateTimeOffset? RunningAt { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public DateTimeOffset? DrainingAt { get; set; }
    public DateTimeOffset? FaultedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? StartedBy { get; set; }
    public string RequestedBy { get; set; } = "legacy";
    public string? StopReason { get; set; }
    public required string DeploymentHash { get; set; }
    public long ConcurrencyToken { get; set; }
    public string? IdempotencyKey { get; set; }
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

public sealed class PolicyPermissionEventEntity
{
    public Guid PermissionEventId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public required string BrokerEnvironment { get; set; }
    public Guid? BrokerAccountId { get; set; }
    public PolicyExecutionPermission Permissions { get; set; }
    public bool Granted { get; set; }
    public required string ConfigurationHash { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string ActorIdentity { get; set; }
    public required string Reason { get; set; }
}

public sealed class ParityCertificationEntity
{
    public Guid CertificationId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string SourceCommit { get; set; }
    public required string RecordingHash { get; set; }
    public required string SimulatorBuildHash { get; set; }
    public required string LiveBuildHash { get; set; }
    public int ComparedEpochCount { get; set; }
    public int MismatchCount { get; set; }
    public required string MismatchDetailsJson { get; set; }
    public ParityCertificationStatus Status { get; set; }
    public DateTimeOffset CertifiedAt { get; set; }
    public required string CertifiedBy { get; set; }
}

public sealed class DeploymentAgentEntity
{
    public Guid DeploymentAgentId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public AgentMode AgentMode { get; set; }
    public DeploymentAgentStatus Status { get; set; }
    public bool Enabled { get; set; }
    public required string PackageHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public string? FaultCode { get; set; }
    public string? FaultMessage { get; set; }
}

public sealed class DeploymentCommandEntity
{
    public Guid CommandId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid? DeploymentAgentId { get; set; }
    public DeploymentCommandType CommandType { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public required string RequestedBy { get; set; }
    public long ExpectedVersion { get; set; }
    public required string PayloadJson { get; set; }
    public DeploymentCommandStatus Status { get; set; }
    public string? ClaimedByHost { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public required string IdempotencyKey { get; set; }
}

public sealed class DeploymentEventEntity
{
    public long EventId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid? DeploymentAgentId { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string ActorIdentity { get; set; }
    public required string CorrelationId { get; set; }
    public string? ReasonCode { get; set; }
    public string? DetailJson { get; set; }
}

public sealed class RuntimeProfileEntity
{
    public Guid RuntimeProfileId { get; set; }
    public RuntimeProfileKind ProfileKind { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

public sealed class RuntimeProfileRevisionEntity
{
    public Guid RuntimeProfileRevisionId { get; set; }
    public Guid RuntimeProfileId { get; set; }
    public int Revision { get; set; }
    public RuntimeProfileRevisionStatus Status { get; set; }
    public required string SettingsJson { get; set; }
    public required string SettingsHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
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
            entity.HasIndex(e => e.ConfigurationHash);
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            // text, not jsonb: TradingPolicyProfile.ConfigurationHash is validated against an exact
            // re-serialization of the stored document on every read, but Postgres's jsonb type does
            // not preserve object key order or number formatting (documented Postgres behavior) -
            // storing as jsonb made every promoted/approved profile fail its own hash check on the
            // very next read. text preserves the exact bytes written, matching the file-based store.
            entity.Property(e => e.PolicyDocumentJson).HasColumnName("policy_document").HasColumnType("text");
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
            // text, not jsonb: GetPayloadAsync re-verifies ContentHash against a re-serialization
            // of this exact payload on every read, and jsonb does not preserve key order/number
            // formatting - same class of bug as PolicyRevisionEntity.PolicyDocumentJson above.
            entity.Property(e => e.PayloadJson).HasColumnName("payload").HasColumnType("text");
            entity.Property(e => e.MediaType).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnType("text");
        });

        modelBuilder.Entity<CalibrationArtifactStatusEventEntity>(entity =>
        {
            entity.ToTable("calibration_artifact_status_events", "config");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.FromStatus).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ToStatus).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.ActorIdentity).HasColumnType("text");
            entity.HasIndex(e => new { e.ArtifactId, e.OccurredAt });
            entity.HasOne<CalibrationArtifactEntity>().WithMany().HasForeignKey(e => e.ArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CalibrationBundleCandidateEntity>(entity =>
        {
            entity.ToTable("calibration_bundle_candidates", "config");
            entity.HasKey(e => e.CandidateId);
            // text, not jsonb: this stores a TradingPolicyProfile, subject to the same
            // ConfigurationHash-vs-jsonb-reordering bug as PolicyRevisionEntity.PolicyDocumentJson.
            entity.Property(e => e.ProposedProfileJson).HasColumnName("proposed_profile").HasColumnType("text");
            entity.Property(e => e.Status).HasColumnType("smallint");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ReviewedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.Status, e.CreatedAt });
            entity.HasOne<CalibrationArtifactEntity>().WithMany().HasForeignKey(e => e.SetupArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CalibrationArtifactEntity>().WithMany().HasForeignKey(e => e.MetaModelArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CalibrationArtifactEntity>().WithMany().HasForeignKey(e => e.ManagementArtifactId)
                .OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(e => e.Environment).HasColumnType("text");
            entity.Property(e => e.ExecutionMode).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.RequestedAt).HasColumnType("timestamptz");
            entity.Property(e => e.PreparingAt).HasColumnType("timestamptz");
            entity.Property(e => e.WarmingUpAt).HasColumnType("timestamptz");
            entity.Property(e => e.RunningAt).HasColumnType("timestamptz");
            entity.Property(e => e.PausedAt).HasColumnType("timestamptz");
            entity.Property(e => e.DrainingAt).HasColumnType("timestamptz");
            entity.Property(e => e.FaultedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StoppedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StartedBy).HasColumnType("text");
            entity.Property(e => e.StopReason).HasColumnType("text");
            entity.Property(e => e.DeploymentHash).HasColumnType("text");
            entity.Property(e => e.IdempotencyKey).HasColumnType("text");
            entity.Property(e => e.ConcurrencyToken).IsConcurrencyToken();
            entity.HasIndex(e => e.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
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

        modelBuilder.Entity<PolicyPermissionEventEntity>(entity =>
        {
            entity.ToTable("policy_permission_events", "config");
            entity.HasKey(e => e.PermissionEventId);
            entity.Property(e => e.BrokerEnvironment).HasColumnType("text");
            entity.Property(e => e.Permissions).HasColumnType("integer").HasConversion<int>();
            entity.Property(e => e.ConfigurationHash).HasColumnType("text");
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.ActorIdentity).HasColumnType("text");
            entity.Property(e => e.Reason).HasColumnType("text");
            entity.HasIndex(e => new { e.PolicyRevisionId, e.BrokerEnvironment, e.BrokerAccountId, e.OccurredAt });
            entity.HasOne<PolicyRevisionEntity>().WithMany().HasForeignKey(e => e.PolicyRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ParityCertificationEntity>(entity =>
        {
            entity.ToTable("parity_certifications", "config");
            entity.HasKey(e => e.CertificationId);
            entity.Property(e => e.ConfigurationHash).HasColumnType("text");
            entity.Property(e => e.SourceCommit).HasColumnType("text");
            entity.Property(e => e.RecordingHash).HasColumnType("text");
            entity.Property(e => e.SimulatorBuildHash).HasColumnType("text");
            entity.Property(e => e.LiveBuildHash).HasColumnType("text");
            entity.Property(e => e.MismatchDetailsJson).HasColumnType("jsonb");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.CertifiedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CertifiedBy).HasColumnType("text");
            entity.HasIndex(e => new { e.PolicyRevisionId, e.CertifiedAt });
            entity.HasOne<PolicyRevisionEntity>().WithMany().HasForeignKey(e => e.PolicyRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentAgentEntity>(entity =>
        {
            entity.ToTable("deployment_agents", "config");
            entity.HasKey(e => e.DeploymentAgentId);
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.AgentMode).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.PackageHash).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StoppedAt).HasColumnType("timestamptz");
            entity.Property(e => e.FaultCode).HasColumnType("text");
            entity.Property(e => e.FaultMessage).HasColumnType("text");
            entity.HasIndex(e => new { e.DeploymentId, e.InstrumentId, e.PolicyRevisionId }).IsUnique();
            entity.HasIndex(e => new { e.BrokerAccountId, e.InstrumentId }).IsUnique()
                .HasFilter("enabled AND agent_mode IN (2, 3) AND status IN (0, 1, 2, 3, 4, 5, 6)")
                .HasDatabaseName("ix_deployment_agents_one_executable_owner");
            entity.HasOne<DeploymentEntity>().WithMany().HasForeignKey(e => e.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Reference.BrokerAccountEntity>().WithMany().HasForeignKey(e => e.BrokerAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PolicyRevisionEntity>().WithMany().HasForeignKey(e => e.PolicyRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Reference.InstrumentEntity>().WithMany().HasForeignKey(e => e.InstrumentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentCommandEntity>(entity =>
        {
            entity.ToTable("deployment_commands", "operations");
            entity.HasKey(e => e.CommandId);
            entity.Property(e => e.CommandType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RequestedAt).HasColumnType("timestamptz");
            entity.Property(e => e.RequestedBy).HasColumnType("text");
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
            entity.Property(e => e.ClaimedByHost).HasColumnType("text");
            entity.Property(e => e.ClaimedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ErrorCode).HasColumnType("text");
            entity.Property(e => e.ErrorMessage).HasColumnType("text");
            entity.Property(e => e.IdempotencyKey).HasColumnType("text");
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.Status, e.RequestedAt });
            entity.HasOne<DeploymentEntity>().WithMany().HasForeignKey(e => e.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DeploymentAgentEntity>().WithMany().HasForeignKey(e => e.DeploymentAgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeploymentEventEntity>(entity =>
        {
            entity.ToTable("deployment_events", "operations");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.EventType).HasColumnType("text");
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.ActorIdentity).HasColumnType("text");
            entity.Property(e => e.CorrelationId).HasColumnType("text");
            entity.Property(e => e.ReasonCode).HasColumnType("text");
            entity.Property(e => e.DetailJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.DeploymentId, e.OccurredAt });
            entity.HasOne<DeploymentEntity>().WithMany().HasForeignKey(e => e.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<DeploymentAgentEntity>().WithMany().HasForeignKey(e => e.DeploymentAgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RuntimeProfileEntity>(entity =>
        {
            entity.ToTable("runtime_profiles", "config");
            entity.HasKey(e => e.RuntimeProfileId);
            entity.Property(e => e.ProfileKind).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Name).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnType("text");
            entity.HasIndex(e => new { e.ProfileKind, e.Name }).IsUnique();
        });

        modelBuilder.Entity<RuntimeProfileRevisionEntity>(entity =>
        {
            entity.ToTable("runtime_profile_revisions", "config");
            entity.HasKey(e => e.RuntimeProfileRevisionId);
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.SettingsJson).HasColumnName("settings").HasColumnType("jsonb");
            entity.Property(e => e.SettingsHash).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnType("text");
            entity.Property(e => e.ApprovedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedBy).HasColumnType("text");
            entity.HasIndex(e => new { e.RuntimeProfileId, e.Revision }).IsUnique();
            entity.HasIndex(e => e.SettingsHash).IsUnique();
            entity.HasOne<RuntimeProfileEntity>().WithMany().HasForeignKey(e => e.RuntimeProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
