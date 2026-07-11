namespace Networking;

/// <summary>
/// Base class for failures where no complete HTTP response is available.
/// </summary>
public abstract class NetworkException : Exception
{
    protected NetworkException(
        string message,
        NetworkProtocol protocol,
        string destination,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        Protocol = protocol;
        Destination = destination;
    }

    public NetworkProtocol Protocol { get; }

    public string Destination { get; }
}
