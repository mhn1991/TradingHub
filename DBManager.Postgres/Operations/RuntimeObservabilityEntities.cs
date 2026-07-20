using Microsoft.EntityFrameworkCore;
using TradingObservability.Abstractions;

namespace DBManager.Postgres.Operations;

public sealed class RuntimeSessionEntity
{
    public Guid RuntimeSessionId { get; set; }
    public RuntimeSessionKind SessionKind { get; set; }
    public Guid? SimulationId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid? ResearchRunId { get; set; }
    public required string HostInstanceId { get; set; }
    public required string MachineName { get; set; }
    public int ProcessId { get; set; }
    public required string ConfigurationHash { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public RuntimeSessionStatus Status { get; set; }
    public string? TerminationReason { get; set; }
}

public sealed class TradingTelemetryEventEntity
{
    public Guid EventId { get; set; }
    public Guid RuntimeSessionId { get; set; }
    public TradingTelemetryType Type { get; set; }
    public TradingTelemetrySeverity Severity { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public required string ReasonCode { get; set; }
    public Guid? ExperimentId { get; set; }
    public Guid? SimulationId { get; set; }
    public Guid? ResearchRunId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Guid? AgentInstanceId { get; set; }
    public Guid? PolicyRevisionId { get; set; }
    public long? InstrumentId { get; set; }
    public Guid? DecisionId { get; set; }
    public string? CandidateId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid? OrderCommandId { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? FillId { get; set; }
    public Guid? PositionId { get; set; }
    public required string PayloadJson { get; set; }
}

public sealed class AgentActivityWindowEntity
{
    public Guid RuntimeSessionId { get; set; }
    public Guid AgentInstanceId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public string PlaybookKey { get; set; } = string.Empty;
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public long EvaluationsObserved { get; set; }
    public long NoSetupCount { get; set; }
    public long WarmupCount { get; set; }
    public long BuyCount { get; set; }
    public long SellCount { get; set; }
    public long HoldCount { get; set; }
    public long CandidatesCreated { get; set; }
    public long CandidatesRejected { get; set; }
    public long StateTransitions { get; set; }
    public long TimeoutCount { get; set; }
    public long ErrorCount { get; set; }
    public required string ReasonCountsJson { get; set; }
    public double MeanEvaluationMilliseconds { get; set; }
    public double MinEvaluationMilliseconds { get; set; }
    public double MaxEvaluationMilliseconds { get; set; }
    public double? MeanCandidateConfidence { get; set; }
    public long? LatestSnapshotVersion { get; set; }
    public DateTimeOffset? LatestMarketTime { get; set; }
}

public sealed class ManualApprovalCandidateEntity
{
    public required string CandidateId { get; set; }
    public required string CandidateFingerprint { get; set; }
    public required string ReservationId { get; set; }
    public Guid? DeploymentId { get; set; }
    public string? PolicyRevision { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string CandidateJson { get; set; }
    public short State { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewReason { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class ManualApprovalEventEntity
{
    public long EventId { get; set; }
    public required string CandidateId { get; set; }
    public short FromState { get; set; }
    public short ToState { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Actor { get; set; }
    public string? Reason { get; set; }
    public long CandidateRevision { get; set; }
}

public sealed class LiveEventJournalEntity
{
    public long EventSequence { get; set; }
    public Guid EventId { get; set; }
    public Guid DeploymentId { get; set; }
    public required string StreamName { get; set; }
    public required string PayloadType { get; set; }
    public required string PayloadJson { get; set; }
    public required string PayloadHash { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal static class RuntimeObservabilityModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RuntimeSessionEntity>(entity =>
        {
            entity.ToTable("runtime_sessions", "operations");
            entity.HasKey(row => row.RuntimeSessionId);
            entity.Property(row => row.SessionKind).HasColumnType("smallint").HasConversion<short>();
            entity.Property(row => row.Status).HasColumnType("smallint").HasConversion<short>();
            entity.HasIndex(row => new { row.SessionKind, row.StartedAt });
            entity.HasIndex(row => row.SimulationId);
            entity.HasIndex(row => row.DeploymentId);
        });
        modelBuilder.Entity<TradingTelemetryEventEntity>(entity =>
        {
            entity.ToTable("trading_telemetry_events", "operations");
            entity.HasKey(row => row.EventId);
            entity.Property(row => row.Type).HasColumnType("smallint").HasConversion<short>();
            entity.Property(row => row.Severity).HasColumnType("smallint").HasConversion<short>();
            entity.Property(row => row.PayloadJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.RuntimeSessionId, row.OccurredAt });
            entity.HasIndex(row => new { row.ReasonCode, row.OccurredAt });
            entity.HasIndex(row => row.CandidateId);
            entity.HasIndex(row => row.PositionId);
        });
        modelBuilder.Entity<AgentActivityWindowEntity>(entity =>
        {
            entity.ToTable("agent_activity_windows", "operations");
            entity.HasKey(row => new
            {
                row.RuntimeSessionId, row.AgentInstanceId, row.InstrumentId,
                row.StrategyId, row.PlaybookKey, row.WindowStart
            });
            entity.Property(row => row.ReasonCountsJson).HasColumnType("jsonb");
            entity.HasIndex(row => new { row.RuntimeSessionId, row.WindowStart });
        });
        modelBuilder.Entity<ManualApprovalCandidateEntity>(entity =>
        {
            entity.ToTable("manual_approval_candidates", "operations");
            entity.HasKey(row => row.CandidateId);
            entity.Property(row => row.CandidateJson).HasColumnType("jsonb");
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.State, row.ExpiresAt });
            entity.HasIndex(row => row.CandidateFingerprint);
        });
        modelBuilder.Entity<ManualApprovalEventEntity>(entity =>
        {
            entity.ToTable("manual_approval_events", "operations");
            entity.HasKey(row => row.EventId);
            entity.Property(row => row.EventId).UseIdentityByDefaultColumn();
            entity.HasIndex(row => new { row.CandidateId, row.CandidateRevision }).IsUnique();
        });
        modelBuilder.Entity<LiveEventJournalEntity>(entity =>
        {
            entity.ToTable("live_event_journal", "operations");
            entity.HasKey(row => row.EventSequence);
            entity.Property(row => row.EventSequence).UseIdentityByDefaultColumn();
            entity.HasIndex(row => row.EventId).IsUnique();
            entity.HasIndex(row => new { row.DeploymentId, row.StreamName, row.OccurredAt });
            entity.Property(row => row.PayloadJson).HasColumnType("jsonb");
        });
    }
}
