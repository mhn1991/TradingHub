using Brokers;
using Brokers.Abstractions;
using Brokers.Models;
using Brokers.Oanda;
using NUnit.Framework;

namespace Brokers.IntegrationTests;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class OandaIntegrationTests
{
    private IBrokerClient _broker = null!;
    private InstrumentKey _instrument;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _instrument = new InstrumentKey(
            IntegrationTestEnvironment.Optional("OANDA_TEST_INSTRUMENT") ?? "FX:EUR/USD");

        _broker = BrokerClientFactory.CreateOanda(new OandaOptions
        {
            Environment = IntegrationTestEnvironment.ParseBrokerEnvironment("OANDA_ENVIRONMENT"),
            AccessToken = IntegrationTestEnvironment.Required("OANDA_TOKEN"),
            AccountId = IntegrationTestEnvironment.Required("OANDA_ACCOUNT_ID"),
            BaseAddress = IntegrationTestEnvironment.OptionalUri("OANDA_BASE_URL")
        });
    }

    [Test]
    [Explicit("Calls the real OANDA API using environment-variable credentials.")]
    public async Task AccountSummary_TravelsThroughBrokerAndNetworking()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<AccountSnapshot> accounts =
            await _broker.Accounts.GetAccountsAsync(timeout.Token);

        Assert.That(accounts, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(accounts[0].AccountId, Is.Not.Empty);
            Assert.That(accounts[0].Currency, Is.Not.Null.And.Not.Empty);
            Assert.That(accounts[0].Balance, Is.Not.Null);
        });
    }

    [Test]
    [Explicit("Calls the real OANDA market-data endpoint.")]
    public async Task FiveMinuteCandles_AreReturnedAsStandardCandles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<Candle> candles = await _broker.MarketData.GetCandlesAsync(
            new CandleQuery(_instrument, BarInterval.Minutes(5), Limit: 20),
            timeout.Token);

        Assert.That(candles, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(candles, Has.Count.LessThanOrEqualTo(20));
            Assert.That(candles.All(candle => candle.Instrument == _instrument), Is.True);
            Assert.That(candles.All(candle => candle.Prices.High >= candle.Prices.Low), Is.True);
        });
    }

    [Test]
    [Explicit("Calls OANDA read-only order and position endpoints.")]
    public async Task OpenOrdersAndPositions_CanBeReadEvenWhenEmpty()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<BrokerOrder> orders =
            await _broker.Orders.GetOpenOrdersAsync(cancellationToken: timeout.Token);
        IReadOnlyList<BrokerPosition> positions =
            await _broker.Positions.GetOpenPositionsAsync(timeout.Token);

        Assert.Multiple(() =>
        {
            Assert.That(orders, Is.Not.Null);
            Assert.That(positions, Is.Not.Null);
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
