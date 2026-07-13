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
    private const int CandleLimit = 20;
    private OandaBrokerClient _broker = null!;
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
            AccountId = IntegrationTestEnvironment.Optional("OANDA_ACCOUNT_ID") ??
                "not-used-by-market-data-tests",
            BaseAddress = IntegrationTestEnvironment.OptionalUri("OANDA_BASE_URL")
        });
    }

    [Test]
    [Explicit("Calls the real OANDA API using environment-variable credentials.")]
    public async Task AccountSummary_TravelsThroughBrokerAndNetworking()
    {
        _ = IntegrationTestEnvironment.Required("OANDA_ACCOUNT_ID");
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

    [TestCase(1, BarUnit.Minute)]
    [TestCase(5, BarUnit.Minute)]
    [TestCase(15, BarUnit.Minute)]
    [TestCase(1, BarUnit.Hour)]
    [TestCase(4, BarUnit.Hour)]
    [TestCase(1, BarUnit.Day)]
    [Explicit("Calls the real OANDA market-data endpoint for multiple timeframes.")]
    public async Task Candles_AreReturnedForMultipleTimeframes(
        int intervalValue,
        BarUnit intervalUnit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var interval = new BarInterval(intervalValue, intervalUnit);

        IReadOnlyList<Candle> candles = await _broker.MarketData.GetCandlesAsync(
            new CandleQuery(_instrument, interval, Limit: CandleLimit),
            timeout.Token);

        Assert.That(candles, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(candles, Has.Count.LessThanOrEqualTo(CandleLimit));
            Assert.That(candles.All(candle => candle.Instrument == _instrument), Is.True);
            Assert.That(candles.All(candle => candle.Interval == interval), Is.True);
            Assert.That(candles.Select(candle => candle.OpenTime), Is.Ordered.Ascending);
            Assert.That(
                candles.Select(candle => candle.OpenTime).Distinct().Count(),
                Is.EqualTo(candles.Count));
            Assert.That(candles.All(HasConsistentPrices), Is.True);
            Assert.That(
                candles.All(candle =>
                    candle.Volume is { Kind: VolumeKind.TickCount, Value: >= 0m }),
                Is.True);
        });
    }

    [Test]
    [Explicit("Calls the real OANDA account-instrument endpoint.")]
    public async Task AccountInstruments_AreDiscoveredForWorkspaceSelection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<OandaInstrumentInfo> instruments =
            await _broker.GetInstrumentsAsync(timeout.Token);

        Assert.Multiple(() =>
        {
            Assert.That(instruments, Is.Not.Empty);
            Assert.That(instruments.Select(instrument => instrument.Name), Does.Contain("EUR_USD"));
            Assert.That(instruments.All(instrument => !string.IsNullOrWhiteSpace(instrument.DisplayName)),
                Is.True);
        });
    }

    [Test]
    [Explicit("Opens the real OANDA practice pricing stream and waits for one tradeable price.")]
    public async Task PracticePricingStream_ReturnsBidAndAsk()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        OandaPriceTick? received = null;
        await foreach (OandaPriceTick tick in _broker.StreamPricesAsync(
            ["FX:EUR/USD"],
            timeout.Token))
        {
            if (tick.IsTradeable)
            {
                received = tick;
                break;
            }
        }

        Assert.That(received, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(received!.Bid, Is.GreaterThan(0m));
            Assert.That(received.Ask, Is.GreaterThanOrEqualTo(received.Bid));
            Assert.That(received.Instrument, Is.EqualTo("EUR_USD"));
        });
    }

    private static bool HasConsistentPrices(Candle candle) =>
        candle.Prices.High >= candle.Prices.Open &&
        candle.Prices.High >= candle.Prices.Close &&
        candle.Prices.Low <= candle.Prices.Open &&
        candle.Prices.Low <= candle.Prices.Close;

    [Test]
    [Explicit("Calls OANDA read-only order and position endpoints.")]
    public async Task OpenOrdersAndPositions_CanBeReadEvenWhenEmpty()
    {
        _ = IntegrationTestEnvironment.Required("OANDA_ACCOUNT_ID");
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
