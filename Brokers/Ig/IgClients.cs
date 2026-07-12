using Brokers.Abstractions;
using Brokers.Infrastructure;
using Brokers.Models;
using Networking.Abstractions;

namespace Brokers.Ig;

internal sealed class IgMarketDataClient(
    TransportId transportId,
    IgSessionManager sessions,
    IReadOnlyDictionary<string, string> instrumentMappings,
    TimeProvider timeProvider) : IMarketDataClient
{
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(10000, "IG");

        if (query.From is not null || query.To is not null)
        {
            throw new NotSupportedException(
                "This first IG implementation supports count-based candle queries only.");
        }

        IgPricesResponse response = await sessions.SendAuthenticatedAsync(
            session => new IgGetPricesCommand(
                transportId,
                session.Tokens,
                InstrumentMappers.ToIg(query.Instrument, instrumentMappings),
                IgMappings.ToResolution(query.Interval),
                query.Limit),
            cancellationToken).ConfigureAwait(false);

        return response.Prices
            .Select(price => IgMappings.ToCandle(price, query, timeProvider))
            .ToArray();
    }
}

internal sealed class IgAccountClient(
    TransportId transportId,
    IgSessionManager sessions) : IAccountClient
{
    public async Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        IgAccountDto[] accounts = await sessions.SendAuthenticatedAsync(
            session => new IgGetAccountsCommand(transportId, session.Tokens),
            cancellationToken).ConfigureAwait(false);

        return accounts.Select(account => new AccountSnapshot
        {
            AccountId = account.AccountId,
            AccountType = account.AccountType,
            Currency = account.Currency,
            Balance = account.Balance?.Balance,
            Available = account.Balance?.Available,
            MarginUsed = account.Balance?.Deposit,
            UnrealizedProfitLoss = account.Balance?.ProfitLoss,
            CanTrade = string.Equals(account.Status, "ENABLED", StringComparison.OrdinalIgnoreCase)
        }).ToArray();
    }
}

internal sealed class IgOrderClient(
    TransportId transportId,
    IgSessionManager sessions,
    IReadOnlyDictionary<string, string> instrumentMappings) : IOrderClient
{
    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default)
    {
        string? nativeFilter = instrument is null
            ? null
            : InstrumentMappers.ToIg(instrument.Value, instrumentMappings);

        IgWorkingOrdersResponse response = await sessions.SendAuthenticatedAsync(
            session => new IgGetWorkingOrdersCommand(transportId, session.Tokens),
            cancellationToken).ConfigureAwait(false);

        return response.WorkingOrders
            .Where(order => nativeFilter is null ||
                string.Equals(
                    order.MarketData?.Epic,
                    nativeFilter,
                    StringComparison.OrdinalIgnoreCase))
            .Select(order => IgMappings.ToOrder(order, instrumentMappings))
            .ToArray();
    }
}

internal sealed class IgPositionClient(
    TransportId transportId,
    IgSessionManager sessions,
    IReadOnlyDictionary<string, string> instrumentMappings) : IPositionClient
{
    public async Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        IgPositionsResponse response = await sessions.SendAuthenticatedAsync(
            session => new IgGetPositionsCommand(transportId, session.Tokens),
            cancellationToken).ConfigureAwait(false);

        return response.Positions
            .Select(position => IgMappings.ToPosition(position, instrumentMappings))
            .ToArray();
    }
}
