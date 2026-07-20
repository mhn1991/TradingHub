using DBManager.Abstractions.Management;
using DBManager.Postgres;
using DBManager.Postgres.Operations;
using DBManager.Postgres.Reference;
using LiveTrading.AccountLease;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TradingHub.Persistence.Postgres.Live;

/// <summary>Adapts the runtime lease to PostgreSQL's session-level advisory account lock.</summary>
public sealed class PostgresTradingAccountLease(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : ITradingAccountLease
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAccountLease? _lease;
    private LeaseMetadata? _metadata;

    public async Task<AccountLeaseResult> TryAcquireAsync(
        string broker,
        string accountId,
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broker);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is not null)
                return new AccountLeaseResult { Outcome = AccountLeaseOutcome.Acquired, CurrentHolder = _metadata };

            Guid brokerAccountId = await ResolveBrokerAccountIdAsync(broker, accountId, cancellationToken)
                .ConfigureAwait(false);
            using IServiceScope scope = scopeFactory.CreateScope();
            IAccountLeaseStore store = scope.ServiceProvider.GetRequiredService<IAccountLeaseStore>();
            IAccountLease? lease = await store.TryAcquireLeaseAsync(
                    brokerAccountId,
                    instanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lease is null)
            {
                LeaseMetadata? holder = await ReadHolderAsync(
                    brokerAccountId, broker, accountId, cancellationToken).ConfigureAwait(false);
                return new AccountLeaseResult
                {
                    Outcome = AccountLeaseOutcome.HeldByOther,
                    CurrentHolder = holder
                };
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            _lease = lease;
            _metadata = new LeaseMetadata
            {
                Broker = broker,
                AccountId = accountId,
                InstanceId = instanceId,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                AcquiredAt = now,
                RenewedAt = now
            };
            return new AccountLeaseResult { Outcome = AccountLeaseOutcome.Acquired, CurrentHolder = _metadata };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenewAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is null || _metadata is null)
                throw new InvalidOperationException("No PostgreSQL account lease is held.");

            DateTimeOffset now = timeProvider.GetUtcNow();
            await using TradingHubDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            AccountLeaseEntity row = await context.AccountLeases
                .SingleAsync(item => item.BrokerAccountId == _lease.BrokerAccountId, cancellationToken)
                .ConfigureAwait(false);
            if (row.LeaseGeneration != _lease.LeaseGeneration || row.Status != AccountLeaseStatus.Active)
                throw new InvalidOperationException("The PostgreSQL account lease generation is no longer active.");
            row.HeartbeatAt = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _metadata = _metadata with { RenewedAt = now };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is null) return;
            await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
            _metadata = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Guid> ResolveBrokerAccountIdAsync(
        string broker,
        string accountId,
        CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        Guid? id = await (
                from settings in context.BrokerAccountSettingsRevisions.AsNoTracking()
                join environment in context.BrokerEnvironments.AsNoTracking()
                    on settings.BrokerEnvironmentId equals environment.BrokerEnvironmentId
                join brokerRow in context.Brokers.AsNoTracking() on environment.BrokerId equals brokerRow.BrokerId
                where settings.Active && settings.ExternalAccountId == accountId &&
                      brokerRow.Code.ToUpper() == broker.ToUpper()
                select settings.BrokerAccountId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return id ?? throw new InvalidOperationException(
            "No active PostgreSQL broker-account settings match the requested account lease.");
    }

    private async Task<LeaseMetadata?> ReadHolderAsync(
        Guid brokerAccountId,
        string broker,
        string accountId,
        CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AccountLeaseEntity? row = await context.AccountLeases.AsNoTracking()
            .SingleOrDefaultAsync(item => item.BrokerAccountId == brokerAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        return new LeaseMetadata
        {
            Broker = broker,
            AccountId = accountId,
            InstanceId = row.HostInstanceId,
            MachineName = "database-lease-holder",
            ProcessId = 0,
            AcquiredAt = row.AcquiredAt,
            RenewedAt = row.HeartbeatAt
        };
    }
}
