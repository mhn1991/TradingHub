using Brokers.Tests.TestDoubles;
using Networking;

namespace Brokers.Tests;

public sealed class BrokerFactoryTests
{
    [Test]
    public async Task CreateAsync_HydratesInstanceFromPersistenceRecordsAndCredentialReference()
    {
        var dispatcher = new StubNetworkDispatcher(_ => throw new AssertionException("No request expected."));
        var credentialStore = new StubCredentialStore(new BrokerCredentials { AccessToken = "token" });
        var factory = new BrokerFactory(dispatcher, credentialStore);
        var configuration = new BrokerConfiguration
        {
            Id = "oanda-account-7",
            Name = "OANDA account 7",
            Provider = BrokerProvider.OandaV20,
            Environment = BrokerEnvironment.Sandbox,
            AccountId = "account-7",
            CredentialReference = "vault:oanda-7",
            DefaultCandleLimit = 750,
            RequestTimeoutMilliseconds = 12_000
        };
        BrokerInstrumentConfiguration[] instruments =
        [
            new("FX:EUR-USD", "EUR_USD"),
            new("FX:GBP-JPY", "GBP_JPY")
        ];

        IBroker broker = await factory.CreateAsync(configuration, instruments);

        Assert.Multiple(() =>
        {
            Assert.That(broker.Id, Is.EqualTo("oanda-account-7"));
            Assert.That(broker.Provider, Is.EqualTo(BrokerProvider.OandaV20));
            Assert.That(broker.Environment, Is.EqualTo(BrokerEnvironment.Sandbox));
            Assert.That(broker.RestEndpoint, Is.EqualTo(new Uri("https://api-fxpractice.oanda.com/")));
            Assert.That(broker.StreamingEndpoint, Is.EqualTo(new Uri("https://stream-fxpractice.oanda.com/")));
            Assert.That(broker.CandlePriceBasis, Is.EqualTo(CandlePriceBasis.Midpoint));
            Assert.That(broker.Instruments, Has.Count.EqualTo(2));
            Assert.That(credentialStore.RequestedReference, Is.EqualTo("vault:oanda-7"));
        });
    }

    [Test]
    public void Create_OandaWithoutToken_IsRejectedBeforeUse()
    {
        var dispatcher = new StubNetworkDispatcher(_ => throw new AssertionException("No request expected."));
        var factory = new BrokerFactory(dispatcher);
        var configuration = new BrokerConfiguration
        {
            Id = "oanda",
            Name = "OANDA",
            Provider = BrokerProvider.OandaV20,
            Environment = BrokerEnvironment.Live,
            AccountId = "account"
        };

        BrokerConfigurationException? exception = Assert.Throws<BrokerConfigurationException>(() =>
            factory.Create(
                configuration,
                [new BrokerInstrumentConfiguration("FX:EUR-USD", "EUR_USD")]));

        Assert.That(exception!.Message, Does.Contain("access token"));
    }

    [Test]
    public void Create_DuplicateCanonicalInstrument_IsRejected()
    {
        var dispatcher = new StubNetworkDispatcher(_ => throw new AssertionException("No request expected."));
        var factory = new BrokerFactory(dispatcher);
        var configuration = new BrokerConfiguration
        {
            Id = "binance",
            Name = "Binance",
            Provider = BrokerProvider.BinanceSpot,
            Environment = BrokerEnvironment.Live
        };

        Assert.Throws<BrokerConfigurationException>(() => factory.Create(
            configuration,
            [
                new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BTCUSDT"),
                new BrokerInstrumentConfiguration("crypto:btc-usdt", "BTCUSDT")
            ]));
    }

    [Test]
    public void Create_RejectsProviderIncompatiblePriceBasis()
    {
        var dispatcher = new StubNetworkDispatcher(_ => throw new AssertionException("No request expected."));
        var factory = new BrokerFactory(dispatcher);
        var configuration = new BrokerConfiguration
        {
            Id = "binance",
            Name = "Binance",
            Provider = BrokerProvider.BinanceSpot,
            Environment = BrokerEnvironment.Live,
            CandlePriceBasis = CandlePriceBasis.Midpoint
        };

        Assert.Throws<BrokerConfigurationException>(() => factory.Create(
            configuration,
            [new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BTCUSDT")]));
    }
}
