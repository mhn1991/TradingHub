using Networking.Abstractions;

namespace Networking.Duplex;

/// <summary>
/// A request/response operation sent over a long-lived connection.
/// </summary>
public interface IDuplexCommand<TResponse> : INetworkCommand<TResponse>
{
    string CorrelationId { get; }

    TimeSpan Timeout { get; }

    ReadOnlyMemory<byte> Encode();

    TResponse DecodeResponse(ReadOnlyMemory<byte> response);
}
