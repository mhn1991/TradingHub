namespace Networking;

public sealed class NetworkResponseTooLargeException : NetworkException
{
    internal NetworkResponseTooLargeException(
        NetworkProtocol protocol,
        string destination,
        int maximumBodyBytes)
        : base(
            $"The {protocol} response from '{destination}' exceeded the configured {maximumBodyBytes} byte body limit.",
            protocol,
            destination)
    {
        MaximumBodyBytes = maximumBodyBytes;
    }

    public int MaximumBodyBytes { get; }
}
