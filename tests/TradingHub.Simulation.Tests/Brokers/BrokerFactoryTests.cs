using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingHub.Brokers.Configuration;
using TradingHub.Brokers.Factory;
using TradingHub.Domain.Trading;
using TradingHub.Simulation.Tests.TestDoubles;

namespace TradingHub.Simulation.Tests.Brokers;

[TestFixture]
public sealed class BrokerFactoryTests
{
    [Test]
    public async Task CreateAsync_CreatesDifferentProvidersFromConfiguration()
    {
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton<IBrokerCredentialStore>(CreateCredentialStore());
        services.AddTradingHubBrokers(configuration.GetSection("TradingHub:Brokers"));
        await using var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IBrokerFactory>();

        var oanda = await factory.CreateAsync("oanda-demo");
        var sameOanda = await factory.CreateAsync("oanda-demo");
        var binance = await factory.CreateAsync("binance-testnet");

        Assert.Multiple(() =>
        {
            Assert.That(factory.ConfiguredBrokerIds, Is.EquivalentTo(new[] { "oanda-demo", "binance-testnet" }));
            Assert.That(oanda.BrokerId, Is.EqualTo("oanda-demo"));
            Assert.That(sameOanda, Is.SameAs(oanda));
            Assert.That(oanda.ProviderName, Is.EqualTo("Oanda"));
            Assert.That(oanda.Environment, Is.EqualTo(TradingEnvironment.Demo));
            Assert.That(binance.BrokerId, Is.EqualTo("binance-testnet"));
            Assert.That(binance.ProviderName, Is.EqualTo("Binance"));
            Assert.That(binance.Environment, Is.EqualTo(TradingEnvironment.Demo));
        });
    }

    private static IConfigurationRoot CreateConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["TradingHub:Brokers:Items:0:Id"] = "oanda-demo",
            ["TradingHub:Brokers:Items:0:Provider"] = "Oanda",
            ["TradingHub:Brokers:Items:0:Environment"] = "Demo",
            ["TradingHub:Brokers:Items:0:Account:AccountId"] = "OANDA-PRACTICE",
            ["TradingHub:Brokers:Items:0:Account:CredentialKey"] = "OANDA_DEMO",
            ["TradingHub:Brokers:Items:0:Instruments:0:InstrumentId"] = "FX:GBP-JPY",
            ["TradingHub:Brokers:Items:0:Instruments:0:BrokerSymbol"] = "GBP_JPY",
            ["TradingHub:Brokers:Items:1:Id"] = "binance-testnet",
            ["TradingHub:Brokers:Items:1:Provider"] = "Binance",
            ["TradingHub:Brokers:Items:1:Environment"] = "Demo",
            ["TradingHub:Brokers:Items:1:Account:AccountId"] = "BINANCE-TESTNET",
            ["TradingHub:Brokers:Items:1:Account:CredentialKey"] = "BINANCE_TESTNET",
            ["TradingHub:Brokers:Items:1:Instruments:0:InstrumentId"] = "CRYPTO:BTC-USDT:SPOT",
            ["TradingHub:Brokers:Items:1:Instruments:0:BrokerSymbol"] = "BTCUSDT"
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static InMemoryBrokerCredentialStore CreateCredentialStore()
    {
        return new InMemoryBrokerCredentialStore(new Dictionary<string, BrokerCredentialSet>
        {
            ["OANDA_DEMO"] = new() { AccessToken = "not-a-real-token" },
            ["BINANCE_TESTNET"] = new()
            {
                ApiKey = "not-a-real-key",
                SecretKey = "not-a-real-secret"
            }
        });
    }
}
