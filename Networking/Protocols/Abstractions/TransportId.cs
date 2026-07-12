namespace Networking.Abstractions;

/// <summary>
/// Identifies one configured transport instance, for example
/// "oanda.rest", "binance.market-data", or "broker.fix.trading".
/// </summary>
public readonly struct TransportId : IEquatable<TransportId>
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public TransportId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public bool Equals(TransportId other) => Comparer.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is TransportId other && Equals(other);

    public override int GetHashCode() => Comparer.GetHashCode(Value ?? string.Empty);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator TransportId(string value) => new(value);

    public static bool operator ==(TransportId left, TransportId right) => left.Equals(right);

    public static bool operator !=(TransportId left, TransportId right) => !left.Equals(right);
}
