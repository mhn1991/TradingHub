namespace Networking.Abstractions;

public interface IRequestTransport
{
    TransportId Id { get; }

    TransportKind Kind { get; }

    Task<TResponse> SendAsync<TResponse>(
        INetworkCommand<TResponse> command,
        CancellationToken cancellationToken = default);
}
