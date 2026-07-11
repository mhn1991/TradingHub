using System.Text;

namespace Networking;

/// <summary>
/// One complete, framed FIX message. The payload includes the FIX header, body, checksum, and SOH delimiters.
/// </summary>
public sealed class FixNetworkMessage
{
    private readonly byte[] _payload;

    public FixNetworkMessage(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A FIX message cannot be empty.", nameof(payload));
        }

        _payload = payload.ToArray();
    }

    public ReadOnlyMemory<byte> Payload => _payload;

    public string GetAsciiText() => Encoding.ASCII.GetString(_payload);
}
