namespace Networking;

public sealed class NetworkTimeoutException : NetworkException
{
    internal NetworkTimeoutException(
        NetworkProtocol protocol,
        string destination,
        TimeSpan timeout,
        Exception innerException)
        : base(
            $"The {protocol} operation for '{destination}' timed out after {timeout}.",
            protocol,
            destination,
            innerException)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}
