using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingHub.Abstractions.Time;
using TradingHub.Brokers.Configuration;
using TradingHub.Brokers.Factory;
using TradingHub.Brokers.Binance;
using TradingHub.Brokers.Oanda;

namespace Microsoft.Extensions.DependencyInjection;

public static class BrokerServiceCollectionExtensions
{
    public static IServiceCollection AddTradingHubBrokers(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        services.Configure<BrokerCatalogOptions>(configurationSection);
        services.AddHttpClient();
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IBrokerCredentialStore, EnvironmentBrokerCredentialStore>();
        services.AddSingleton<IBrokerProvider, OandaBrokerProvider>();
        services.AddSingleton<IBrokerProvider, BinanceBrokerProvider>();
        services.AddSingleton<IBrokerFactory, BrokerFactory>();
        return services;
    }
}
