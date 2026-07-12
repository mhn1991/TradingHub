using Networking.Abstractions;

namespace Networking.Exceptions;

public sealed class NetworkTransportNotFoundException : InvalidOperationException
{
    public NetworkTransportNotFoundException(TransportId id, bool streaming)
        : base(
            streaming
                ? $"No streaming transport is registered with ID '{id}'."
                : $"No request transport is registered with ID '{id}'.")
    {
        TransportId = id;
        IsStreaming = streaming;
    }

    public TransportId TransportId { get; }

    public bool IsStreaming { get; }
}
