namespace Agent.Strategies.Alfonso.Trend;

/// <summary>
/// Draws trendlines the way module 3 specifies: connect the latest two valleys or peaks, require the
/// second to sit inside the first, confirm only once price has extended past them, and never cut
/// through a candle.
/// </summary>
public static class TrendlineBuilder
{
    /// <summary>
    /// Builds the current bullish trendline from the two most recent valleys, or null when the rules
    /// do not permit one. Module 3 is explicit that this is normal: "sometimes you won't be able to
    /// draw a trendline ... Don't get obsessed with trendlines, sometimes they just can't be drawn."
    /// </summary>
    public static Trendline? Bullish(
        IReadOnlyList<SwingPoint> valleys,
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<DateTimeOffset> times,
        int currentIndex)
    {
        if (valleys.Count < 2)
            return null;

        SwingPoint first = valleys[^2];
        SwingPoint second = valleys[^1];

        // "The low of Valley V[2] always has to be higher than the low of V[1]."
        if (second.Price <= first.Price)
            return null;

        // "Valley V[1] and Valley V[2] can be connected once the high of V[2] makes a high higher
        // than [4]" - the pair is only usable after price has pushed past the interim high.
        if (!ExtendedBeyond(highs, first.Index, second.Index, currentIndex, higher: true))
            return null;

        return Fit(first, second, lows, times, TrendlineDirection.Bullish);
    }

    /// <summary>
    /// Builds the current bearish trendline from the two most recent peaks. Module 3: "The high of
    /// the second peak at [P2] should not be higher than the high of the first peak at [P1]" and
    /// "Once price makes a low lower than [L1], a bearish trendline can connect peaks [P1] and [P2]."
    /// </summary>
    public static Trendline? Bearish(
        IReadOnlyList<SwingPoint> peaks,
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<DateTimeOffset> times,
        int currentIndex)
    {
        if (peaks.Count < 2)
            return null;

        SwingPoint first = peaks[^2];
        SwingPoint second = peaks[^1];

        if (second.Price >= first.Price)
            return null;

        if (!ExtendedBeyond(lows, first.Index, second.Index, currentIndex, higher: false))
            return null;

        return Fit(first, second, highs, times, TrendlineDirection.Bearish);
    }

    /// <summary>
    /// The aggressive line module 3 permits once a timeframe is over-extended: "In over-extension
    /// with three or more consecutive CPs, the trendlines can be drawn more aggressively connecting
    /// the last three CPs."
    /// <para>
    /// This is the one place continuation patterns may anchor a line. Everywhere else module 3
    /// forbids it - "Continuation Patterns (CPs) will not be used to connect trendlines" - because a
    /// CP is not the origin of an impulse; in over-extension there is nothing else to connect, since
    /// a market running without correction prints no new peaks or valleys to draw from.
    /// </para>
    /// <para>
    /// Two conditions the ordinary builders impose are dropped deliberately. The three anchors must
    /// run monotonically the way the line does, which is what makes them a line rather than three
    /// unrelated pauses; but the "price has extended beyond the pair" test is not applied, because
    /// over-extension is that condition - a market with three consecutive continuation patterns has
    /// by definition kept going. The line is then fitted to the outer two anchors and pulled back off
    /// any candle it would cut, exactly as elsewhere.
    /// </para>
    /// </summary>
    public static Trendline? OverExtended(
        IReadOnlyList<SwingPoint> continuations,
        IReadOnlyList<decimal> constraint,
        IReadOnlyList<DateTimeOffset> times,
        int currentIndex,
        TrendlineDirection direction,
        int anchors = 3)
    {
        ArgumentNullException.ThrowIfNull(continuations);

        if (anchors < 2 || continuations.Count < anchors)
            return null;

        bool bullish = direction == TrendlineDirection.Bullish;

        for (int back = anchors; back > 1; back--)
        {
            SwingPoint earlier = continuations[^back];
            SwingPoint later = continuations[^(back - 1)];
            bool ordered = bullish ? later.Price > earlier.Price : later.Price < earlier.Price;
            if (!ordered)
                return null;
        }

        SwingPoint first = continuations[^anchors];
        SwingPoint last = continuations[^1];

        if (last.Index >= currentIndex)
            return null;

        return Fit(first, last, constraint, times, direction);
    }

    /// <summary>
    /// Whether price has pushed past the range spanned by the two swings, which is what turns a pair
    /// of swings into a drawable trendline.
    /// </summary>
    private static bool ExtendedBeyond(
        IReadOnlyList<decimal> series, int firstIndex, int secondIndex, int currentIndex, bool higher)
    {
        if (secondIndex >= currentIndex || firstIndex < 0 || currentIndex >= series.Count)
            return false;

        decimal interim = series[firstIndex];
        for (int index = firstIndex + 1; index <= secondIndex; index++)
            interim = higher ? Math.Max(interim, series[index]) : Math.Min(interim, series[index]);

        for (int index = secondIndex + 1; index <= currentIndex; index++)
        {
            if (higher ? series[index] > interim : series[index] < interim)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Anchors the line at the first swing and takes the steepest slope that still clears every
    /// candle in between.
    /// <para>
    /// Module 3 forbids cutting candles - "the trendlines cannot go through wicks or candlestick
    /// bodies" - and tells the reader to "adjust the trendline in such a way that candlesticks will
    /// be respected". Connecting the two swing extremes literally would violate that whenever a bar
    /// between them ran past the straight line, so the second anchor is pulled back to whichever bar
    /// actually constrains it. That bar is the one the line genuinely rests on.
    /// </para>
    /// </summary>
    private static Trendline? Fit(
        SwingPoint first,
        SwingPoint second,
        IReadOnlyList<decimal> constraint,
        IReadOnlyList<DateTimeOffset> times,
        TrendlineDirection direction)
    {
        if (second.Index <= first.Index || second.Index >= constraint.Count)
            return null;

        bool bullish = direction == TrendlineDirection.Bullish;
        decimal slope = (second.Price - first.Price) / (second.Index - first.Index);
        int anchorIndex = second.Index;

        for (int index = first.Index + 1; index <= second.Index; index++)
        {
            decimal candidate = (constraint[index] - first.Price) / (index - first.Index);
            bool tighter = bullish ? candidate < slope : candidate > slope;
            if (!tighter)
                continue;

            slope = candidate;
            anchorIndex = index;
        }

        return new Trendline
        {
            Direction = direction,
            FromIndex = first.Index,
            FromPrice = first.Price,
            ToIndex = anchorIndex,
            ToPrice = first.Price + (slope * (anchorIndex - first.Index)),
            FromTime = first.At,
            ToTime = times[anchorIndex]
        };
    }
}
