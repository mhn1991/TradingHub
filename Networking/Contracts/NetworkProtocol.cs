namespace Networking;

/// <summary>
/// Identifies the transport that must be used for a network request.
/// </summary>
public enum NetworkProtocol
{
    Http = 1,
    Https = 2,
    WebSocket = 3,
    WebSocketSecure = 4,
    Fix = 5,
    FixTls = 6
}
