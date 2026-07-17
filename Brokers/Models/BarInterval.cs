using System.Text.Json.Serialization;

namespace Brokers.Models;

public enum BarUnit
{
    Second,
    Minute,
    Hour,
    Day,
    Week,
    Month
}

/// <summary>
/// Custom-converted because <see cref="BarInterval"/>'s only public constructor validates its
/// arguments: System.Text.Json's reflection-based deserializer prefers a record struct's implicit
/// parameterless constructor over a custom one when both exist, then cannot populate the get-only
/// <see cref="Value"/>/<see cref="Unit"/> properties afterward, silently producing
/// <c>default(BarInterval)</c> (an invalid, zeroed interval) on every round trip without this
/// converter.
/// </summary>
[JsonConverter(typeof(BarIntervalJsonConverter))]
public readonly record struct BarInterval
{
    public BarInterval(int value, BarUnit unit)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
        Unit = unit;
    }

    public int Value { get; }

    public BarUnit Unit { get; }

    public bool IsValid => Value > 0 && Enum.IsDefined(Unit);

    public static BarInterval Seconds(int value) => new(value, BarUnit.Second);
    public static BarInterval Minutes(int value) => new(value, BarUnit.Minute);
    public static BarInterval Hours(int value) => new(value, BarUnit.Hour);
    public static BarInterval Days(int value) => new(value, BarUnit.Day);
    public static BarInterval Weeks(int value) => new(value, BarUnit.Week);
    public static BarInterval Months(int value) => new(value, BarUnit.Month);

    public DateTimeOffset AddTo(DateTimeOffset value)
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("A valid interval is required to calculate a timestamp.");
        }

        return Unit switch
        {
            BarUnit.Second => value.AddSeconds(Value),
            BarUnit.Minute => value.AddMinutes(Value),
            BarUnit.Hour => value.AddHours(Value),
            BarUnit.Day => value.AddDays(Value),
            BarUnit.Week => value.AddDays(checked(Value * 7)),
            BarUnit.Month => value.AddMonths(Value),
            _ => throw new ArgumentOutOfRangeException(nameof(Unit))
        };
    }

    public override string ToString() => $"{Value} {Unit}";
}
