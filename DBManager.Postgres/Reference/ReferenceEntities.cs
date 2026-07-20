using DBManager.Abstractions.Config;
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

public sealed class BrokerEnvironmentEntity
{
    public Guid BrokerEnvironmentId { get; set; }
    public long BrokerId { get; set; }
    public required string EnvironmentCode { get; set; }
    public required string DisplayName { get; set; }
    public bool IsLive { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BrokerEndpointRevisionEntity
{
    public Guid BrokerEndpointRevisionId { get; set; }
    public Guid BrokerEnvironmentId { get; set; }
    public BrokerEndpointKind Kind { get; set; }
    public int Revision { get; set; }
    public required string BaseAddress { get; set; }
    public required string ContentHash { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
}

/// <summary>Only provider coordinates are stored. This row can never hold secret material.</summary>
public sealed class CredentialReferenceEntity
{
    public Guid CredentialReferenceId { get; set; }
    public Guid BrokerEnvironmentId { get; set; }
    public required string Purpose { get; set; }
    public required string Provider { get; set; }
    public required string SecretKey { get; set; }
    public string? SecretVersion { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BrokerAccountSettingsRevisionEntity
{
    public Guid BrokerAccountSettingsRevisionId { get; set; }
    public Guid BrokerEnvironmentId { get; set; }
    public Guid? BrokerAccountId { get; set; }
    public int Revision { get; set; }
    public required string AccountAlias { get; set; }
    public string? ExternalAccountId { get; set; }
    public required string AccountCurrency { get; set; }
    public required string SettingsJson { get; set; }
    public required string ContentHash { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
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

        modelBuilder.Entity<BrokerEnvironmentEntity>(entity =>
        {
            entity.ToTable("broker_environments", "reference");
            entity.HasKey(e => e.BrokerEnvironmentId);
            entity.Property(e => e.EnvironmentCode).HasColumnType("text");
            entity.Property(e => e.DisplayName).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.BrokerId, e.EnvironmentCode }).IsUnique();
            entity.HasOne<BrokerEntity>().WithMany().HasForeignKey(e => e.BrokerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BrokerEndpointRevisionEntity>(entity =>
        {
            entity.ToTable("broker_endpoint_revisions", "reference");
            entity.HasKey(e => e.BrokerEndpointRevisionId);
            entity.Property(e => e.Kind).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.BaseAddress).HasColumnType("text");
            entity.Property(e => e.ContentHash).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnType("text");
            entity.HasIndex(e => new { e.BrokerEnvironmentId, e.Kind, e.Revision }).IsUnique();
            entity.HasIndex(e => new { e.BrokerEnvironmentId, e.Kind })
                .HasFilter("active").IsUnique()
                .HasDatabaseName("ix_broker_endpoint_revisions_one_active");
            entity.HasOne<BrokerEnvironmentEntity>().WithMany().HasForeignKey(e => e.BrokerEnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CredentialReferenceEntity>(entity =>
        {
            entity.ToTable("credential_references", "reference");
            entity.HasKey(e => e.CredentialReferenceId);
            entity.Property(e => e.Purpose).HasColumnType("text");
            entity.Property(e => e.Provider).HasColumnType("text");
            entity.Property(e => e.SecretKey).HasColumnType("text");
            entity.Property(e => e.SecretVersion).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.BrokerEnvironmentId, e.Purpose })
                .HasFilter("active").IsUnique()
                .HasDatabaseName("ix_credential_references_one_active");
            entity.HasOne<BrokerEnvironmentEntity>().WithMany().HasForeignKey(e => e.BrokerEnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BrokerAccountSettingsRevisionEntity>(entity =>
        {
            entity.ToTable("broker_account_settings_revisions", "reference");
            entity.HasKey(e => e.BrokerAccountSettingsRevisionId);
            entity.Property(e => e.AccountAlias).HasColumnType("text");
            entity.Property(e => e.ExternalAccountId).HasColumnType("text");
            entity.Property(e => e.AccountCurrency).HasColumnType("char(3)");
            entity.Property(e => e.SettingsJson).HasColumnName("settings").HasColumnType("jsonb");
            entity.Property(e => e.ContentHash).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CreatedBy).HasColumnType("text");
            entity.HasIndex(e => new { e.BrokerEnvironmentId, e.Revision }).IsUnique();
            entity.HasIndex(e => e.BrokerEnvironmentId).HasFilter("active").IsUnique()
                .HasDatabaseName("ix_broker_account_settings_one_active");
            entity.HasOne<BrokerEnvironmentEntity>().WithMany().HasForeignKey(e => e.BrokerEnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<BrokerAccountEntity>().WithMany().HasForeignKey(e => e.BrokerAccountId)
                .OnDelete(DeleteBehavior.Restrict);
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
