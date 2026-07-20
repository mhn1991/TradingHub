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
    private const int CandleLimit = 20;
    private IBrokerClient _broker = null!;
    private InstrumentKey _instrument;
    private bool _hasCredentials;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _instrument = new InstrumentKey(
            IntegrationTestEnvironment.Optional("BINANCE_TEST_INSTRUMENT") ?? "CRYPTO:BTC/USDT");
        var credential = IntegrationTestEnvironment.OptionalCredential("BINANCE", "TESTNET") ??
                         IntegrationTestEnvironment.OptionalCredential("BINANCE", "LIVE");
        string? apiKey = credential?.ApiKey;
        string? secretKey = credential?.SecretKey;
        _hasCredentials = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(secretKey);

        _broker = BrokerClientFactory.CreateBinance(new BinanceOptions
        {
            Environment = credential is null
                ? BrokerEnvironment.Live
                : IntegrationTestEnvironment.ParseStoredBrokerEnvironment(credential.Environment),
            ApiKey = apiKey ?? "not-used-by-public-market-data-tests",
            SecretKey = secretKey ?? "not-used-by-public-market-data-tests",
            BaseAddress = IntegrationTestEnvironment.CredentialUri(credential?.BaseAddress, "base address") ??
                (_hasCredentials ? null : new Uri("https://data-api.binance.vision/"))
        });
    }

    [Test]
    [Explicit("Calls a real signed Binance account endpoint.")]
    public async Task AccountInformation_UsesSignedNetworkingRequest()
    {
        RequireCredentials();
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

    [TestCase(1, BarUnit.Minute)]
    [TestCase(5, BarUnit.Minute)]
    [TestCase(15, BarUnit.Minute)]
    [TestCase(1, BarUnit.Hour)]
    [TestCase(4, BarUnit.Hour)]
    [TestCase(1, BarUnit.Day)]
    [Explicit("Calls the real Binance kline endpoint for multiple timeframes.")]
    public async Task Candles_AreReturnedForMultipleTimeframes(
        int intervalValue,
        BarUnit intervalUnit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var interval = new BarInterval(intervalValue, intervalUnit);

        IReadOnlyList<Candle> candles = await _broker.MarketData.GetCandlesAsync(
            new CandleQuery(_instrument, interval, Limit: CandleLimit),
            timeout.Token);

        Assert.That(candles, Has.Count.EqualTo(CandleLimit));
        Assert.Multiple(() =>
        {
            Assert.That(candles.All(candle => candle.Instrument == _instrument), Is.True);
            Assert.That(candles.All(candle => candle.Interval == interval), Is.True);
            Assert.That(candles.Select(candle => candle.OpenTime), Is.Ordered.Ascending);
            Assert.That(
                candles.Select(candle => candle.OpenTime).Distinct().Count(),
                Is.EqualTo(candles.Count));
            Assert.That(candles.All(HasConsistentPrices), Is.True);
            Assert.That(
                candles.All(candle =>
                    candle.CloseTime.HasValue && candle.CloseTime.Value > candle.OpenTime),
                Is.True);
            Assert.That(
                candles.All(candle =>
                    candle.Volume is { Kind: VolumeKind.BaseAssetQuantity, Value: >= 0m }),
                Is.True);
        });
    }

    private static bool HasConsistentPrices(Candle candle) =>
        candle.Prices.High >= candle.Prices.Open &&
        candle.Prices.High >= candle.Prices.Close &&
        candle.Prices.Low <= candle.Prices.Open &&
        candle.Prices.Low <= candle.Prices.Close;

    [Test]
    [Explicit("Calls real signed Binance open-order and commission endpoints.")]
    public async Task OpenOrdersAndCommission_AreReadWithoutPlacingAnOrder()
    {
        RequireCredentials();
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

    private void RequireCredentials()
    {
        if (!_hasCredentials)
            Assert.Ignore("Import and enable Binance credentials in the broker credential database.");
    }
}
