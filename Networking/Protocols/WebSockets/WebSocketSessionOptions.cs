using System.Net.WebSockets;

namespace Networking.WebSockets;

public sealed class WebSocketSessionOptions
{
    public required Uri Uri { get; init; }

    public int OutboundQueueCapacity { get; init; } = 1_024;

    public int InboundQueueCapacity { get; init; } = 4_096;

    public int ReceiveChunkSize { get; init; } = 16 * 1_024;

    public int MaximumInboundMessageBytes { get; init; } = 4 * 1_024 * 1_024;

    public int MaximumOutboundMessageBytes { get; init; } = 4 * 1_024 * 1_024;

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public WebSocketMessageType OutboundMessageType { get; init; } =
        WebSocketMessageType.Text;

    /// <summary>
    /// Adds broker-specific headers, cookies, certificates, or subprotocols before connecting.
    /// </summary>
    public Action<ClientWebSocketOptions>? ConfigureClient { get; init; }
}
