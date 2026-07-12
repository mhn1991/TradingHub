namespace Networking.Http;

/// <summary>
/// Creates a long-lived HttpClient backed by SocketsHttpHandler's connection pool.
/// The caller should reuse and dispose the returned client when the application stops.
/// </summary>
public static class PooledHttpClient
{
    public static HttpClient Create(PooledHttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxConnectionsPerServer <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxConnectionsPerServer must be greater than zero.");
        }

        ValidateTimeout(options.PooledConnectionLifetime, nameof(options.PooledConnectionLifetime));
        ValidateTimeout(options.PooledConnectionIdleTimeout, nameof(options.PooledConnectionIdleTimeout));
        ValidateTimeout(options.ConnectTimeout, nameof(options.ConnectTimeout));

        if (options.BaseAddress is { IsAbsoluteUri: false })
        {
            throw new ArgumentException("BaseAddress must be absolute.", nameof(options));
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = options.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            AutomaticDecompression = options.AutomaticDecompression,
            UseCookies = options.UseCookies,
            EnableMultipleHttp2Connections = true
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = options.DefaultRequestVersion,
            DefaultVersionPolicy = options.DefaultVersionPolicy
        };

        if (options.DefaultHeaders is not null)
        {
            foreach ((string name, string value) in options.DefaultHeaders)
            {
                if (!client.DefaultRequestHeaders.TryAddWithoutValidation(name, value))
                {
                    client.Dispose();
                    throw new ArgumentException(
                        $"The default HTTP header '{name}' is invalid.",
                        nameof(options));
                }
            }
        }

        return client;
    }

    private static void ValidateTimeout(TimeSpan value, string propertyName)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                "Timeout values must be positive or Timeout.InfiniteTimeSpan.");
        }
    }
}
