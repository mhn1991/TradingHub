using DBManager.Abstractions.Config;
using DBManager.Abstractions.Research;
using DBManager.Postgres.Config;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Research;

public sealed class ResearchRunEntity
{
    public Guid ResearchRunId { get; set; }
    public ResearchRunType RunType { get; set; }
    public required string StrategyId { get; set; }
    public required string StrategyVersion { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string DatasetHash { get; set; }
    public ResearchRunStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? RandomSeed { get; set; }
    public required string ParametersJson { get; set; }
    public string? SummaryMetricsJson { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class TimeSeriesFoldEntity
{
    public Guid FoldId { get; set; }
    public Guid ResearchRunId { get; set; }
    public int FoldNumber { get; set; }
    public DateTimeOffset TrainingFrom { get; set; }
    public DateTimeOffset TrainingTo { get; set; }
    public DateTimeOffset ValidationFrom { get; set; }
    public DateTimeOffset ValidationTo { get; set; }
    public DateTimeOffset? TestFrom { get; set; }
    public DateTimeOffset? TestTo { get; set; }
    public TimeSpan PurgeDuration { get; set; }
    public TimeSpan EmbargoDuration { get; set; }
    public required string SampleCountsJson { get; set; }
    public required string MetricsJson { get; set; }
}

public sealed class CalibrationRunEntity
{
    public Guid CalibrationRunId { get; set; }
    public Guid ResearchRunId { get; set; }
    public ArtifactRole Stage { get; set; }
    public required string InputArtifactIdsJson { get; set; }
    public Guid? OutputArtifactId { get; set; }
    public ResearchRunStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string MetricsJson { get; set; }
}

internal static class ResearchModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ResearchRunEntity>(entity =>
        {
            entity.ToTable("research_runs", "research");
            entity.HasKey(e => e.ResearchRunId);
            entity.Property(e => e.RunType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.StrategyVersion).HasColumnType("text");
            entity.Property(e => e.ConfigurationHash).HasColumnType("text");
            entity.Property(e => e.DatasetHash).HasColumnType("text");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RequestedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ParametersJson).HasColumnName("parameters").HasColumnType("jsonb");
            entity.Property(e => e.SummaryMetricsJson).HasColumnName("summary_metrics").HasColumnType("jsonb");
            entity.Property(e => e.FailureReason).HasColumnType("text");
        });

        modelBuilder.Entity<TimeSeriesFoldEntity>(entity =>
        {
            entity.ToTable("time_series_folds", "research");
            entity.HasKey(e => e.FoldId);
            entity.Property(e => e.TrainingFrom).HasColumnType("timestamptz");
            entity.Property(e => e.TrainingTo).HasColumnType("timestamptz");
            entity.Property(e => e.ValidationFrom).HasColumnType("timestamptz");
            entity.Property(e => e.ValidationTo).HasColumnType("timestamptz");
            entity.Property(e => e.TestFrom).HasColumnType("timestamptz");
            entity.Property(e => e.TestTo).HasColumnType("timestamptz");
            entity.Property(e => e.PurgeDuration).HasColumnType("interval");
            entity.Property(e => e.EmbargoDuration).HasColumnType("interval");
            entity.Property(e => e.SampleCountsJson).HasColumnName("sample_counts").HasColumnType("jsonb");
            entity.Property(e => e.MetricsJson).HasColumnName("metrics").HasColumnType("jsonb");
            entity.HasOne<ResearchRunEntity>().WithMany().HasForeignKey(e => e.ResearchRunId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.ResearchRunId, e.FoldNumber }).IsUnique();
        });

        modelBuilder.Entity<CalibrationRunEntity>(entity =>
        {
            entity.ToTable("calibration_runs", "research");
            entity.HasKey(e => e.CalibrationRunId);
            entity.Property(e => e.Stage).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.InputArtifactIdsJson).HasColumnName("input_artifact_ids").HasColumnType("jsonb");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.Property(e => e.MetricsJson).HasColumnName("metrics").HasColumnType("jsonb");
            entity.HasOne<ResearchRunEntity>().WithMany().HasForeignKey(e => e.ResearchRunId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CalibrationArtifactEntity>().WithMany().HasForeignKey(e => e.OutputArtifactId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
