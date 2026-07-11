using System.Net;

namespace Networking.Tests;

public sealed class NetworkDispatcherTests
{
    [Test]
    public async Task DispatchAsync_SelectsClientFromRequestProtocol()
    {
        var httpClient = new RecordingProtocolClient(NetworkProtocol.Http);
        var httpsClient = new RecordingProtocolClient(NetworkProtocol.Https);
        await using var pool = new NetworkClientPool([httpClient, httpsClient]);
        await using var dispatcher = new NetworkDispatcher(pool);
        var request = new HttpNetworkRequest(
            NetworkProtocol.Https,
            new Uri("https://broker.example/status"),
            HttpMethod.Get);

        HttpNetworkResponse response = await dispatcher.DispatchAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.Protocol, Is.EqualTo(NetworkProtocol.Https));
            Assert.That(httpClient.SendCount, Is.Zero);
            Assert.That(httpsClient.SendCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Pool_ReusesClientAndDisposesSharedClientOnce()
    {
        var client = new RecordingProtocolClient(NetworkProtocol.Http, NetworkProtocol.Https);
        var pool = new NetworkClientPool([client]);

        INetworkProtocolClient first = pool.GetClient(NetworkProtocol.Http);
        INetworkProtocolClient second = pool.GetClient(NetworkProtocol.Https);
        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That(client.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DispatchAsync_ReturnsProtocolSpecificSessionResponse()
    {
        var session = new StubWebSocketSession();
        var client = new WebSocketProtocolClient(session);
        await using var pool = new NetworkClientPool([client]);
        await using var dispatcher = new NetworkDispatcher(pool);
        var request = new WebSocketConnectRequest(new Uri("wss://stream.broker.example/prices"));

        WebSocketConnectResponse response = await dispatcher.DispatchAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(response.Protocol, Is.EqualTo(NetworkProtocol.WebSocketSecure));
            Assert.That(response.Session, Is.SameAs(session));
        });
    }

    private sealed class RecordingProtocolClient(params NetworkProtocol[] protocols) : INetworkProtocolClient
    {
        public IReadOnlyCollection<NetworkProtocol> SupportedProtocols { get; } = Array.AsReadOnly(protocols);

        public int SendCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task<NetworkResponse> DispatchAsync(
            NetworkRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;

            if (request is not HttpNetworkRequest httpRequest)
            {
                throw new ArgumentException("This test client only handles HTTP requests.", nameof(request));
            }

            return Task.FromResult<NetworkResponse>(new HttpNetworkResponse(
                httpRequest.Protocol,
                httpRequest.Endpoint,
                HttpStatusCode.OK));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WebSocketProtocolClient(StubWebSocketSession session) : INetworkProtocolClient
    {
        public IReadOnlyCollection<NetworkProtocol> SupportedProtocols { get; } =
            Array.AsReadOnly(new[] { NetworkProtocol.WebSocketSecure });

        public Task<NetworkResponse> DispatchAsync(
            NetworkRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request is not WebSocketConnectRequest)
            {
                throw new ArgumentException("A WebSocket connect request was expected.", nameof(request));
            }

            return Task.FromResult<NetworkResponse>(new WebSocketConnectResponse(session));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubWebSocketSession : INetworkSession<WebSocketNetworkMessage>
    {
        public string SessionId { get; } = "session-1";

        public NetworkProtocol Protocol => NetworkProtocol.WebSocketSecure;

        public string RemoteEndpoint => "wss://stream.broker.example/prices";

        public bool IsOpen => true;

        public Task Completion { get; } = new TaskCompletionSource().Task;

        public ValueTask SendAsync(
            WebSocketNetworkMessage message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<WebSocketNetworkMessage> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
