using DBManager.Abstractions.Decision;
using DBManager.Abstractions.Execution;
using DBManager.Postgres.Config;
using DBManager.Postgres.Decision;
using DBManager.Postgres.Reference;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Execution;

public sealed class OrderCommandEntity
{
    public Guid CommandId { get; set; }
    public required string IdempotencyKey { get; set; }
    public Guid CandidateId { get; set; }
    public Guid ReservationId { get; set; }
    public OrderCommandType CommandType { get; set; }
    public required string ClientOrderId { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal? RequestedStop { get; set; }
    public decimal? RequestedTarget { get; set; }
    public OrderCommandStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public long Version { get; set; }
}

public sealed class OrderEntity
{
    public Guid OrderId { get; set; }
    public Guid CommandId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public required string ClientOrderId { get; set; }
    public string? BrokerOrderId { get; set; }
    public string? BrokerTradeId { get; set; }
    public TradeDirection Direction { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public decimal InitialStopPrice { get; set; }
    public decimal? InitialTargetPrice { get; set; }
    public OrderState State { get; set; }
    public SubmissionCertainty SubmissionCertainty { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? FilledAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long Version { get; set; }
}

public sealed class OrderEventEntity
{
    public long EventId { get; set; }
    public Guid OrderId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public OrderEventType EventType { get; set; }
    public OrderState? FromState { get; set; }
    public OrderState? ToState { get; set; }
    public string? BrokerTransactionId { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public string? ReasonCode { get; set; }
    public required string DetailsJson { get; set; }
}

public sealed class BrokerTransactionEntity
{
    public long EventId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public required string BrokerTransactionId { get; set; }
    public DateTimeOffset BrokerTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public BrokerTransactionType TransactionType { get; set; }
    public string? ClientOrderId { get; set; }
    public string? BrokerOrderId { get; set; }
    public string? BrokerTradeId { get; set; }
    public long? InstrumentId { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }
    public string? ReasonCode { get; set; }
    public required string RawPayloadJson { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

/// <summary>Global idempotency for broker transactions, independent of any future partition layout (section 7.6).</summary>
public sealed class BrokerTransactionKeyEntity
{
    public Guid BrokerAccountId { get; set; }
    public required string BrokerTransactionId { get; set; }
    public DateTimeOffset BrokerTime { get; set; }
}

public sealed class FillEntity
{
    public Guid FillId { get; set; }
    public Guid OrderId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public required string BrokerTransactionId { get; set; }
    public string? BrokerFillId { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Commission { get; set; }
    public decimal Financing { get; set; }
    public decimal? SpreadCost { get; set; }
    public decimal? SlippageCost { get; set; }
    public DateTimeOffset BrokerTime { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class PositionEntity
{
    public Guid PositionId { get; set; }
    public Guid BrokerAccountId { get; set; }
    public long InstrumentId { get; set; }
    public required string StrategyId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public Guid? ManagementArtifactId { get; set; }
    public required string BrokerTradeId { get; set; }
    public TradeDirection Direction { get; set; }
    public PositionState State { get; set; }
    public decimal OriginalQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal InitialStopPrice { get; set; }
    public decimal CurrentStopPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal OriginalRisk { get; set; }
    public decimal RemainingRisk { get; set; }
    public decimal RealisedPnl { get; set; }
    public decimal UnrealisedPnl { get; set; }
    public decimal Commission { get; set; }
    public decimal Financing { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long Version { get; set; }
}

public sealed class PositionEventEntity
{
    public long EventId { get; set; }
    public Guid PositionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public PositionEventType EventType { get; set; }
    public decimal? QuantityDelta { get; set; }
    public decimal? Price { get; set; }
    public decimal? RealisedPnlDelta { get; set; }
    public string? ReasonCode { get; set; }
    public string? BrokerTransactionId { get; set; }
    public required string DetailsJson { get; set; }
}

internal static class ExecutionModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderCommandEntity>(entity =>
        {
            entity.ToTable("order_commands", "execution");
            entity.HasKey(e => e.CommandId);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => e.ClientOrderId).IsUnique();
            entity.Property(e => e.CommandType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RequestedQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RequestedStop).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RequestedTarget).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Status).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.HasOne<CandidateKeyEntity>().WithMany().HasForeignKey(e => e.CandidateId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderEntity>(entity =>
        {
            entity.ToTable("orders", "execution");
            entity.HasKey(e => e.OrderId);
            entity.HasIndex(e => e.CommandId).IsUnique();
            entity.HasIndex(e => e.ClientOrderId).IsUnique();
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.Direction).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RequestedQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.FilledQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.AverageFillPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.InitialStopPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.InitialTargetPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.State).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.SubmissionCertainty).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.SubmittedAt).HasColumnType("timestamptz");
            entity.Property(e => e.AcceptedAt).HasColumnType("timestamptz");
            entity.Property(e => e.FilledAt).HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.BrokerAccountId, e.State });
            entity.HasIndex(e => new { e.InstrumentId, e.State });
            entity.HasIndex(e => new { e.StrategyId, e.State });
            entity.HasIndex(e => e.State)
                .HasFilter("state IN (0,1,2,4,5)")
                .HasDatabaseName("ix_orders_active");
            entity.HasOne<OrderCommandEntity>().WithMany().HasForeignKey(e => e.CommandId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderEventEntity>(entity =>
        {
            entity.ToTable("order_events", "execution");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.EventType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.FromState).HasColumnType("smallint").HasConversion<short?>();
            entity.Property(e => e.ToState).HasColumnType("smallint").HasConversion<short?>();
            entity.Property(e => e.Quantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Price).HasColumnType("numeric(28,12)");
            entity.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.OrderId, e.OccurredAt });
        });

        modelBuilder.Entity<BrokerTransactionEntity>(entity =>
        {
            entity.ToTable("broker_transactions", "execution");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.BrokerTime).HasColumnType("timestamptz");
            entity.Property(e => e.ReceivedAt).HasColumnType("timestamptz");
            entity.Property(e => e.TransactionType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.Quantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Price).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RawPayloadJson).HasColumnName("raw_payload").HasColumnType("jsonb");
            entity.Property(e => e.AppliedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<BrokerTransactionKeyEntity>(entity =>
        {
            entity.ToTable("broker_transaction_keys", "execution");
            entity.HasKey(e => new { e.BrokerAccountId, e.BrokerTransactionId });
            entity.Property(e => e.BrokerTime).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<FillEntity>(entity =>
        {
            entity.ToTable("fills", "execution");
            entity.HasKey(e => e.FillId);
            entity.HasIndex(e => new { e.BrokerAccountId, e.BrokerTransactionId }).IsUnique();
            entity.Property(e => e.Quantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Price).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Commission).HasColumnType("numeric(28,8)");
            entity.Property(e => e.Financing).HasColumnType("numeric(28,8)");
            entity.Property(e => e.SpreadCost).HasColumnType("numeric(28,8)");
            entity.Property(e => e.SlippageCost).HasColumnType("numeric(28,8)");
            entity.Property(e => e.BrokerTime).HasColumnType("timestamptz");
            entity.Property(e => e.ReceivedAt).HasColumnType("timestamptz");
            entity.HasOne<OrderEntity>().WithMany().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PositionEntity>(entity =>
        {
            entity.ToTable("positions", "execution");
            entity.HasKey(e => e.PositionId);
            entity.HasIndex(e => new { e.BrokerAccountId, e.BrokerTradeId, e.StrategyId }).IsUnique();
            entity.Property(e => e.StrategyId).HasColumnType("text");
            entity.Property(e => e.Direction).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.State).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.OriginalQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RemainingQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.AverageEntryPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.InitialStopPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.CurrentStopPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.TargetPrice).HasColumnType("numeric(28,12)");
            entity.Property(e => e.OriginalRisk).HasColumnType("numeric(28,8)");
            entity.Property(e => e.RemainingRisk).HasColumnType("numeric(28,8)");
            entity.Property(e => e.RealisedPnl).HasColumnType("numeric(28,8)");
            entity.Property(e => e.UnrealisedPnl).HasColumnType("numeric(28,8)");
            entity.Property(e => e.Commission).HasColumnType("numeric(28,8)");
            entity.Property(e => e.Financing).HasColumnType("numeric(28,8)");
            entity.Property(e => e.OpenedAt).HasColumnType("timestamptz");
            entity.Property(e => e.ClosedAt).HasColumnType("timestamptz");
            entity.HasOne<PolicyRevisionEntity>().WithMany().HasForeignKey(e => e.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PositionEventEntity>(entity =>
        {
            entity.ToTable("position_events", "execution");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventId).UseIdentityAlwaysColumn();
            entity.Property(e => e.OccurredAt).HasColumnType("timestamptz");
            entity.Property(e => e.EventType).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.QuantityDelta).HasColumnType("numeric(28,12)");
            entity.Property(e => e.Price).HasColumnType("numeric(28,12)");
            entity.Property(e => e.RealisedPnlDelta).HasColumnType("numeric(28,8)");
            entity.Property(e => e.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            entity.HasIndex(e => new { e.PositionId, e.OccurredAt });
        });
    }
}
