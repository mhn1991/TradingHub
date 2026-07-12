using System.Net;

namespace Networking.Http;

public sealed class PooledHttpClientOptions
{
    public Uri? BaseAddress { get; init; }

    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxConnectionsPerServer { get; init; } = 32;

    public DecompressionMethods AutomaticDecompression { get; init; } =
        DecompressionMethods.GZip |
        DecompressionMethods.Deflate |
        DecompressionMethods.Brotli;

    public bool UseCookies { get; init; }

    public Version DefaultRequestVersion { get; init; } = HttpVersion.Version20;

    public HttpVersionPolicy DefaultVersionPolicy { get; init; } =
        HttpVersionPolicy.RequestVersionOrLower;

    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }
}
