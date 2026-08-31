namespace Agent.Strategies.Alfonso.Trend;

/// <summary>Which way a trendline runs, and therefore which side a break has to come from.</summary>
public enum TrendlineDirection
{
    /// <summary>Drawn under valleys. Broken by a full candle below it.</summary>
    Bullish,

    /// <summary>Drawn over peaks. Broken by a full candle above it.</summary>
    Bearish
}

/// <summary>
/// A trendline connecting two swings, expressed in bar-index space.
/// <para>
/// Slope is per bar, not per unit of wall-clock time. Sessions close and weekends intervene, so a
/// time-based slope would tilt a line simply because a weekend sat between its anchors, and the
/// break test would then fire on the calendar rather than on price.
/// </para>
/// </summary>
public sealed record Trendline
{
    public required TrendlineDirection Direction { get; init; }

    public required int FromIndex { get; init; }

    public required decimal FromPrice { get; init; }

    public required int ToIndex { get; init; }

    public required decimal ToPrice { get; init; }

    public required DateTimeOffset FromTime { get; init; }

    public required DateTimeOffset ToTime { get; init; }

    /// <summary>Price change per bar. Zero when the two anchors sit at the same level.</summary>
    public decimal Slope => ToIndex == FromIndex ? 0m : (ToPrice - FromPrice) / (ToIndex - FromIndex);

    /// <summary>The line's price at any bar index, extrapolated forward past the second anchor.</summary>
    public decimal PriceAt(int index) => FromPrice + (Slope * (index - FromIndex));

    /// <summary>
    /// Whether a candle breaks the line.
    /// <para>
    /// With <paramref name="requireClose"/> set - the default behaviour of the trend layer - the
    /// break is a completed candle CLOSING beyond the line, which is how the rule is taught: "a
    /// candle closes below the trendline". With it clear, the whole candle including wicks must sit
    /// beyond, the stricter reading of the English text's "at least a full OCHL candlestick".
    /// </para>
    /// <para>
    /// Either way a wick alone is never a break; the two readings differ only in whether the wick
    /// may remain on the far side once the body has crossed.
    /// </para>
    /// </summary>
    public bool IsBrokenBy(int index, decimal high, decimal low, decimal close, bool requireClose = true)
    {
        decimal level = PriceAt(index);

        if (requireClose)
            return Direction == TrendlineDirection.Bullish ? close < level : close > level;

        return Direction == TrendlineDirection.Bullish ? high < level : low > level;
    }
}
