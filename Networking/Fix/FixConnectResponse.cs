namespace Networking;

/// <summary>
/// A successfully opened FIX transport session.
/// </summary>
public sealed class FixConnectResponse : NetworkResponse
{
    public FixConnectResponse(INetworkSession<FixNetworkMessage> session)
        : base(GetProtocol(session))
    {
        Session = session;
    }

    public INetworkSession<FixNetworkMessage> Session { get; }

    private static NetworkProtocol GetProtocol(INetworkSession<FixNetworkMessage> session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Protocol is not NetworkProtocol.Fix and not NetworkProtocol.FixTls)
        {
            throw new ArgumentException("The session does not use a FIX protocol.", nameof(session));
        }

        return session.Protocol;
    }
}
