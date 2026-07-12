namespace Brokers.Models;

public readonly record struct InstrumentKey
{
    public InstrumentKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim().ToUpperInvariant();
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;

    public static implicit operator InstrumentKey(string value) => new(value);
}
