using DBManager.Abstractions.Decision;
using DBManager.Abstractions.Management;
using DBManager.Postgres.Config;
using DBManager.Postgres.Execution;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Management;

public sealed class PositionManagementStateEntity
{
    public Guid PositionId { get; set; }
    public Guid ManagementPolicyRevisionId { get; set; }
    public ManagementStage Stage { get; set; }
    public decimal MfePrice { get; set; }
    public decimal MaePrice { get; set; }
    public decimal MfeR { get; set; }
    public decimal MaeR { get; set; }
    public decimal? HighestProfitFloor { get; set; }
    public DateTimeOffset? LastFastIntervalAt { get; set; }
    public DateTimeOffset? LastMainIntervalAt { get; set; }
    public DateTimeOffset? LastThesisIntervalAt { get; set; }
    public DateTimeOffset? LastActionAt { get; set; }
    public required string StatePayloadJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ManagementEventEntity
{
    public long EventId { get; set; }
    public Guid PositionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public TriggerInterval EvaluationClock { get; set; }
    public ManagementActionType ActionType { get; set; }
    public required string ReasonCode { get; set; }
    public decimal? ReferenceBid { get; set; }
    public decimal? ReferenceAsk { get; set; }
    public decimal CurrentR { get; set; }
    public decimal MfeR { get; set; }
    public decimal MaeR { get; set; }
    public decimal? PreviousStop { get; set; }
    public decimal? RequestedStop { get; set; }
    public decimal? ConfirmedStop { get; set; }
    public decimal? RequestedReduction { get; set; }
    public decimal? ConfirmedReduction { get; set; }
    public Guid? BrokerCommandId { get; set; }
    public required string DetailsJson { get; set; }
}

internal static class ManagementModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PositionManagementStateEntity>(entity =>
        {
            entity.ToTable("position_management_state", "management");
            entity.HasKey(e => e.PositionId);
            entity.Property(e => e.Stage).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.MfePrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.MaePrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.MfeR).HasColumnType("numeric(18,10)");
            entity.Property(e => e.MaeR).HasColumnType("numeric(18,10)");
            entity.Property(e => e.HighestProfitFloor).HasColumnType("numeric(28,12)");
            entity.Property(e => e.LastFastIntervalAt).HasColumnType("timestamptz");
            entity.Property(e => e.LastMainIntervalAt).HasColumnType("timestamptz");
            entity.Property(e => e.LastThesisIntervalAt).HasColumnType("timestamptz");
            entity.Property(e => e.LastActionAt).HasColumnType("timestamptz");
            entity.Property(e => e.StatePayloadJson).HasColumnName("state_payload").HasColumnType("jsonb");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamptz");
            entity.HasOne<PositionEntity>().WithMany().HasForeignKey(e => e.PositionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PolicyRevisionEntity>().WithMany().HasForeignKey(e => e.ManagementPolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManagementEventEntity>(entity =>
        {
            entity.ToTable("management_events", "management");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.EvaluationClock).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ActionType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ReasonCode).HasColumnType("text");
            entity.Property(e => e.ReferenceBid).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ReferenceAsk).HasColumnType("numeric(28,12)");
            entity.Property(e => e.CurrentR).HasColumnType("numeric(18,10)");
            entity.Property(e => e.MfeR).HasColumnType("numeric(18,10)");
            entity.Property(e => e.MaeR).HasColumnType("numeric(18,10)");
            entity.Property(e => e.PreviousStop).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RequestedStop).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ConfirmedStop).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RequestedReduction).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ConfirmedReduction).HasColumnType("numeric(28,12)");
            entity.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.PositionId, e.OccurredAt });
            // The idempotency guard for "close/reduce/stop actions are idempotent" (section 30
            // Phase 5 acceptance): replaying the same broker command must not double-apply.
            entity.HasIndex(e => e.BrokerCommandId).IsUnique().HasFilter("broker_command_id IS NOT NULL");
        });
    }
}
