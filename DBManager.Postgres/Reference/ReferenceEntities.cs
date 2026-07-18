using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Reference;

/// <summary>EF Core entity for <c>reference.brokers</c> (design doc section 7.1).</summary>
public sealed class BrokerEntity
{
    public long BrokerId { get; set; }
    public required string Code { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>EF Core entity for <c>reference.broker_accounts</c>. Never stores API tokens.</summary>
public sealed class BrokerAccountEntity
{
    public Guid BrokerAccountId { get; set; }
    public long BrokerId { get; set; }
    public required string ExternalAccountKeyHash { get; set; }
    public required string MaskedAccountName { get; set; }
    public short Environment { get; set; }
    public required string AccountCurrency { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>EF Core entity for <c>reference.instruments</c>.</summary>
public sealed class InstrumentEntity
{
    public long InstrumentId { get; set; }
    public required string CanonicalKey { get; set; }
    public short AssetClass { get; set; }
    public string? BaseCurrency { get; set; }
    public string? QuoteCurrency { get; set; }
    public required string DisplayName { get; set; }
}

/// <summary>
/// EF Core entity for <c>reference.broker_instruments</c>. Versioned because broker size and
/// margin rules may change (section 7.1).
/// </summary>
public sealed class BrokerInstrumentEntity
{
    public Guid BrokerAccountId { get; set; }
    public long InstrumentId { get; set; }
    public long MetadataRevision { get; set; }
    public required string BrokerSymbol { get; set; }
    public decimal MinimumQuantity { get; set; }
    public decimal QuantityStep { get; set; }
    public decimal? MaximumQuantity { get; set; }
    public short PricePrecision { get; set; }
    public short QuantityPrecision { get; set; }
    public short? PipLocation { get; set; }
    public decimal MarginRate { get; set; }
    public bool Tradeable { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
}

internal static class ReferenceModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BrokerEntity>(entity =>
        {
            entity.ToTable("brokers", "reference");
            entity.HasKey(e => e.BrokerId);
            entity.Property(e => e.BrokerId).UseIdentityAlwaysColumn();
            entity.Property(e => e.Code).HasColumnType("text");
            entity.HasIndex(e => e.Code).IsUnique();
            entity.Property(e => e.DisplayName).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<BrokerAccountEntity>(entity =>
        {
            entity.ToTable("broker_accounts", "reference");
            entity.HasKey(e => e.BrokerAccountId);
            entity.Property(e => e.ExternalAccountKeyHash).HasColumnType("text");
            entity.Property(e => e.MaskedAccountName).HasColumnType("text");
            entity.Property(e => e.AccountCurrency).HasColumnType("char(3)");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.HasOne<BrokerEntity>()
                .WithMany()
                .HasForeignKey(e => e.BrokerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstrumentEntity>(entity =>
        {
            entity.ToTable("instruments", "reference");
            entity.HasKey(e => e.InstrumentId);
            entity.Property(e => e.InstrumentId).UseIdentityAlwaysColumn();
            entity.Property(e => e.CanonicalKey).HasColumnType("text");
            entity.HasIndex(e => e.CanonicalKey).IsUnique();
            entity.Property(e => e.BaseCurrency).HasColumnType("char(3)");
            entity.Property(e => e.QuoteCurrency).HasColumnType("char(3)");
            entity.Property(e => e.DisplayName).HasColumnType("text");
        });

        modelBuilder.Entity<BrokerInstrumentEntity>(entity =>
        {
            entity.ToTable("broker_instruments", "reference");
            entity.HasKey(e => new { e.BrokerAccountId, e.InstrumentId, e.MetadataRevision });
            entity.Property(e => e.BrokerSymbol).HasColumnType("text");
            entity.Property(e => e.MinimumQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.QuantityStep).HasColumnType("numeric(28,12)");
            entity.Property(e => e.MaximumQuantity).HasColumnType("numeric(28,12)");
            entity.Property(e => e.MarginRate).HasColumnType("numeric(18,10)");
            entity.Property(e => e.ValidFrom).HasColumnType("timestamptz");
            entity.Property(e => e.ValidTo).HasColumnType("timestamptz");
            entity.HasOne<BrokerAccountEntity>()
                .WithMany()
                .HasForeignKey(e => e.BrokerAccountId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<InstrumentEntity>()
                .WithMany()
                .HasForeignKey(e => e.InstrumentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
