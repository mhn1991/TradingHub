using System.Net;
using Networking.Duplex;
using Networking.WebSockets;

namespace Dashboard.Live;

internal interface ILiveWebSocketFactory
{
    IDuplexSession Create(Uri uri);
}

internal sealed class LiveWebSocketFactory : ILiveWebSocketFactory
{
    public IDuplexSession Create(Uri uri) => new WebSocketDuplexSession(
        new WebSocketSessionOptions
        {
            Uri = uri,
            OutboundQueueCapacity = 4,
            InboundQueueCapacity = 128,
            ReceiveChunkSize = 8 * 1_024,
            MaximumInboundMessageBytes = 128 * 1_024,
            MaximumOutboundMessageBytes = 16 * 1_024,
            KeepAliveInterval = TimeSpan.FromSeconds(15),
            CloseTimeout = TimeSpan.FromSeconds(5)
        });
}

internal static class ReconnectDelay
{
    public static TimeSpan Calculate(
        int attempt,
        TimeSpan minimum,
        TimeSpan maximum,
        double jitterSample)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (minimum <= TimeSpan.Zero || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        if (jitterSample is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSample));
        }

        double exponent = Math.Min(attempt - 1, 20);
        double baseMilliseconds = Math.Min(
            maximum.TotalMilliseconds,
            minimum.TotalMilliseconds * Math.Pow(2d, exponent));
        double jitterFactor = 0.8d + jitterSample * 0.4d;
        return TimeSpan.FromMilliseconds(Math.Min(
            maximum.TotalMilliseconds,
            baseMilliseconds * jitterFactor));
    }

    public static TimeSpan RespectRetryAfter(TimeSpan calculated, TimeSpan? retryAfter)
    {
        if (calculated <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(calculated));
        }

        return retryAfter is TimeSpan requested && requested > calculated
            ? requested
            : calculated;
    }
}

internal sealed class BinanceRateLimitException(
    HttpStatusCode statusCode,
    TimeSpan? retryAfter) : HttpRequestException(
        "Binance rate-limited the public market-data warm-up request.",
        null,
        statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
