namespace Networking;

public sealed class NetworkTransportException : NetworkException
{
    internal NetworkTransportException(
        NetworkProtocol protocol,
        string destination,
        Exception innerException)
        : base($"The {protocol} operation for '{destination}' failed before it completed.",
            protocol,
            destination,
            innerException)
    {
    }
}
