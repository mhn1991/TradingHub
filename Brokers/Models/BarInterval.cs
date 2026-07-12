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

    public override string ToString() => $"{Value} {Unit}";
}
