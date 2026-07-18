using DBManager.Abstractions.Decision;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Decision;

/// <summary>Global idempotency for decisions, independent of any future partition layout (section 7.4).</summary>
public sealed class DecisionKeyEntity
{
    public Guid DecisionId { get; set; }
    public DateTimeOffset DecisionTime { get; set; }
    public Guid DeploymentId { get; set; }
}

public sealed class AgentEvaluationEntity
{
    public long EvaluationSequence { get; set; }
    public Guid DecisionId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public DateTimeOffset DecisionTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public AgentEvaluationAction Action { get; set; }
    public AgentState StateBefore { get; set; }
    public AgentState StateAfter { get; set; }
    public TriggerInterval TriggerInterval { get; set; }
    public long SnapshotVersion { get; set; }
    public decimal? RawConfidence { get; set; }
    public decimal? MtfAlignment { get; set; }
    public Regime? Regime { get; set; }
    public required string PrimaryReasonCode { get; set; }
    public required string DiagnosticsJson { get; set; }
    public DateTimeOffset PersistedAt { get; set; }
}

public sealed class CandidateKeyEntity
{
    public Guid CandidateId { get; set; }
    public Guid DecisionId { get; set; }
    public DateTimeOffset CandidateTime { get; set; }
    public CandidateStatus CurrentStatus { get; set; }
}

public sealed class TradeCandidateEntity
{
    public long CandidateSequence { get; set; }
    public Guid CandidateId { get; set; }
    public Guid DecisionId { get; set; }
    public required string SetupId { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public TradeDirection Direction { get; set; }
    public DateTimeOffset DecisionTime { get; set; }
    public decimal? ReferenceBid { get; set; }
    public decimal? ReferenceAsk { get; set; }
    public decimal ReferenceMid { get; set; }
    public decimal StopPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal RawConfidence { get; set; }
    public decimal MtfAlignment { get; set; }
    public Regime Regime { get; set; }
    public required string SetupCalibrationAuditJson { get; set; }
    public required string MetaLabelAuditJson { get; set; }
    public required string TradingConditionAuditJson { get; set; }
    public CandidateStatus Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only funnel history (section 7.4) — prevents one final rejection reason from hiding earlier results.</summary>
public sealed class CandidateStageEventEntity
{
    public long EventId { get; set; }
    public Guid CandidateId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public CandidateStage Stage { get; set; }
    public StageOutcome Outcome { get; set; }
    public required string ReasonCode { get; set; }
    public decimal? RiskMultiplier { get; set; }
    public decimal? ConfidenceBefore { get; set; }
    public decimal? ConfidenceAfter { get; set; }
    public required string DetailsJson { get; set; }
}

internal static class DecisionModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DecisionKeyEntity>(entity =>
        {
            entity.ToTable("decision_keys", "decision");
            entity.HasKey(e => e.DecisionId);
            entity.Property(e => e.DecisionTime).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AgentEvaluationEntity>(entity =>
        {
            entity.ToTable("agent_evaluations", "decision");
            entity.HasKey(e => e.EvaluationSequence);
            entity.Property(e => e.EvaluationSequence).UseIdentityAlwaysColumn();
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.DecisionTime).HasColumnType("timestamptz");
            entity.Property(e => e.ReceivedAt).HasColumnType("timestamptz");
            entity.Property(e => e.Action).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StateBefore).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.StateAfter).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.TriggerInterval).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RawConfidence).HasColumnType("numeric(18,10)");
            entity.Property(e => e.MtfAlignment).HasColumnType("numeric(18,10)");
            entity.Property(e => e.Regime).HasColumnType("smallint").HasConversion<short?>();
            entity.Property(e => e.PrimaryReasonCode).HasColumnType("text");
            entity.Property(e => e.DiagnosticsJson).HasColumnName("diagnostics").HasColumnType("jsonb");
            entity.Property(e => e.PersistedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => e.DecisionId).IsUnique();
            entity.HasIndex(e => new { e.DeploymentId, e.StrategyId, e.InstrumentId, e.DecisionTime });
            entity.HasIndex(e => new { e.PrimaryReasonCode, e.DecisionTime });
            entity.HasIndex(e => e.DecisionTime).HasMethod("brin");
        });

        modelBuilder.Entity<CandidateKeyEntity>(entity =>
        {
            entity.ToTable("candidate_keys", "decision");
            entity.HasKey(e => e.CandidateId);
            entity.Property(e => e.CandidateTime).HasColumnType("timestamptz");
            entity.Property(e => e.CurrentStatus).HasColumnType("smallint").HasConversion<short>();
            entity.HasIndex(e => e.DecisionId).IsUnique();
        });

        modelBuilder.Entity<TradeCandidateEntity>(entity =>
        {
            entity.ToTable("trade_candidates", "decision");
            entity.HasKey(e => e.CandidateSequence);
            entity.Property(e => e.CandidateSequence).UseIdentityAlwaysColumn();
            entity.Property(e => e.SetupId).HasColumnType("text");
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.Direction).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.DecisionTime).HasColumnType("timestamptz");
            entity.Property(e => e.ReferenceBid).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ReferenceAsk).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ReferenceMid).HasColumnType("numeric(28,12)");
            entity.Property(e => e.StopPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.TargetPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RawConfidence).HasColumnType("numeric(18,10)");
            entity.Property(e => e.MtfAlignment).HasColumnType("numeric(18,10)");
            entity.Property(e => e.Regime).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.SetupCalibrationAuditJson).HasColumnName("setup_calibration_audit").HasColumnType("jsonb");
            entity.Property(e => e.MetaLabelAuditJson).HasColumnName("meta_label_audit").HasColumnType("jsonb");
            entity.Property(e => e.TradingConditionAuditJson).HasColumnName("trading_condition_audit").HasColumnType("jsonb");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => e.CandidateId).IsUnique();
        });

        modelBuilder.Entity<CandidateStageEventEntity>(entity =>
        {
            entity.ToTable("candidate_stage_events", "decision");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.Stage).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Outcome).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ReasonCode).HasColumnType("text");
            entity.Property(e => e.RiskMultiplier).HasColumnType("numeric(18,10)");
            entity.Property(e => e.ConfidenceBefore).HasColumnType("numeric(18,10)");
            entity.Property(e => e.ConfidenceAfter).HasColumnType("numeric(18,10)");
            entity.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.CandidateId, e.OccurredAt });
        });
    }
}
