namespace Networking.Duplex;

/// <summary>
/// Extracts routing information from an inbound WebSocket or FIX message.
/// Implement this per broker/protocol.
/// </summary>
public interface IInboundMessageClassifier
{
    bool TryGetCorrelationId(
        ReadOnlyMemory<byte> message,
        out string correlationId);

    bool TryGetStreamKey(
        ReadOnlyMemory<byte> message,
        out string streamKey);
}
