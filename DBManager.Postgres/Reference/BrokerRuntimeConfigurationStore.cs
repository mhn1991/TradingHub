using DBManager.Abstractions.Config;
using DBManager.Abstractions.Credentials;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Reference;

public sealed class BrokerRuntimeConfigurationStore(
    IDbContextFactory<TradingHubDbContext> contextFactory) : IBrokerRuntimeConfigurationStore
{
    public async Task<ResolvedBrokerRuntimeConfiguration?> GetActiveAsync(
        string brokerCode,
        string environmentCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentCode);
        string broker = brokerCode.Trim().ToUpperInvariant();
        string environment = environmentCode.Trim().ToUpperInvariant();

        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var identity = await (
                from brokerRow in context.Brokers.AsNoTracking()
                join environmentRow in context.BrokerEnvironments.AsNoTracking()
                    on brokerRow.BrokerId equals environmentRow.BrokerId
                where brokerRow.Code == broker &&
                      environmentRow.EnvironmentCode == environment &&
                      environmentRow.Enabled
                select new { Broker = brokerRow, Environment = environmentRow })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (identity is null)
            return null;

        BrokerEndpointRevisionEntity[] endpointRows = await context.BrokerEndpointRevisions.AsNoTracking()
            .Where(row => row.BrokerEnvironmentId == identity.Environment.BrokerEnvironmentId && row.Active)
            .OrderBy(row => row.Kind)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        BrokerEndpointRevisionDetail[] endpoints = endpointRows
            .Select(row => new BrokerEndpointRevisionDetail
            {
                BrokerEndpointRevisionId = row.BrokerEndpointRevisionId,
                BrokerEnvironmentId = row.BrokerEnvironmentId,
                Kind = row.Kind,
                Revision = row.Revision,
                BaseAddress = new Uri(row.BaseAddress, UriKind.Absolute),
                Active = row.Active,
                ContentHash = row.ContentHash
            })
            .ToArray();

        BrokerAccountSettingsRevisionEntity? accountRow = await context.BrokerAccountSettingsRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(row =>
                row.BrokerEnvironmentId == identity.Environment.BrokerEnvironmentId && row.Active,
                cancellationToken).ConfigureAwait(false);

        SecretReference[] references = await context.CredentialReferences.AsNoTracking()
            .Where(row => row.BrokerEnvironmentId == identity.Environment.BrokerEnvironmentId && row.Active)
            .OrderBy(row => row.Purpose)
            .Select(row => new SecretReference
            {
                CredentialReferenceId = row.CredentialReferenceId,
                Provider = row.Provider,
                SecretKey = row.SecretKey,
                Purpose = row.Purpose,
                Version = row.SecretVersion
            })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        return new ResolvedBrokerRuntimeConfiguration
        {
            Environment = new BrokerEnvironmentDetail
            {
                BrokerEnvironmentId = identity.Environment.BrokerEnvironmentId,
                BrokerId = identity.Broker.BrokerId,
                BrokerCode = identity.Broker.Code,
                EnvironmentCode = identity.Environment.EnvironmentCode,
                DisplayName = identity.Environment.DisplayName,
                IsLive = identity.Environment.IsLive,
                Enabled = identity.Environment.Enabled
            },
            Endpoints = endpoints,
            Account = accountRow is null ? null : new BrokerAccountSettingsDetail
            {
                BrokerAccountSettingsRevisionId = accountRow.BrokerAccountSettingsRevisionId,
                BrokerEnvironmentId = accountRow.BrokerEnvironmentId,
                BrokerAccountId = accountRow.BrokerAccountId,
                Revision = accountRow.Revision,
                AccountAlias = accountRow.AccountAlias,
                ExternalAccountId = accountRow.ExternalAccountId,
                AccountCurrency = accountRow.AccountCurrency,
                SettingsJson = accountRow.SettingsJson,
                Active = accountRow.Active
            },
            CredentialReferences = references
        };
    }
}
