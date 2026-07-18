using DBManager.Abstractions.Analytics;
using DBManager.Abstractions.Decision;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Analytics;

/// <summary>
/// Maps to the partitioned <c>analytics.candidate_outcomes</c> table for LINQ reads. The table
/// itself is created by raw SQL in the migration (<see cref="AnalyticsModelConfiguration"/>'s
/// <c>ExcludeFromMigrations</c>) because EF Core has no first-class support for PostgreSQL
/// declarative partitioning (section 20).
/// </summary>
public sealed class CandidateOutcomeEntity
{
    public Guid OutcomeId { get; set; }
    public Guid CandidateId { get; set; }
    public DateTimeOffset DecisionTime { get; set; }
    public DateTimeOffset HorizonEnd { get; set; }
    public bool TargetReached { get; set; }
    public bool StopReached { get; set; }
    public TerminalEvent FirstTerminalEvent { get; set; }
    public decimal MfePrice { get; set; }
    public decimal MaePrice { get; set; }
    public decimal MfeR { get; set; }
    public decimal MaeR { get; set; }
    public decimal MaximumAchievableR { get; set; }
    public TimeSpan TimeToMfe { get; set; }
    public TimeSpan TimeToMae { get; set; }
    public decimal SpreadAdjustedR { get; set; }
    public int OutcomeVersion { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
}

public sealed class ExecutionQualityEntity
{
    public Guid ExecutionQualityId { get; set; }
    public Guid OrderId { get; set; }
    public decimal DecisionBid { get; set; }
    public decimal DecisionAsk { get; set; }
    public decimal SubmissionBid { get; set; }
    public decimal SubmissionAsk { get; set; }
    public decimal ExpectedFillPrice { get; set; }
    public decimal ActualFillPrice { get; set; }
    public decimal ExpectedSpread { get; set; }
    public decimal ActualSpread { get; set; }
    public decimal ExpectedSlippage { get; set; }
    public decimal ActualSlippage { get; set; }
    public int SubmissionLatencyMs { get; set; }
    public int? AckLatencyMs { get; set; }
    public int? FillLatencyMs { get; set; }
    public short Session { get; set; }
    public Regime Regime { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
}

public sealed class ModelMonitoringWindowEntity
{
    public Guid WindowId { get; set; }
    public Guid ArtifactId { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public long SampleCount { get; set; }
    public required string DriftMetricsJson { get; set; }
    public required string CalibrationMetricsJson { get; set; }
}

internal static class AnalyticsModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandidateOutcomeEntity>(entity =>
        {
            entity.ToTable("candidate_outcomes", "analytics", tb => tb.ExcludeFromMigrations());
            entity.HasKey(e => new { e.DecisionTime, e.OutcomeId });
            entity.Property(e => e.FirstTerminalEvent).HasConversion<short>();
        });

        modelBuilder.Entity<ExecutionQualityEntity>(entity =>
        {
            entity.ToTable("execution_quality", "analytics");
            entity.HasKey(e => e.ExecutionQualityId);
            entity.HasIndex(e => e.OrderId).IsUnique();
            entity.Property(e => e.DecisionBid).HasColumnType("numeric(28,12)");
            entity.Property(e => e.DecisionAsk).HasColumnType("numeric(28,12)");
            entity.Property(e => e.SubmissionBid).HasColumnType("numeric(28,12)");
            entity.Property(e => e.SubmissionAsk).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ExpectedFillPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ActualFillPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ExpectedSpread).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ActualSpread).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ExpectedSlippage).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ActualSlippage).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Session).HasColumnType("smallint");
            entity.Property(e => e.Regime).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.CalculatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ModelMonitoringWindowEntity>(entity =>
        {
            entity.ToTable("model_monitoring_windows", "analytics");
            entity.HasKey(e => e.WindowId);
            entity.Property(e => e.WindowStart).HasColumnType("timestamptz");
            entity.Property(e => e.WindowEnd).HasColumnType("timestamptz");
            entity.Property(e => e.DriftMetricsJson).HasColumnName("drift_metrics").HasColumnType("jsonb");
            entity.Property(e => e.CalibrationMetricsJson).HasColumnName("calibration_metrics").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.ArtifactId, e.WindowStart });
        });
    }
}
