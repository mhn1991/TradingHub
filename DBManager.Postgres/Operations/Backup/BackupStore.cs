using DBManager.Abstractions.Operations;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Operations.Backup;

public sealed class BackupStore(IDbContextFactory<TradingHubDbContext> contextFactory) : IBackupStore
{
    public async Task RecordBackupStartedAsync(RecordBackupStarted command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new BackupRecordEntity
        {
            BackupId = command.BackupId,
            FilePath = command.FilePath,
            StartedAt = DateTimeOffset.UtcNow,
            RestoreTestStatus = RestoreTestStatus.NotTested
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordBackupCompletedAsync(RecordBackupCompleted command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        BackupRecordEntity entity = await context.Set<BackupRecordEntity>()
            .FirstAsync(b => b.BackupId == command.BackupId, cancellationToken).ConfigureAwait(false);
        entity.CompletedAt = DateTimeOffset.UtcNow;
        entity.PostgreSqlVersion = command.PostgreSqlVersion;
        entity.SchemaVersion = command.SchemaVersion;
        entity.Checksum = command.Checksum;
        entity.SizeBytes = command.SizeBytes;
        entity.RetentionExpiresAt = command.RetentionExpiresAt;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordBackupFailedAsync(RecordBackupFailed command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        BackupRecordEntity entity = await context.Set<BackupRecordEntity>()
            .FirstAsync(b => b.BackupId == command.BackupId, cancellationToken).ConfigureAwait(false);
        entity.FailureReason = command.FailureReason;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRestoreTestAsync(RecordRestoreTest command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        BackupRecordEntity entity = await context.Set<BackupRecordEntity>()
            .FirstAsync(b => b.BackupId == command.BackupId, cancellationToken).ConfigureAwait(false);
        entity.RestoreTestStatus = command.Status;
        entity.RestoreTestedAt = DateTimeOffset.UtcNow;
        entity.RestoreFailureDetail = command.FailureDetail;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackupRecordDetail?> GetLatestVerifiedBackupAsync(CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        BackupRecordEntity? entity = await context.Set<BackupRecordEntity>()
            .AsNoTracking()
            .Where(b => b.RestoreTestStatus == RestoreTestStatus.Passed)
            .OrderByDescending(b => b.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToDetail(entity);
    }

    public async Task<IReadOnlyList<BackupRecordDetail>> GetExpiredBackupsAsync(CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<BackupRecordEntity> entities = await context.Set<BackupRecordEntity>()
            .AsNoTracking()
            .Where(b => b.RetentionExpiresAt != null && b.RetentionExpiresAt < now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entities.Select(ToDetail).ToArray();
    }

    private static BackupRecordDetail ToDetail(BackupRecordEntity entity) => new()
    {
        BackupId = entity.BackupId,
        FilePath = entity.FilePath,
        StartedAt = entity.StartedAt,
        CompletedAt = entity.CompletedAt,
        Checksum = entity.Checksum,
        RestoreTestStatus = entity.RestoreTestStatus,
        RestoreTestedAt = entity.RestoreTestedAt,
        RetentionExpiresAt = entity.RetentionExpiresAt
    };
}
