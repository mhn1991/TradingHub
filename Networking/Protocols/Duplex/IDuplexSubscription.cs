using System.Threading.Channels;
using Networking.Abstractions;

namespace Networking.Duplex;

/// <summary>
/// A continuous event stream over a long-lived connection.
/// </summary>
public interface IDuplexSubscription<TEvent> : INetworkSubscription<TEvent>
{
    string StreamKey { get; }

    int BufferCapacity { get; }

    BoundedChannelFullMode FullMode { get; }

    /// <summary>
    /// Returns the broker-specific subscription message. Empty means no message is required.
    /// </summary>
    ReadOnlyMemory<byte> EncodeSubscribe();

    /// <summary>
    /// Returns the unsubscribe message, or null when none is required.
    /// </summary>
    ReadOnlyMemory<byte>? EncodeUnsubscribe();

    TEvent DecodeEvent(ReadOnlyMemory<byte> message);
}
