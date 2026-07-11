namespace Networking;

/// <summary>
/// Dispatches packets for one or more registered protocols.
/// Implementations must be safe for concurrent use.
/// </summary>
public interface INetworkProtocolClient : IAsyncDisposable
{
    IReadOnlyCollection<NetworkProtocol> SupportedProtocols { get; }

    Task<NetworkResponse> DispatchAsync(
        NetworkRequest request,
        CancellationToken cancellationToken = default);
}
