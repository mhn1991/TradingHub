namespace Networking;

/// <summary>
/// Selects a pooled client from the packet protocol, dispatches it asynchronously, and returns the response.
/// </summary>
public sealed class NetworkDispatcher : INetworkDispatcher, IAsyncDisposable
{
    private readonly INetworkClientPool _clientPool;
    private readonly bool _disposeClientPool;
    private int _disposed;

    public NetworkDispatcher(INetworkClientPool clientPool, bool disposeClientPool = false)
    {
        ArgumentNullException.ThrowIfNull(clientPool);
        _clientPool = clientPool;
        _disposeClientPool = disposeClientPool;
    }

    public static NetworkDispatcher CreateDefault(HttpNetworkClientOptions? options = null)
    {
        var httpClient = new HttpNetworkClient(options);
        var clientPool = new NetworkClientPool([httpClient]);
        return new NetworkDispatcher(clientPool, disposeClientPool: true);
    }

    public async Task<TResponse> DispatchAsync<TResponse>(
        NetworkRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : NetworkResponse
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        INetworkProtocolClient client = _clientPool.GetClient(request.Protocol);
        NetworkResponse response = await client.DispatchAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Protocol != request.Protocol)
        {
            throw new InvalidOperationException(
                $"The '{request.Protocol}' client returned a response for protocol '{response.Protocol}'.");
        }

        if (response is not TResponse typedResponse)
        {
            throw new InvalidOperationException(
                $"The '{request.Protocol}' client returned '{response.GetType().Name}' instead of " +
                $"the expected '{typeof(TResponse).Name}'.");
        }

        return typedResponse;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_disposeClientPool)
        {
            return;
        }

        await _clientPool.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(NetworkDispatcher));
        }
    }
}
