namespace Networking;

/// <summary>
/// A long-lived, bidirectional protocol session.
/// </summary>
/// <typeparam name="TMessage">The complete message type emitted and accepted by the protocol client.</typeparam>
public interface INetworkSession<TMessage> : IAsyncDisposable
{
    string SessionId { get; }

    NetworkProtocol Protocol { get; }

    string RemoteEndpoint { get; }

    bool IsOpen { get; }

    /// <summary>
    /// Completes when the session ends, including when a background receive operation fails.
    /// </summary>
    Task Completion { get; }

    /// <summary>
    /// Sends one complete protocol message. Implementations must support concurrent callers safely.
    /// </summary>
    ValueTask SendAsync(TMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams complete inbound messages, including messages not correlated with an outbound request.
    /// A session supports one active reader unless its implementation explicitly documents otherwise.
    /// </summary>
    IAsyncEnumerable<TMessage> ReadAllAsync(CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
