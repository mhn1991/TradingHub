using Brokers.Abstractions;
using Brokers.Infrastructure;
using Brokers.Models;
using Networking.Abstractions;

namespace Brokers.Binance;

internal sealed class BinanceMarketDataClient(
    INetworkGateway gateway,
    TransportId transportId,
    TimeProvider timeProvider,
    IReadOnlyDictionary<string, string> instrumentMappings) : IMarketDataClient
{
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(1000, "Binance");

        BinanceKlineRow[] response = await gateway.SendAsync(
            new BinanceGetKlinesCommand(
                transportId,
                InstrumentMappers.ToBinance(query.Instrument, instrumentMappings),
                BinanceMappings.ToInterval(query.Interval),
                query.Limit,
                query.From,
                query.To),
            cancellationToken).ConfigureAwait(false);

        return response.Select(row => BinanceMappings.ToCandle(row, query, timeProvider)).ToArray();
    }
}

internal sealed class BinanceAccountClient(
    INetworkGateway gateway,
    TransportId transportId,
    BinanceRequestSigner signer) : IAccountClient
{
    public async Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        BinanceAccountResponse response = await gateway.SendAsync(
            new BinanceGetAccountCommand(transportId, signer),
            cancellationToken).ConfigureAwait(false);

        return
        [
            new AccountSnapshot
            {
                AccountId = "spot",
                AccountType = response.AccountType,
                CanTrade = response.CanTrade,
                AssetBalances = response.Balances
                    .Select(balance => new AssetBalance(
                        balance.Asset,
                        BrokerJson.ParseDecimal(balance.Free),
                        BrokerJson.ParseDecimal(balance.Locked)))
                    .ToArray()
            }
        ];
    }
}

internal sealed class BinanceOrderClient(
    INetworkGateway gateway,
    TransportId transportId,
    BinanceRequestSigner signer,
    IReadOnlyDictionary<string, string> instrumentMappings) : IOrderClient
{
    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default)
    {
        string? symbol = instrument is null
            ? null
            : InstrumentMappers.ToBinance(instrument.Value, instrumentMappings);
        BinanceOrderDto[] response = await gateway.SendAsync(
            new BinanceGetOpenOrdersCommand(transportId, signer, symbol),
            cancellationToken).ConfigureAwait(false);

        return response.Select(order => BinanceMappings.ToOrder(order, instrumentMappings)).ToArray();
    }
}

internal sealed class BinanceCostClient(
    INetworkGateway gateway,
    TransportId transportId,
    BinanceRequestSigner signer,
    IReadOnlyDictionary<string, string> instrumentMappings) : ICostClient
{
    public async Task<CommissionSchedule> GetCommissionAsync(
        InstrumentKey instrument,
        CancellationToken cancellationToken = default)
    {
        BinanceCommissionResponse response = await gateway.SendAsync(
            new BinanceGetCommissionCommand(
                transportId,
                signer,
                InstrumentMappers.ToBinance(instrument, instrumentMappings)),
            cancellationToken).ConfigureAwait(false);

        return new CommissionSchedule
        {
            Instrument = instrument,
            Maker = BrokerJson.ParseNullableDecimal(response.StandardCommission?.Maker),
            Taker = BrokerJson.ParseNullableDecimal(response.StandardCommission?.Taker),
            Buyer = BrokerJson.ParseNullableDecimal(response.StandardCommission?.Buyer),
            Seller = BrokerJson.ParseNullableDecimal(response.StandardCommission?.Seller),
            IsDiscountEnabled = response.Discount?.EnabledForAccount == true &&
                response.Discount.EnabledForSymbol == true,
            DiscountAsset = response.Discount?.DiscountAsset,
            Discount = BrokerJson.ParseNullableDecimal(response.Discount?.Discount),
            Source = "Binance account commission endpoint"
        };
    }
}
