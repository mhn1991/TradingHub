using System.Collections.ObjectModel;
using System.Net;

namespace Networking;

/// <summary>
/// Immutable HTTP or HTTPS request produced by a caller, such as a broker adapter.
/// </summary>
public sealed class HttpNetworkRequest : NetworkRequest<HttpNetworkResponse>
{
    private readonly byte[]? _body;

    public HttpNetworkRequest(
        NetworkProtocol protocol,
        Uri endpoint,
        HttpMethod method,
        IEnumerable<HttpNetworkHeader>? headers = null,
        ReadOnlyMemory<byte>? body = null,
        string? contentType = null,
        TimeSpan? timeout = null,
        Version? httpVersion = null,
        HttpVersionPolicy versionPolicy = HttpVersionPolicy.RequestVersionOrLower)
        : base(protocol)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(method);

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The endpoint must be an absolute URI.", nameof(endpoint));
        }

        ValidateProtocolAndScheme(protocol, endpoint);
        ValidateTimeout(timeout);

        if (contentType is not null &&
            (contentType.Contains('\r', StringComparison.Ordinal) ||
             contentType.Contains('\n', StringComparison.Ordinal)))
        {
            throw new ArgumentException("The content type cannot contain a carriage return or line feed.", nameof(contentType));
        }

        Endpoint = endpoint;
        Method = method;
        Headers = new ReadOnlyCollection<HttpNetworkHeader>((headers ?? []).ToArray());
        _body = body?.ToArray();
        ContentType = contentType;
        Timeout = timeout;
        HttpVersion = httpVersion ?? System.Net.HttpVersion.Version20;
        VersionPolicy = versionPolicy;
    }

    public Uri Endpoint { get; }

    public HttpMethod Method { get; }

    public IReadOnlyList<HttpNetworkHeader> Headers { get; }

    public bool HasBody => _body is not null;

    public ReadOnlyMemory<byte> Body => _body ?? ReadOnlyMemory<byte>.Empty;

    public string? ContentType { get; }

    /// <summary>
    /// Overrides the client's default timeout. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// to disable the Networking timeout for this request.
    /// </summary>
    public TimeSpan? Timeout { get; }

    public Version HttpVersion { get; }

    public HttpVersionPolicy VersionPolicy { get; }

    public static NetworkProtocol GetProtocol(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Scheme.ToLowerInvariant() switch
        {
            "http" => NetworkProtocol.Http,
            "https" => NetworkProtocol.Https,
            _ => throw new NotSupportedException($"URI scheme '{endpoint.Scheme}' is not supported by HTTP.")
        };
    }

    private static void ValidateProtocolAndScheme(NetworkProtocol protocol, Uri endpoint)
    {
        string expectedScheme = protocol switch
        {
            NetworkProtocol.Http => Uri.UriSchemeHttp,
            NetworkProtocol.Https => Uri.UriSchemeHttps,
            _ => throw new ArgumentOutOfRangeException(
                nameof(protocol),
                protocol,
                "An HTTP request requires the HTTP or HTTPS protocol.")
        };

        if (!string.Equals(endpoint.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Protocol '{protocol}' requires an endpoint using the '{expectedScheme}' scheme.",
                nameof(endpoint));
        }
    }

    private static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && value != System.Threading.Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive or infinite.");
        }
    }
}
