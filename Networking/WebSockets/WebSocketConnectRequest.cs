using System.Collections.ObjectModel;

namespace Networking;

/// <summary>
/// Opens a persistent WebSocket session. Messages are sent and received through the returned session.
/// </summary>
public sealed class WebSocketConnectRequest : NetworkRequest<WebSocketConnectResponse>
{
    public WebSocketConnectRequest(
        Uri endpoint,
        IEnumerable<HttpNetworkHeader>? handshakeHeaders = null,
        IEnumerable<string>? subProtocols = null,
        TimeSpan? connectTimeout = null)
        : base(GetProtocol(endpoint))
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The endpoint must be an absolute URI.", nameof(endpoint));
        }

        ValidateTimeout(connectTimeout);

        string[] protocolCopy = (subProtocols ?? []).ToArray();
        ValidateSubProtocols(protocolCopy);

        Endpoint = endpoint;
        HandshakeHeaders = new ReadOnlyCollection<HttpNetworkHeader>((handshakeHeaders ?? []).ToArray());
        SubProtocols = new ReadOnlyCollection<string>(protocolCopy);
        ConnectTimeout = connectTimeout;
    }

    public Uri Endpoint { get; }

    /// <summary>
    /// Additional headers for the HTTP upgrade handshake. Restricted WebSocket headers remain client-owned.
    /// </summary>
    public IReadOnlyList<HttpNetworkHeader> HandshakeHeaders { get; }

    public IReadOnlyList<string> SubProtocols { get; }

    public TimeSpan? ConnectTimeout { get; }

    public static NetworkProtocol GetProtocol(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Scheme.ToLowerInvariant() switch
        {
            "ws" => NetworkProtocol.WebSocket,
            "wss" => NetworkProtocol.WebSocketSecure,
            _ => throw new NotSupportedException(
                $"URI scheme '{endpoint.Scheme}' is not supported by WebSocket.")
        };
    }

    private static void ValidateSubProtocols(IEnumerable<string> subProtocols)
    {
        var uniqueProtocols = new HashSet<string>(StringComparer.Ordinal);

        foreach (string subProtocol in subProtocols)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subProtocol);

            if (!subProtocol.All(HttpNetworkHeader.IsTokenCharacter))
            {
                throw new ArgumentException(
                    $"WebSocket subprotocol '{subProtocol}' contains an invalid character.",
                    nameof(subProtocols));
            }

            if (!uniqueProtocols.Add(subProtocol))
            {
                throw new ArgumentException(
                    $"WebSocket subprotocol '{subProtocol}' was specified more than once.",
                    nameof(subProtocols));
            }
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
