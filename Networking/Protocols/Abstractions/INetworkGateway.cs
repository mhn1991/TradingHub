namespace Networking.Abstractions;

public interface INetworkGateway
{
    Task<TResponse> SendAsync<TResponse>(
        INetworkCommand<TResponse> command,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        INetworkSubscription<TEvent> subscription,
        CancellationToken cancellationToken = default);
}
