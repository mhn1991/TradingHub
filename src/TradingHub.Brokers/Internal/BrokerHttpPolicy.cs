using System.Net;

namespace TradingHub.Brokers.Internal;

internal static class BrokerHttpPolicy
{
    public static void EnsureExactEndpoint(
        HttpClient httpClient,
        Uri requiredEndpoint,
        string providerName)
    {
        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = requiredEndpoint;
            return;
        }

        if (httpClient.BaseAddress != requiredEndpoint)
        {
            throw new InvalidOperationException(
                $"The {providerName} HttpClient endpoint must be exactly '{requiredEndpoint}'.");
        }
    }

    public static bool IsAmbiguousOrderStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }
}
