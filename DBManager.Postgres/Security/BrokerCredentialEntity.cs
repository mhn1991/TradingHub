using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Security;

public sealed class BrokerCredentialEntity
{
    public required string BrokerCode { get; set; }
    public required string Environment { get; set; }
    public required byte[] ProtectedPayload { get; set; }
    public required string ProtectionScheme { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class SecurityModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BrokerCredentialEntity>(entity =>
        {
            entity.ToTable("broker_credentials", "security");
            entity.HasKey(e => new { e.BrokerCode, e.Environment });
            entity.Property(e => e.BrokerCode).HasColumnType("text");
            entity.Property(e => e.Environment).HasColumnType("text");
            entity.Property(e => e.ProtectedPayload).HasColumnType("bytea");
            entity.Property(e => e.ProtectionScheme).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnType("timestamptz");
        });
    }
}
