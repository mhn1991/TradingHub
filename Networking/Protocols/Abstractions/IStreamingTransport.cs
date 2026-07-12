namespace Networking.Abstractions;

public interface IStreamingTransport
{
    TransportId Id { get; }

    TransportKind Kind { get; }

    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        INetworkSubscription<TEvent> subscription,
        CancellationToken cancellationToken = default);
}
