namespace Networking;

/// <summary>
/// Immutable protocol-client pool. A client can serve multiple protocols and is disposed only once.
/// </summary>
public sealed class NetworkClientPool : INetworkClientPool
{
    private readonly IReadOnlyDictionary<NetworkProtocol, INetworkProtocolClient> _clientsByProtocol;
    private readonly IReadOnlyList<INetworkProtocolClient> _clients;
    private int _disposed;

    public NetworkClientPool(IEnumerable<INetworkProtocolClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);

        var clientsByProtocol = new Dictionary<NetworkProtocol, INetworkProtocolClient>();
        var uniqueClients = new HashSet<INetworkProtocolClient>(ReferenceEqualityComparer.Instance);

        foreach (INetworkProtocolClient client in clients)
        {
            ArgumentNullException.ThrowIfNull(client);

            if (client.SupportedProtocols.Count == 0)
            {
                throw new ArgumentException("Every pooled client must support at least one protocol.", nameof(clients));
            }

            uniqueClients.Add(client);

            foreach (NetworkProtocol protocol in client.SupportedProtocols)
            {
                if (!Enum.IsDefined(protocol))
                {
                    throw new ArgumentException($"Client registered unknown protocol value '{protocol}'.", nameof(clients));
                }

                if (!clientsByProtocol.TryAdd(protocol, client))
                {
                    throw new ArgumentException(
                        $"More than one client was registered for protocol '{protocol}'.",
                        nameof(clients));
                }
            }
        }

        if (clientsByProtocol.Count == 0)
        {
            throw new ArgumentException("At least one protocol client must be registered.", nameof(clients));
        }

        _clientsByProtocol = clientsByProtocol;
        _clients = uniqueClients.ToArray();
    }

    public INetworkProtocolClient GetClient(NetworkProtocol protocol)
    {
        ThrowIfDisposed();

        if (_clientsByProtocol.TryGetValue(protocol, out INetworkProtocolClient? client))
        {
            return client;
        }

        throw new NotSupportedException($"No network client is registered for protocol '{protocol}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (INetworkProtocolClient client in _clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(NetworkClientPool));
        }
    }
}
