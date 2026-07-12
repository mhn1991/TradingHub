using Networking.Abstractions;
using Networking.Http;
using Networking.Routing;

namespace Brokers.Infrastructure;

internal sealed class BrokerHttpRuntime : IAsyncDisposable
{
    private readonly HttpTransport _transport;

    public BrokerHttpRuntime(
        TransportId transportId,
        HttpClient httpClient,
        TimeSpan? defaultTimeout = null)
    {
        _transport = new HttpTransport(
            transportId,
            httpClient,
            defaultTimeout,
            ownsHttpClient: true);

        Gateway = new NetworkGateway([_transport]);
    }

    public INetworkGateway Gateway { get; }

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
