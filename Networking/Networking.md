# Networking

`Networking` is the protocol transport boundary used by broker adapters. Broker code creates a
protocol-specific request; `NetworkDispatcher` reads the protocol, resolves its long-lived client from
`NetworkClientPool`, awaits the operation, and returns the request's strongly typed response.

The Networking project intentionally does not reference the Brokers project. Brokers may reference
Networking and create these transport models without creating a circular dependency.

## Contract shape

Only `Protocol` is shared by `NetworkRequest<TResponse>` and `NetworkResponse`. Details remain in the
model that owns their meaning:

| Protocol | Initial operation | Result and ongoing traffic |
|---|---|---|
| HTTP/HTTPS | `HttpNetworkRequest` | `HttpNetworkResponse` with status, headers, and body |
| WebSocket/WSS | `WebSocketConnectRequest` | `WebSocketConnectResponse` containing `INetworkSession<WebSocketNetworkMessage>` |
| FIX/FIX-TLS | `FixConnectRequest` | `FixConnectResponse` containing `INetworkSession<FixNetworkMessage>` |

HTTP is request/response. WebSocket and FIX are persistent, bidirectional sessions, so they cannot be
represented honestly as one request followed by one response. `INetworkSession<TMessage>.ReadAllAsync`
delivers all inbound traffic, including execution reports, market data, heartbeats, and other messages
that were not initiated by a local request. `SendAsync` sends complete protocol messages independently.

The concrete HTTP client is implemented. WebSocket and FIX currently have protocol-specific models and
the session/client extension boundary; their concrete clients can be added independently and registered
in the same pool without changing the dispatcher or HTTP implementation.

## HTTP usage

```csharp
using System.Text;
using Networking;

await using NetworkDispatcher networking = NetworkDispatcher.CreateDefault();

var request = new HttpNetworkRequest(
    NetworkProtocol.Https,
    new Uri("https://broker.example/v1/orders"),
    HttpMethod.Post,
    headers:
    [
        new HttpNetworkHeader("Authorization", "Bearer <token>")
    ],
    body: Encoding.UTF8.GetBytes("{\"symbol\":\"EUR_USD\"}"),
    contentType: "application/json");

HttpNetworkResponse response = await networking.DispatchAsync(request, cancellationToken);

switch (response.Category)
{
    case HttpResponseCategory.Success:
        // Parse response.Body.
        break;
    case HttpResponseCategory.Redirect:
        // Inspect response.RedirectLocation. Redirects are deliberately not followed automatically.
        break;
    case HttpResponseCategory.ClientError:
    case HttpResponseCategory.ServerError:
        // Parse the broker error body and decide at the broker boundary whether a retry is safe.
        break;
}
```

HTTP 1xx, 2xx, 3xx, 4xx, 5xx, and non-standard statuses are returned as responses. Networking does not
call `EnsureSuccessStatusCode`, so broker error payloads are not discarded. Caller cancellation remains
`OperationCanceledException`. A Networking timeout throws `NetworkTimeoutException`; DNS, connection,
TLS, and incomplete-response failures throw `NetworkTransportException`.

## Stateful protocol usage

After a WebSocket or FIX client is registered in `NetworkClientPool`, the same dispatcher opens its session:

```csharp
WebSocketConnectResponse connection = await networking.DispatchAsync(
    new WebSocketConnectRequest(
        new Uri("wss://stream.broker.example/prices"),
        subProtocols: ["prices.v1"]),
    cancellationToken);

await foreach (WebSocketNetworkMessage message in
               connection.Session.ReadAllAsync(cancellationToken))
{
    // Route every inbound message, including unsolicited updates.
}
```

`FixNetworkMessage` represents one complete framed FIX packet, including its header, checksum, and SOH
delimiters. `FixConnectRequest` contains stream/TLS connection data; FIX logon contents, credentials,
sequence policy, and business packets remain explicit messages supplied by the broker integration.

## Pooling and lifetime

`NetworkClientPool` stores one concurrent client per protocol. One client may serve related protocols—for
example, the default HTTP client serves both HTTP and HTTPS. `HttpNetworkClient` uses one long-lived
`SocketsHttpHandler`, which owns the actual per-origin connection pools. Creating an `HttpClient` per
request would defeat connection reuse and can exhaust sockets.

The default HTTP client disables automatic redirects, reuses connections, applies async cancellation and
timeouts, bounds response bodies, uses `ArrayPool<byte>` while copying, and decompresses gzip, deflate,
and Brotli responses. `HttpNetworkClientOptions` controls its pool and buffer limits.
