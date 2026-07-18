using DBManager.Abstractions.Operations;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Operations.Backup;

/// <summary>
/// Backup/restore-verification metadata (section 27). Not part of the doc's section 7 table
/// catalog — backups aren't covered there — designed this session to satisfy section 27's
/// explicit "store: backup ID, start/end time, PostgreSQL version, schema version, checksum,
/// restore-test status, retention expiry" list.
/// </summary>
public sealed class BackupRecordEntity
{
    public Guid BackupId { get; set; }
    public required string FilePath { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? PostgreSqlVersion { get; set; }
    public string? SchemaVersion { get; set; }
    public string? Checksum { get; set; }
    public long? SizeBytes { get; set; }
    public string? FailureReason { get; set; }
    public RestoreTestStatus RestoreTestStatus { get; set; }
    public DateTimeOffset? RestoreTestedAt { get; set; }
    public string? RestoreFailureDetail { get; set; }
    public DateTimeOffset? RetentionExpiresAt { get; set; }
}

internal static class BackupModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupRecordEntity>(entity =>
        {
            entity.ToTable("backup_records", "operations");
            entity.HasKey(e => e.BackupId);
            entity.Property(e => e.FilePath).HasColumnType("text");
            entity.Property(e => e.StartedAt).HasColumnType("timestamptz");
            entity.Property(e => e.CompletedAt).HasColumnType("timestamptz");
            entity.Property(e => e.PostgreSqlVersion).HasColumnType("text");
            entity.Property(e => e.SchemaVersion).HasColumnType("text");
            entity.Property(e => e.Checksum).HasColumnType("text");
            entity.Property(e => e.FailureReason).HasColumnType("text");
            entity.Property(e => e.RestoreTestStatus).HasColumnType("smallint").HasConversion<short>();
            entity.Property(e => e.RestoreTestedAt).HasColumnType("timestamptz");
            entity.Property(e => e.RestoreFailureDetail).HasColumnType("text");
            entity.Property(e => e.RetentionExpiresAt).HasColumnType("timestamptz");
            entity.HasIndex(e => e.RetentionExpiresAt);
        });
    }
}
