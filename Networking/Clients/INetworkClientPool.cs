namespace Networking;

/// <summary>
/// Resolves a long-lived, concurrent client for a protocol.
/// </summary>
public interface INetworkClientPool : IAsyncDisposable
{
    INetworkProtocolClient GetClient(NetworkProtocol protocol);
}
