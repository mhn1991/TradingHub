using System.Net;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Binance;
using Brokers.Ig;
using Brokers.Infrastructure;
using Brokers.Oanda;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class BrokerJsonAotTests
{
    [Test]
    public async Task BinanceResponseTypes_UseSourceGeneratedMetadata()
    {
        JsonElement[][] klines = await ReadAsync<JsonElement[][]>(
            BrokerKind.Binance,
            "[[1,\"1\",\"2\",\"0.5\",\"1.5\",\"10\",2]]");
        BinanceKlineRow[] rows = await ReadAsync<BinanceKlineRow[]>(BrokerKind.Binance, "[]");
        BinanceAccountResponse account = await ReadAsync<BinanceAccountResponse>(
            BrokerKind.Binance,
            "{\"canTrade\":true,\"balances\":[{\"asset\":\"USD\",\"free\":\"1\",\"locked\":\"0\"}]}");
        BinanceOrderDto[] orders = await ReadAsync<BinanceOrderDto[]>(BrokerKind.Binance, "[]");
        BinanceCommissionResponse commission = await ReadAsync<BinanceCommissionResponse>(
            BrokerKind.Binance,
            "{\"symbol\":\"BTCUSDT\"}");

        Assert.Multiple(() =>
        {
            Assert.That(klines, Has.Length.EqualTo(1));
            Assert.That(rows, Is.Empty);
            Assert.That(account.CanTrade, Is.True);
            Assert.That(account.Balances.Single().Asset, Is.EqualTo("USD"));
            Assert.That(orders, Is.Empty);
            Assert.That(commission.Symbol, Is.EqualTo("BTCUSDT"));
        });
    }

    [Test]
    public async Task OandaResponseTypes_UseSourceGeneratedMetadata()
    {
        OandaCandlesResponse candles = await ReadAsync<OandaCandlesResponse>(
            BrokerKind.Oanda,
            "{\"candles\":[]}");
        OandaAccountSummaryResponse account = await ReadAsync<OandaAccountSummaryResponse>(
            BrokerKind.Oanda,
            "{\"account\":{\"id\":\"account-1\",\"balance\":\"100\"}}");
        OandaPendingOrdersResponse orders = await ReadAsync<OandaPendingOrdersResponse>(
            BrokerKind.Oanda,
            "{\"orders\":[]}");
        OandaOpenPositionsResponse positions = await ReadAsync<OandaOpenPositionsResponse>(
            BrokerKind.Oanda,
            "{\"positions\":[]}");
        OandaInstrumentsResponse instruments = await ReadAsync<OandaInstrumentsResponse>(
            BrokerKind.Oanda,
            "{\"instruments\":[{\"name\":\"EUR_USD\"}]}");
        OandaOrderMutationResponse mutation = await ReadAsync<OandaOrderMutationResponse>(
            BrokerKind.Oanda,
            "{\"orderCreateTransaction\":{\"id\":\"1\",\"type\":\"MARKET_ORDER\"}}");
        OandaPricingStreamMessage price = await ReadAsync<OandaPricingStreamMessage>(
            BrokerKind.Oanda,
            "{\"type\":\"PRICE\",\"instrument\":\"EUR_USD\",\"bids\":[],\"asks\":[]}");

        Assert.Multiple(() =>
        {
            Assert.That(candles.Candles, Is.Empty);
            Assert.That(account.Account.Id, Is.EqualTo("account-1"));
            Assert.That(orders.Orders, Is.Empty);
            Assert.That(positions.Positions, Is.Empty);
            Assert.That(instruments.Instruments.Single().Name, Is.EqualTo("EUR_USD"));
            Assert.That(mutation.OrderCreateTransaction!.Id, Is.EqualTo("1"));
            Assert.That(price.Instrument, Is.EqualTo("EUR_USD"));
        });
    }

    [Test]
    public async Task IgResponseAndRequestTypes_UseSourceGeneratedMetadata()
    {
        IgLoginResponse login = await ReadAsync<IgLoginResponse>(
            BrokerKind.Ig,
            "{\"currentAccountId\":\"account-1\"}");
        IgSwitchAccountResponse accountSwitch = await ReadAsync<IgSwitchAccountResponse>(
            BrokerKind.Ig,
            "{\"dealingEnabled\":true}");
        IgAccountDto[] accounts = await ReadAsync<IgAccountDto[]>(
            BrokerKind.Ig,
            "[{\"accountId\":\"account-1\"}]");
        IgPricesResponse prices = await ReadAsync<IgPricesResponse>(BrokerKind.Ig, "{\"prices\":[]}");
        IgPositionsResponse positions = await ReadAsync<IgPositionsResponse>(
            BrokerKind.Ig,
            "{\"positions\":[]}");
        IgWorkingOrdersResponse orders = await ReadAsync<IgWorkingOrdersResponse>(
            BrokerKind.Ig,
            "{\"workingOrders\":[]}");
        string loginRequest = BrokerJson.Serialize(
            new IgLoginRequest("user", "secret", EncryptedPassword: false));
        string switchRequest = BrokerJson.Serialize(
            new IgSwitchAccountRequest("account-2", DefaultAccount: false));

        Assert.Multiple(() =>
        {
            Assert.That(login.CurrentAccountId, Is.EqualTo("account-1"));
            Assert.That(accountSwitch.DealingEnabled, Is.True);
            Assert.That(accounts.Single().AccountId, Is.EqualTo("account-1"));
            Assert.That(prices.Prices, Is.Empty);
            Assert.That(positions.Positions, Is.Empty);
            Assert.That(orders.WorkingOrders, Is.Empty);
            Assert.That(loginRequest, Does.Contain("\"identifier\":\"user\""));
            Assert.That(loginRequest, Does.Contain("\"encryptedPassword\":false"));
            Assert.That(switchRequest, Does.Contain("\"accountId\":\"account-2\""));
        });
    }

    private static async Task<T> ReadAsync<T>(BrokerKind broker, string json)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };

        return await BrokerJson.ReadAsync<T>(broker, response, CancellationToken.None);
    }
}
