namespace Networking;

/// <summary>
/// Opens a persistent FIX or FIX-over-TLS transport session.
/// Logon fields, sequence policy, and business messages remain explicit FIX messages owned by the caller.
/// </summary>
public sealed class FixConnectRequest : NetworkRequest<FixConnectResponse>
{
    public FixConnectRequest(
        NetworkProtocol protocol,
        string host,
        int port,
        TimeSpan? connectTimeout = null,
        string? tlsTargetHost = null)
        : base(protocol)
    {
        if (protocol is not NetworkProtocol.Fix and not NetworkProtocol.FixTls)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocol),
                protocol,
                "A FIX connection requires the FIX or FIX-over-TLS protocol.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The port must be between 1 and 65535.");
        }

        if (connectTimeout is { } value &&
            value != System.Threading.Timeout.InfiniteTimeSpan &&
            value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                "The timeout must be positive or infinite.");
        }

        if (protocol == NetworkProtocol.FixTls && string.IsNullOrWhiteSpace(tlsTargetHost))
        {
            tlsTargetHost = host;
        }

        if (protocol == NetworkProtocol.Fix && tlsTargetHost is not null)
        {
            throw new ArgumentException("A TLS target host is only valid for FIX-over-TLS.", nameof(tlsTargetHost));
        }

        Host = host;
        Port = port;
        ConnectTimeout = connectTimeout;
        TlsTargetHost = tlsTargetHost;
    }

    public string Host { get; }

    public int Port { get; }

    public TimeSpan? ConnectTimeout { get; }

    public string? TlsTargetHost { get; }

    public string Destination => $"{Host}:{Port}";
}
