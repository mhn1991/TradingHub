using DBManager.Abstractions;
using DBManager.Abstractions.Management;
using DBManager.Postgres.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DBManager.Postgres.Management;

/// <summary>
/// Session-level advisory lock as the real process-exclusion mechanism, held on a dedicated
/// connection from <see cref="PersistenceLane.Lease"/> for the lease's lifetime (section 7.8 +
/// section 17). Unlike Phase 3's <c>pg_advisory_xact_lock</c> (auto-released at commit), a
/// session-level lock must be explicitly released — <see cref="DisposeAsync"/> does that and then
/// closes the dedicated connection.
/// </summary>
internal sealed class AccountLease(
    NpgsqlConnection connection, long lockKey, Guid brokerAccountId, long leaseGeneration,
    IDbContextFactory<TradingHubDbContext> contextFactory)
    : IAccountLease
{
    public Guid BrokerAccountId { get; } = brokerAccountId;
    public long LeaseGeneration { get; } = leaseGeneration;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using NpgsqlCommand unlock = new("SELECT pg_advisory_unlock($1)", connection);
            unlock.Parameters.Add(new NpgsqlParameter { Value = lockKey });
            await unlock.ExecuteNonQueryAsync().ConfigureAwait(false);

            await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync()
                .ConfigureAwait(false);
            AccountLeaseEntity? row = await context.Set<AccountLeaseEntity>()
                .FirstOrDefaultAsync(l => l.BrokerAccountId == BrokerAccountId).ConfigureAwait(false);
            if (row is not null && row.LeaseGeneration == LeaseGeneration)
            {
                row.Status = AccountLeaseStatus.Released;
                await context.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class AccountLeaseStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    [FromKeyedServices(PersistenceLane.Lease)] NpgsqlDataSource leaseDataSource)
    : IAccountLeaseStore
{
    public async Task<IAccountLease?> TryAcquireLeaseAsync(
        Guid brokerAccountId, string hostInstanceId, CancellationToken cancellationToken)
    {
        long lockKey = BitConverter.ToInt64(brokerAccountId.ToByteArray(), 0);
        NpgsqlConnection connection = await leaseDataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlCommand tryLock = new("SELECT pg_try_advisory_lock($1)", connection);
        tryLock.Parameters.Add(new NpgsqlParameter { Value = lockKey });
        bool acquired = (bool)(await tryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (!acquired)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long newGeneration;
        await using (TradingHubDbContext context = await contextFactory
                         .CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            AccountLeaseEntity? row = await context.Set<AccountLeaseEntity>()
                .FirstOrDefaultAsync(l => l.BrokerAccountId == brokerAccountId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                newGeneration = 1;
                context.Add(new AccountLeaseEntity
                {
                    BrokerAccountId = brokerAccountId,
                    HostInstanceId = hostInstanceId,
                    LeaseGeneration = newGeneration,
                    AcquiredAt = now,
                    HeartbeatAt = now,
                    Status = AccountLeaseStatus.Active
                });
            }
            else
            {
                newGeneration = row.LeaseGeneration + 1;
                row.HostInstanceId = hostInstanceId;
                row.LeaseGeneration = newGeneration;
                row.AcquiredAt = now;
                row.HeartbeatAt = now;
                row.Status = AccountLeaseStatus.Active;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new AccountLease(connection, lockKey, brokerAccountId, newGeneration, contextFactory);
    }
}
