namespace Networking;

/// <summary>
/// Protocol-neutral base for a result returned by Networking.
/// </summary>
public abstract class NetworkResponse
{
    protected NetworkResponse(NetworkProtocol protocol)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown network protocol.");
        }

        Protocol = protocol;
    }

    public NetworkProtocol Protocol { get; }
}
