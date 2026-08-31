namespace Agent.Strategies.Alfonso.Zones;

/// <summary>
/// One candle, reduced to what the Set and Forget rules actually read.
/// <para>
/// The rules are expressed entirely in open/high/low/close and time. Volume, spread and instrument
/// identity never appear in them, so they are deliberately absent here: a bar type that cannot
/// carry an input the rules do not use cannot accidentally start depending on one.
/// </para>
/// </summary>
public readonly record struct AlfonsoBar(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close)
{
    /// <summary>High minus low. Zero for a completely flat bar, which several ratios guard against.</summary>
    public decimal Range => High - Low;

    /// <summary>Absolute body size, ignoring direction.</summary>
    public decimal Body => Math.Abs(Close - Open);

    /// <summary>Higher of open and close.</summary>
    public decimal BodyTop => Math.Max(Open, Close);

    /// <summary>Lower of open and close.</summary>
    public decimal BodyBottom => Math.Min(Open, Close);

    public bool IsBullish => Close > Open;

    public bool IsBearish => Close < Open;

    /// <summary>
    /// Body as a fraction of the whole candle range, the quantity both candle rules in the course
    /// are stated in ("bodies &lt;= 50% of the candle range" for basing, "about 80% of the whole
    /// candle range" for an ERC).
    /// <para>
    /// A zero-range bar returns 1: it is a single price with no wick, which is the opposite of the
    /// pause that a basing candle is supposed to represent, so it must not qualify as one.
    /// </para>
    /// </summary>
    public decimal BodyRatio => Range <= 0m ? 1m : Body / Range;
}
