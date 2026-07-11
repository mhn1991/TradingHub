using System.Net.WebSockets;
using System.Text;

namespace Networking;

/// <summary>
/// One complete WebSocket message. Fragmentation and reassembly are owned by the protocol client.
/// </summary>
public sealed class WebSocketNetworkMessage
{
    private readonly byte[] _payload;

    public WebSocketNetworkMessage(
        WebSocketMessageType messageType,
        ReadOnlyMemory<byte> payload = default,
        WebSocketCloseStatus? closeStatus = null,
        string? closeDescription = null)
    {
        if (messageType == WebSocketMessageType.Close && closeStatus is null)
        {
            throw new ArgumentException("A close message must include its close status.", nameof(closeStatus));
        }

        if (messageType != WebSocketMessageType.Close &&
            (closeStatus is not null || closeDescription is not null))
        {
            throw new ArgumentException("Close details can only be set on a close message.", nameof(closeStatus));
        }

        MessageType = messageType;
        _payload = payload.ToArray();
        CloseStatus = closeStatus;
        CloseDescription = closeDescription;
    }

    public WebSocketMessageType MessageType { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    public WebSocketCloseStatus? CloseStatus { get; }

    public string? CloseDescription { get; }

    public static WebSocketNetworkMessage Text(string value, Encoding? encoding = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new WebSocketNetworkMessage(
            WebSocketMessageType.Text,
            (encoding ?? Encoding.UTF8).GetBytes(value));
    }

    public static WebSocketNetworkMessage Binary(ReadOnlyMemory<byte> value) =>
        new(WebSocketMessageType.Binary, value);

    public string GetText(Encoding? encoding = null)
    {
        if (MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidOperationException("Only a text WebSocket message can be decoded as text.");
        }

        return (encoding ?? Encoding.UTF8).GetString(_payload);
    }
}
