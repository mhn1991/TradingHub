using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBManager.Postgres.Reference;

public sealed class ReferenceDataStore(IDbContextFactory<TradingHubDbContext> contextFactory) : IReferenceDataStore
{
    public async Task<DurableResult> RegisterBrokerAsync(RegisterBroker command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Brokers.Add(new BrokerEntity
        {
            Code = command.Code,
            DisplayName = command.DisplayName,
            CreatedAt = DateTimeOffset.UtcNow
        });

        return await SaveAsync(context, "broker", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> RegisterBrokerAccountAsync(
        RegisterBrokerAccount command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        BrokerEntity? broker = await context.Brokers
            .FirstOrDefaultAsync(b => b.Code == command.BrokerCode, cancellationToken).ConfigureAwait(false);
        if (broker is null)
            return DurableResult.PermanentFailure("broker_not_found", $"No broker with code '{command.BrokerCode}'.");

        context.BrokerAccounts.Add(new BrokerAccountEntity
        {
            BrokerAccountId = command.BrokerAccountId,
            BrokerId = broker.BrokerId,
            ExternalAccountKeyHash = command.ExternalAccountKeyHash,
            MaskedAccountName = command.MaskedAccountName,
            Environment = command.Environment,
            AccountCurrency = command.AccountCurrency,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        return await SaveAsync(context, "broker_account", cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> RegisterInstrumentAsync(
        RegisterInstrument command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        InstrumentEntity entity = new()
        {
            CanonicalKey = command.CanonicalKey,
            AssetClass = command.AssetClass,
            BaseCurrency = command.BaseCurrency,
            QuoteCurrency = command.QuoteCurrency,
            DisplayName = command.DisplayName
        };
        context.Instruments.Add(entity);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return entity.InstrumentId;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return null;
        }
    }

    private static async Task<DurableResult> SaveAsync(
        TradingHubDbContext context, string entityName, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate($"{entityName}_already_exists");
        }
    }
}
