using Networking.Abstractions;
using Networking.Exceptions;

namespace Networking.Routing;

/// <summary>
/// Routes commands and subscriptions to configured transports.
/// The route lookup is created once and is safe for concurrent reads.
/// </summary>
public sealed class NetworkGateway : INetworkGateway
{
    private readonly IReadOnlyDictionary<TransportId, IRequestTransport> _requestTransports;
    private readonly IReadOnlyDictionary<TransportId, IStreamingTransport> _streamingTransports;

    public NetworkGateway(
        IEnumerable<IRequestTransport> requestTransports,
        IEnumerable<IStreamingTransport>? streamingTransports = null)
    {
        ArgumentNullException.ThrowIfNull(requestTransports);

        _requestTransports = BuildMap(requestTransports);
        _streamingTransports = BuildMap(streamingTransports ?? []);
    }

    public Task<TResponse> SendAsync<TResponse>(
        INetworkCommand<TResponse> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_requestTransports.TryGetValue(command.TransportId, out IRequestTransport? transport))
        {
            throw new NetworkTransportNotFoundException(command.TransportId, streaming: false);
        }

        return transport.SendAsync(command, cancellationToken);
    }

    public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        INetworkSubscription<TEvent> subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (!_streamingTransports.TryGetValue(
                subscription.TransportId,
                out IStreamingTransport? transport))
        {
            throw new NetworkTransportNotFoundException(subscription.TransportId, streaming: true);
        }

        return transport.SubscribeAsync(subscription, cancellationToken);
    }

    private static IReadOnlyDictionary<TransportId, TTransport> BuildMap<TTransport>(
        IEnumerable<TTransport> transports)
        where TTransport : class
    {
        var result = new Dictionary<TransportId, TTransport>();

        foreach (TTransport transport in transports)
        {
            TransportId id = transport switch
            {
                IRequestTransport requestTransport => requestTransport.Id,
                IStreamingTransport streamingTransport => streamingTransport.Id,
                _ => throw new ArgumentException(
                    $"{typeof(TTransport).Name} is not a recognised transport type.",
                    nameof(transports))
            };

            if (!result.TryAdd(id, transport))
            {
                throw new InvalidOperationException(
                    $"More than one {typeof(TTransport).Name} is registered with ID '{id}'.");
            }
        }

        return result;
    }
}
