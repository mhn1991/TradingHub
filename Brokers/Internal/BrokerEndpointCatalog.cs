namespace Brokers;

internal static class BrokerEndpointCatalog
{
    public static Uri GetRestEndpoint(BrokerConfiguration configuration)
    {
        string endpoint = configuration.RestBaseUrl ?? (configuration.Provider, configuration.Environment) switch
        {
            (BrokerProvider.BinanceSpot, BrokerEnvironment.Sandbox) => "https://testnet.binance.vision/",
            (BrokerProvider.BinanceSpot, BrokerEnvironment.Live) => "https://api.binance.com/",
            (BrokerProvider.OandaV20, BrokerEnvironment.Sandbox) => "https://api-fxpractice.oanda.com/",
            (BrokerProvider.OandaV20, BrokerEnvironment.Live) => "https://api-fxtrade.oanda.com/",
            _ => throw new BrokerConfigurationException("The broker provider/environment combination is unsupported.")
        };

        Uri restEndpoint = ValidateEndpoint(endpoint, "REST", Uri.UriSchemeHttps);
        if (restEndpoint.AbsolutePath != "/")
        {
            throw new BrokerConfigurationException("The REST endpoint must be a base origin without an API path.");
        }

        return restEndpoint;
    }

    public static Uri? GetStreamingEndpoint(BrokerConfiguration configuration)
    {
        string? endpoint = configuration.StreamingBaseUrl ?? (configuration.Provider, configuration.Environment) switch
        {
            (BrokerProvider.BinanceSpot, BrokerEnvironment.Sandbox) =>
                "wss://stream.testnet.binance.vision/ws",
            (BrokerProvider.BinanceSpot, BrokerEnvironment.Live) =>
                "wss://stream.binance.com:9443/ws",
            (BrokerProvider.OandaV20, BrokerEnvironment.Sandbox) =>
                "https://stream-fxpractice.oanda.com/",
            (BrokerProvider.OandaV20, BrokerEnvironment.Live) =>
                "https://stream-fxtrade.oanda.com/",
            _ => null
        };

        return endpoint is null
            ? null
            : ValidateEndpoint(endpoint, "streaming", Uri.UriSchemeHttps, "wss");
    }

    private static Uri ValidateEndpoint(string value, string endpointName, params string[] schemes)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            !schemes.Contains(endpoint.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new BrokerConfigurationException(
                $"The {endpointName} endpoint must be an absolute {string.Join("/", schemes)} URL.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new BrokerConfigurationException(
                $"The {endpointName} endpoint cannot contain credentials, a query, or a fragment.");
        }

        return endpoint;
    }
}
