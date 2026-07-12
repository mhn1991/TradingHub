using Brokers;
using Brokers.Abstractions;
using Brokers.Binance;
using Brokers.Models;
using NUnit.Framework;

namespace Brokers.IntegrationTests;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class BinanceIntegrationTests
{
    private IBrokerClient _broker = null!;
    private InstrumentKey _instrument;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _instrument = new InstrumentKey(
            IntegrationTestEnvironment.Optional("BINANCE_TEST_INSTRUMENT") ?? "CRYPTO:BTC/USDT");

        _broker = BrokerClientFactory.CreateBinance(new BinanceOptions
        {
            Environment = IntegrationTestEnvironment.ParseBrokerEnvironment("BINANCE_ENVIRONMENT"),
            ApiKey = IntegrationTestEnvironment.Required("BINANCE_API_KEY"),
            SecretKey = IntegrationTestEnvironment.Required("BINANCE_SECRET_KEY"),
            BaseAddress = IntegrationTestEnvironment.OptionalUri("BINANCE_BASE_URL")
        });
    }

    [Test]
    [Explicit("Calls a real signed Binance account endpoint.")]
    public async Task AccountInformation_UsesSignedNetworkingRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<AccountSnapshot> accounts =
            await _broker.Accounts.GetAccountsAsync(timeout.Token);

        Assert.That(accounts, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(accounts[0].AccountId, Is.EqualTo("spot"));
            Assert.That(accounts[0].AssetBalances, Is.Not.Null);
            Assert.That(accounts[0].CanTrade, Is.Not.Null);
        });
    }

    [Test]
    [Explicit("Calls the real Binance kline endpoint.")]
    public async Task FiveMinuteCandles_AreReturnedAsStandardCandles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<Candle> candles = await _broker.MarketData.GetCandlesAsync(
            new CandleQuery(_instrument, BarInterval.Minutes(5), Limit: 20),
            timeout.Token);

        Assert.That(candles, Has.Count.EqualTo(20));
        Assert.Multiple(() =>
        {
            Assert.That(candles.All(candle => candle.Instrument == _instrument), Is.True);
            Assert.That(candles.All(candle => candle.Prices.High >= candle.Prices.Low), Is.True);
            Assert.That(candles.All(candle => candle.Volume?.Kind == VolumeKind.BaseAssetQuantity), Is.True);
        });
    }

    [Test]
    [Explicit("Calls real signed Binance open-order and commission endpoints.")]
    public async Task OpenOrdersAndCommission_AreReadWithoutPlacingAnOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<BrokerOrder> orders = await _broker.Orders.GetOpenOrdersAsync(
            _instrument,
            timeout.Token);
        CommissionSchedule commission = await _broker.Costs.GetCommissionAsync(
            _instrument,
            timeout.Token);

        Assert.Multiple(() =>
        {
            Assert.That(orders, Is.Not.Null);
            Assert.That(commission.Source, Is.Not.Empty);
            Assert.That(commission.Maker, Is.Not.Null);
            Assert.That(commission.Taker, Is.Not.Null);
        });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_broker is not null)
        {
            await _broker.DisposeAsync();
        }
    }
}
