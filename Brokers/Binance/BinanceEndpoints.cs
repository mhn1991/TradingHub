using Brokers.Abstractions;

namespace Brokers.Binance;

internal static class BinanceEndpoints
{
    public static Uri GetRestBaseAddress(
        BrokerEnvironment environment)
    {
        return environment switch
        {
            BrokerEnvironment.Live =>
                new Uri("https://api.binance.com"),

            BrokerEnvironment.Demo =>
                new Uri("https://demo-api.binance.com"),

            BrokerEnvironment.Testnet =>
                new Uri("https://testnet.binance.vision"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(environment),
                environment,
                "Unsupported Binance environment.")
        };
    }

    public static Uri GetWebSocketApiAddress(
        BrokerEnvironment environment)
    {
        return environment switch
        {
            BrokerEnvironment.Live =>
                new Uri("wss://ws-api.binance.com/ws-api/v3"),

            BrokerEnvironment.Demo =>
                new Uri("wss://demo-ws-api.binance.com/ws-api/v3"),

            BrokerEnvironment.Testnet =>
                new Uri("wss://ws-api.testnet.binance.vision/ws-api/v3"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(environment),
                environment,
                "Unsupported Binance environment.")
        };
    }

    public static Uri GetMarketStreamAddress(
        BrokerEnvironment environment)
    {
        return environment switch
        {
            BrokerEnvironment.Live =>
                new Uri("wss://stream.binance.com:9443/ws"),

            BrokerEnvironment.Demo =>
                new Uri("wss://demo-stream.binance.com/ws"),

            BrokerEnvironment.Testnet =>
                new Uri("wss://stream.testnet.binance.vision/ws"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(environment),
                environment,
                "Unsupported Binance environment.")
        };
    }
}