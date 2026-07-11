namespace Networking;

/// <summary>
/// Protocol-neutral base for an operation dispatched by Networking.
/// </summary>
public abstract class NetworkRequest
{
    protected NetworkRequest(NetworkProtocol protocol)
    {
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown network protocol.");
        }

        Protocol = protocol;
    }

    public NetworkProtocol Protocol { get; }
}

/// <summary>
/// Associates a protocol-specific request with its expected response type.
/// </summary>
/// <typeparam name="TResponse">The response returned by the matching protocol client.</typeparam>
public abstract class NetworkRequest<TResponse> : NetworkRequest
    where TResponse : NetworkResponse
{
    protected NetworkRequest(NetworkProtocol protocol)
        : base(protocol)
    {
    }
}
