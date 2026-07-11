using System.Globalization;

namespace Brokers;

/// <summary>
/// Provider-neutral candle period. Provider adapters decide whether a concrete value is supported.
/// </summary>
public readonly record struct CandleTimeframe
{
    public CandleTimeframe(int value, CandleTimeframeUnit unit)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The timeframe value must be positive.");
        }

        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown timeframe unit.");
        }

        Value = value;
        Unit = unit;
    }

    public int Value { get; }

    public CandleTimeframeUnit Unit { get; }

    public bool IsValid => Value > 0 && Enum.IsDefined(Unit);

    public static CandleTimeframe Seconds(int value) => new(value, CandleTimeframeUnit.Second);

    public static CandleTimeframe Minutes(int value) => new(value, CandleTimeframeUnit.Minute);

    public static CandleTimeframe Hours(int value) => new(value, CandleTimeframeUnit.Hour);

    public static CandleTimeframe Days(int value) => new(value, CandleTimeframeUnit.Day);

    public static CandleTimeframe Weeks(int value) => new(value, CandleTimeframeUnit.Week);

    public static CandleTimeframe Months(int value) => new(value, CandleTimeframeUnit.Month);

    public DateTimeOffset AddPeriods(DateTimeOffset value, int periods = 1)
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("The timeframe is not initialized.");
        }

        int amount = checked(Value * periods);
        return Unit switch
        {
            CandleTimeframeUnit.Second => value.AddSeconds(amount),
            CandleTimeframeUnit.Minute => value.AddMinutes(amount),
            CandleTimeframeUnit.Hour => value.AddHours(amount),
            CandleTimeframeUnit.Day => value.AddDays(amount),
            CandleTimeframeUnit.Week => value.AddDays(checked(amount * 7)),
            CandleTimeframeUnit.Month => value.AddMonths(amount),
            _ => throw new InvalidOperationException($"Unsupported timeframe unit '{Unit}'.")
        };
    }

    public override string ToString()
    {
        string suffix = Unit switch
        {
            CandleTimeframeUnit.Second => "s",
            CandleTimeframeUnit.Minute => "m",
            CandleTimeframeUnit.Hour => "h",
            CandleTimeframeUnit.Day => "d",
            CandleTimeframeUnit.Week => "w",
            CandleTimeframeUnit.Month => "mo",
            _ => "?"
        };

        return Value.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
