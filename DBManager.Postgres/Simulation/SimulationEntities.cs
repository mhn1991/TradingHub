using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Simulation;

public sealed class SimulationJobEntity
{
    public Guid SimulationId { get; set; }
    public Guid? ParentExperimentId { get; set; }
    public short JobKind { get; set; }
    public short Status { get; set; }
    public short Phase { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string ResolvedConfigurationJson { get; set; }
    public string? InputRequestId { get; set; }
    public string? InputHash { get; set; }
    public Guid? OutputManifestId { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureDetail { get; set; }
    public bool PauseRequested { get; set; }
    public bool CancelRequested { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public required string SnapshotJson { get; set; }
}

public sealed class SimulationJobProgressEntity
{
    public Guid SimulationId { get; set; }
    public DateTimeOffset? MarketTime { get; set; }
    public long ProcessedCandles { get; set; }
    public decimal ProgressPercent { get; set; }
    public decimal CandlesPerSecond { get; set; }
    public required string CurrentPhase { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SimulationJobEventEntity
{
    public long EventId { get; set; }
    public Guid SimulationId { get; set; }
    public long JobRevision { get; set; }
    public short Status { get; set; }
    public required string EventType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string DetailsJson { get; set; }
}

public sealed class SimulationOutputManifestEntity
{
    public Guid OutputManifestId { get; set; }
    public Guid SimulationId { get; set; }
    public required string ManifestHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SimulationOutputBlobEntity
{
    public Guid BlobId { get; set; }
    public Guid OutputManifestId { get; set; }
    public Guid SimulationId { get; set; }
    public short BlobKind { get; set; }
    public required string StorageUri { get; set; }
    public required string ContentHash { get; set; }
    public long SizeBytes { get; set; }
    public required string MediaType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetentionUntil { get; set; }
}

public sealed class SimulationExperimentEntity
{
    public Guid ExperimentId { get; set; }
    public int Revision { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string ResolvedConfigurationJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

public sealed class SimulationProfileEntity
{
    public Guid ProfileId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public sealed class SimulationProfileRevisionEntity
{
    public Guid ProfileRevisionId { get; set; }
    public Guid ProfileId { get; set; }
    public int Revision { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string SettingsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SimulationExperimentRunEntity
{
    public Guid ExperimentRunId { get; set; }
    public Guid ExperimentId { get; set; }
    public Guid SimulationId { get; set; }
    public Guid ProfileRevisionId { get; set; }
    public string? VariantId { get; set; }
    public Guid? SharedAnalysisGroupId { get; set; }
    public DateTimeOffset? AnalysisWarmupFrom { get; set; }
    public DateTimeOffset? LearningFrom { get; set; }
    public DateTimeOffset? LearningTo { get; set; }
    public DateTimeOffset? EmbargoFrom { get; set; }
    public DateTimeOffset? EmbargoTo { get; set; }
    public DateTimeOffset? EvaluationWarmupFrom { get; set; }
    public DateTimeOffset? EvaluationFrom { get; set; }
    public DateTimeOffset? EvaluationTo { get; set; }
}

public sealed class SimulationExperimentComparisonEntity
{
    public Guid ComparisonId { get; set; }
    public Guid ExperimentId { get; set; }
    public Guid BaselineSimulationId { get; set; }
    public Guid CandidateSimulationId { get; set; }
    public required string MetricsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal static class SimulationModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SimulationJobEntity>(entity =>
        {
            entity.ToTable("jobs", "simulation");
            entity.HasKey(row => row.SimulationId);
            entity.Property(row => row.ResolvedConfigurationJson).HasColumnType("jsonb");
            entity.Property(row => row.SnapshotJson).HasColumnType("jsonb");
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.Status, row.RequestedAt });
            entity.HasIndex(row => row.ParentExperimentId);
        });
        modelBuilder.Entity<SimulationJobProgressEntity>(entity =>
        {
            entity.ToTable("job_progress", "simulation");
            entity.HasKey(row => row.SimulationId);
            entity.Property(row => row.ProgressPercent).HasPrecision(7, 4);
            entity.Property(row => row.CandlesPerSecond).HasPrecision(20, 4);
        });
        modelBuilder.Entity<SimulationJobEventEntity>(entity =>
        {
            entity.ToTable("job_events", "simulation");
            entity.HasKey(row => row.EventId);
            entity.Property(row => row.EventId).UseIdentityByDefaultColumn();
            entity.Property(row => row.DetailsJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.SimulationId, row.JobRevision, row.EventType }).IsUnique();
            entity.HasIndex(row => new { row.SimulationId, row.OccurredAt });
        });
        modelBuilder.Entity<SimulationOutputManifestEntity>(entity =>
        {
            entity.ToTable("output_manifests", "simulation");
            entity.HasKey(row => row.OutputManifestId);
            entity.HasIndex(row => new { row.SimulationId, row.ManifestHash }).IsUnique();
        });
        modelBuilder.Entity<SimulationOutputBlobEntity>(entity =>
        {
            entity.ToTable("output_blobs", "simulation");
            entity.HasKey(row => row.BlobId);
            entity.HasIndex(row => new { row.SimulationId, row.BlobKind });
            entity.HasIndex(row => row.ContentHash);
        });
        modelBuilder.Entity<SimulationExperimentEntity>(entity =>
        {
            entity.ToTable("experiments", "simulation");
            entity.HasKey(row => row.ExperimentId);
            entity.Property(row => row.ResolvedConfigurationJson).HasColumnType("jsonb");
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.Status, row.CreatedAt });
        });
        modelBuilder.Entity<SimulationProfileEntity>(entity =>
        {
            entity.ToTable("experiment_profiles", "simulation");
            entity.HasKey(row => row.ProfileId);
            entity.HasIndex(row => row.Name).IsUnique().HasFilter("archived_at IS NULL");
        });
        modelBuilder.Entity<SimulationProfileRevisionEntity>(entity =>
        {
            entity.ToTable("profile_revisions", "simulation");
            entity.HasKey(row => row.ProfileRevisionId);
            entity.Property(row => row.SettingsJson).HasColumnType("text");
            entity.HasIndex(row => new { row.ProfileId, row.Revision }).IsUnique();
        });
        modelBuilder.Entity<SimulationExperimentRunEntity>(entity =>
        {
            entity.ToTable("experiment_runs", "simulation");
            entity.HasKey(row => row.ExperimentRunId);
            entity.HasIndex(row => new { row.ExperimentId, row.SimulationId }).IsUnique();
        });
        modelBuilder.Entity<SimulationExperimentComparisonEntity>(entity =>
        {
            entity.ToTable("experiment_comparisons", "simulation");
            entity.HasKey(row => row.ComparisonId);
            entity.Property(row => row.MetricsJson).HasColumnType("jsonb");
            entity.HasIndex(row => row.ExperimentId);
        });
    }
}
