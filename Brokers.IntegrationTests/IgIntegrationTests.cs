using Brokers;
using Brokers.Abstractions;
using Brokers.Ig;
using Brokers.Models;
using NUnit.Framework;

namespace Brokers.IntegrationTests;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class IgIntegrationTests
{
    private IBrokerClient _broker = null!;
    private InstrumentKey _instrument;
    private string? _epic;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var credential = IntegrationTestEnvironment.RequiredCredential("IG", "DEMO");
        _instrument = new InstrumentKey(
            IntegrationTestEnvironment.Optional("IG_TEST_INSTRUMENT") ?? "FX:GBP/USD");
        _epic = IntegrationTestEnvironment.Optional("IG_TEST_EPIC");

        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_epic))
        {
            mappings[_instrument.Value] = _epic;
        }

        _broker = BrokerClientFactory.CreateIg(new IgOptions
        {
            Environment = IntegrationTestEnvironment.ParseStoredBrokerEnvironment(credential.Environment),
            ApiKey = IntegrationTestEnvironment.RequiredCredentialValue(credential.ApiKey, "API key"),
            Identifier = IntegrationTestEnvironment.RequiredCredentialValue(credential.Identifier, "identifier"),
            Password = IntegrationTestEnvironment.RequiredCredentialValue(credential.Password, "password"),
            AccountId = credential.AccountId,
            BaseAddress = IntegrationTestEnvironment.CredentialUri(credential.BaseAddress, "base address"),
            InstrumentMappings = mappings
        });
    }

    [Test]
    [Explicit("Logs into the real IG API and reads account details.")]
    public async Task Accounts_LoginAndReuseSessionTokens()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<AccountSnapshot> firstCall =
            await _broker.Accounts.GetAccountsAsync(timeout.Token);
        IReadOnlyList<AccountSnapshot> secondCall =
            await _broker.Accounts.GetAccountsAsync(timeout.Token);

        Assert.That(firstCall, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(firstCall[0].AccountId, Is.Not.Empty);
            Assert.That(secondCall, Has.Count.EqualTo(firstCall.Count));
        });
    }

    [Test]
    [Explicit("Calls the real IG historical-price endpoint. Set IG_TEST_EPIC first.")]
    public async Task FiveMinutePrices_AreReturnedAsStandardCandles()
    {
        if (string.IsNullOrWhiteSpace(_epic))
        {
            Assert.Ignore("Set IG_TEST_EPIC to an EPIC available on your IG account.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        IReadOnlyList<Candle> candles = await _broker.MarketData.GetCandlesAsync(
            new CandleQuery(_instrument, BarInterval.Minutes(5), Limit: 20),
            timeout.Token);

        Assert.That(candles, Is.Not.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(candles, Has.Count.LessThanOrEqualTo(20));
            Assert.That(candles.All(candle => candle.Prices.High >= candle.Prices.Low), Is.True);
        });
    }

    [Test]
    [Explicit("Calls IG read-only working-order and position endpoints.")]
    public async Task WorkingOrdersAndPositions_CanBeReadEvenWhenEmpty()
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
