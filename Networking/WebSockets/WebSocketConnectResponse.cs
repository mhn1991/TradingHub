namespace Networking;

/// <summary>
/// A successfully opened WebSocket session.
/// </summary>
public sealed class WebSocketConnectResponse : NetworkResponse
{
    public WebSocketConnectResponse(
        INetworkSession<WebSocketNetworkMessage> session,
        string? negotiatedSubProtocol = null)
        : base(GetProtocol(session))
    {
        Session = session;
        NegotiatedSubProtocol = negotiatedSubProtocol;
    }

    public INetworkSession<WebSocketNetworkMessage> Session { get; }

    public string? NegotiatedSubProtocol { get; }

    private static NetworkProtocol GetProtocol(INetworkSession<WebSocketNetworkMessage> session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Protocol is not NetworkProtocol.WebSocket and not NetworkProtocol.WebSocketSecure)
        {
            throw new ArgumentException("The session does not use a WebSocket protocol.", nameof(session));
        }

        return session.Protocol;
    }
}
