namespace Networking;

/// <summary>
/// HTTP connection-pool, timeout, and response-buffer limits.
/// </summary>
public sealed class HttpNetworkClientOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxConnectionsPerServer { get; init; } = 32;

    public int MaxResponseBodyBytes { get; init; } = 16 * 1024 * 1024;

    internal void Validate()
    {
        ValidatePositiveOrInfinite(RequestTimeout, nameof(RequestTimeout));
        ValidatePositiveOrInfinite(ConnectTimeout, nameof(ConnectTimeout));
        ValidateNonNegativeOrInfinite(PooledConnectionLifetime, nameof(PooledConnectionLifetime));
        ValidateNonNegativeOrInfinite(PooledConnectionIdleTimeout, nameof(PooledConnectionIdleTimeout));

        if (MaxConnectionsPerServer <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConnectionsPerServer), "The value must be positive.");
        }

        if (MaxResponseBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBodyBytes), "The value must be positive.");
        }
    }

    private static void ValidatePositiveOrInfinite(TimeSpan value, string parameterName)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be positive or infinite.");
        }
    }

    private static void ValidateNonNegativeOrInfinite(TimeSpan value, string parameterName)
    {
        if (value != Timeout.InfiniteTimeSpan && value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be non-negative or infinite.");
        }
    }
}
