using DBManager.Abstractions.Risk;
using DBManager.Postgres.Decision;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Risk;

public sealed class PositionSizingEvaluationEntity
{
    public Guid SizingId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public long AccountSnapshotVersion { get; set; }
    public long InstrumentMetadataRevision { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public decimal Equity { get; set; }
    public decimal BaseRiskAmount { get; set; }
    public decimal StopDistance { get; set; }
    public decimal ExpectedSpread { get; set; }
    public decimal ExpectedSlippage { get; set; }
    public decimal ExpectedCommission { get; set; }
    public decimal ConversionRate { get; set; }
    public required string ConversionPath { get; set; }
    public decimal RawQuantity { get; set; }
    public decimal NormalizedQuantity { get; set; }
    public decimal EstimatedStopLoss { get; set; }
    public decimal EstimatedMargin { get; set; }
    public bool Approved { get; set; }
    public required string ReasonCode { get; set; }
    public required string AuditJson { get; set; }
}

public sealed class PortfolioDecisionEntity
{
    public Guid PortfolioDecisionId { get; set; }
    public DateTimeOffset DecisionEpoch { get; set; }
    public Guid BrokerAccountId { get; set; }
    public Guid CandidateId { get; set; }
    public int Rank { get; set; }
    public decimal RequestedRisk { get; set; }
    public decimal ApprovedRisk { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal ApprovedQuantity { get; set; }
    public decimal EstimatedMargin { get; set; }
    public bool Approved { get; set; }
    public required string ReasonCode { get; set; }
    public required string PortfolioSnapshotJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReservationEntity
{
    public Guid ReservationId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public Guid PortfolioDecisionId { get; set; }
    public ReservationState State { get; set; }
    public decimal ReservedRisk { get; set; }
    public decimal ReservedMargin { get; set; }
    public decimal ReservedQuantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ReservationEventEntity
{
    public long EventId { get; set; }
    public Guid ReservationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public ReservationState FromState { get; set; }
    public ReservationState ToState { get; set; }
    public required string ReasonCode { get; set; }
    public Guid? RelatedOrderId { get; set; }
    public required string DetailsJson { get; set; }
}

internal static class RiskModelConfiguration
{
    private static readonly ReservationState[] ActiveStates =
    [
        ReservationState.Created,
        ReservationState.SubmissionPending,
        ReservationState.BrokerAccepted,
        ReservationState.PartiallyFilled
    ];

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PositionSizingEvaluationEntity>(entity =>
        {
            entity.ToTable("position_sizing_evaluations", "risk");
            entity.HasKey(e => e.SizingId);
            entity.HasIndex(e => e.CandidateId).IsUnique();
            entity.Property(e => e.EvaluatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.Equity).HasColumnType("numeric(28,8)");
            entity.Property(e => e.BaseRiskAmount).HasColumnType("numeric(28,8)");
            entity.Property(e => e.StopDistance).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ExpectedSpread).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ExpectedSlippage).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ExpectedCommission).HasColumnType("numeric(28,8)");
            entity.Property(e => e.ConversionRate).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ConversionPath).HasColumnType("text");
            entity.Property(e => e.RawQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.NormalizedQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.EstimatedStopLoss).HasColumnType("numeric(28,8)");
            entity.Property(e => e.EstimatedMargin).HasColumnType("numeric(28,8)");
            entity.Property(e => e.ReasonCode).HasColumnType("text");
            entity.Property(e => e.AuditJson).HasColumnName("audit").HasColumnType("jsonb");
            entity.HasOne<CandidateKeyEntity>()
                .WithMany()
                .HasForeignKey(e => e.CandidateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PortfolioDecisionEntity>(entity =>
        {
            entity.ToTable("portfolio_decisions", "risk");
            entity.HasKey(e => e.PortfolioDecisionId);
            entity.HasIndex(e => e.CandidateId).IsUnique();
            entity.Property(e => e.DecisionEpoch).HasColumnType("timestamptz");
            entity.Property(e => e.RequestedRisk).HasColumnType("numeric(28,8)");
            entity.Property(e => e.ApprovedRisk).HasColumnType("numeric(28,8)");
            entity.Property(e => e.RequestedQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.ApprovedQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.EstimatedMargin).HasColumnType("numeric(28,8)");
            entity.Property(e => e.ReasonCode).HasColumnType("text");
            entity.Property(e => e.PortfolioSnapshotJson).HasColumnName("portfolio_snapshot").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.HasOne<CandidateKeyEntity>()
                .WithMany()
                .HasForeignKey(e => e.CandidateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReservationEntity>(entity =>
        {
            entity.ToTable("reservations", "risk");
            entity.HasKey(e => e.ReservationId);
            entity.HasIndex(e => e.CandidateId).IsUnique();
            entity.HasIndex(e => e.PortfolioDecisionId).IsUnique();
            entity.Property(e => e.State).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ReservedRisk).HasColumnType("numeric(28,8)");
            entity.Property(e => e.ReservedMargin).HasColumnType("numeric(28,8)");
            entity.Property(e => e.ReservedQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ExpiresAt).HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.BrokerAccountId, e.State, e.ExpiresAt });
            entity.HasIndex(e => e.BrokerAccountId)
                .HasFilter($"state IN ({string.Join(",", ActiveStates.Select(s => (short)s))})")
                .HasDatabaseName("ix_reservations_active_by_account");
            entity.HasOne<CandidateKeyEntity>()
                .WithMany()
                .HasForeignKey(e => e.CandidateId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PortfolioDecisionEntity>()
                .WithMany()
                .HasForeignKey(e => e.PortfolioDecisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReservationEventEntity>(entity =>
        {
            entity.ToTable("reservation_events", "risk");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.FromState).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ToState).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.ReasonCode).HasColumnType("text");
            entity.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.ReservationId, e.OccurredAt });
        });
    }
}
