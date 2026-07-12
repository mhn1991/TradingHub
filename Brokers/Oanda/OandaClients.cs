using Brokers.Abstractions;
using Brokers.Infrastructure;
using Brokers.Models;
using Networking.Abstractions;

namespace Brokers.Oanda;

internal sealed class OandaMarketDataClient(
    INetworkGateway gateway,
    TransportId transportId,
    IReadOnlyDictionary<string, string> instrumentMappings) : IMarketDataClient
{
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(5000, "OANDA");

        var command = new OandaGetCandlesCommand(
            transportId,
            InstrumentMappers.ToOanda(query.Instrument, instrumentMappings),
            OandaMappings.ToGranularity(query.Interval),
            query.Limit,
            query.From,
            query.To);

        OandaCandlesResponse response = await gateway
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        return response.Candles.Select(candle => OandaMappings.ToCandle(candle, query)).ToArray();
    }

}

internal sealed class OandaAccountClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId) : IAccountClient
{
    public async Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        OandaAccountSummaryResponse response = await gateway.SendAsync(
            new OandaGetAccountSummaryCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);

        OandaAccountSummary account = response.Account;
        return
        [
            new AccountSnapshot
            {
                AccountId = account.Id ?? accountId,
                AccountType = "Margin",
                Currency = account.Currency,
                Balance = BrokerJson.ParseNullableDecimal(account.Balance),
                Available = BrokerJson.ParseNullableDecimal(account.MarginAvailable),
                MarginUsed = BrokerJson.ParseNullableDecimal(account.MarginUsed),
                UnrealizedProfitLoss = BrokerJson.ParseNullableDecimal(account.UnrealizedPl),
                CanTrade = account.TradingDisabled is null ? null : !account.TradingDisabled
            }
        ];
    }
}

internal sealed class OandaOrderClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId,
    IReadOnlyDictionary<string, string> instrumentMappings) : IOrderClient
{
    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default)
    {
        OandaPendingOrdersResponse response = await gateway.SendAsync(
            new OandaGetPendingOrdersCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);

        string? nativeFilter = instrument is null
            ? null
            : InstrumentMappers.ToOanda(instrument.Value, instrumentMappings);

        return response.Orders
            .Where(order => nativeFilter is null ||
                string.Equals(order.Instrument, nativeFilter, StringComparison.OrdinalIgnoreCase))
            .Select(order => OandaMappings.ToOrder(order, instrumentMappings))
            .ToArray();
    }
}

internal sealed class OandaPositionClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId,
    IReadOnlyDictionary<string, string> instrumentMappings) : IPositionClient
{
    public async Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        OandaOpenPositionsResponse response = await gateway.SendAsync(
            new OandaGetOpenPositionsCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);

        var result = new List<BrokerPosition>();
        foreach (OandaPosition position in response.Positions)
        {
            OandaMappings.AddPositionSide(
                result, position, position.Long, OrderSide.Buy, "long", instrumentMappings);
            OandaMappings.AddPositionSide(
                result, position, position.Short, OrderSide.Sell, "short", instrumentMappings);
        }

        return result;
    }
}
