using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

/// <summary>
/// Converts confirmed swing points into a compact market-structure state. Direction
/// changes are confirmed from swing-to-swing comparisons, while break events use only
/// the latest closed candle and already-confirmed pivots.
/// </summary>
public sealed class MarketStructureAnalyzer
{
    private readonly decimal _directionToleranceAtr;

    public MarketStructureAnalyzer(decimal directionToleranceAtr = 0.05m)
    {
        if (directionToleranceAtr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(directionToleranceAtr));
        }

        _directionToleranceAtr = directionToleranceAtr;
    }

    public MarketStructureSnapshot Analyze(
        IReadOnlyList<SwingPoint> swings,
        Candle latestCandle,
        decimal? atr,
        MarketStructureSnapshot? previous = null)
    {
        ArgumentNullException.ThrowIfNull(swings);
        ArgumentNullException.ThrowIfNull(latestCandle);
        previous ??= MarketStructureSnapshot.Empty;

        DateTimeOffset availableAt =
            latestCandle.CloseTime ?? latestCandle.OpenTime;
        SwingPoint[] normalized = NormalizeSwings(swings
            .Where(point => point.ConfirmedAt <= availableAt)
            .OrderBy(point => point.PivotTime)
            .ThenBy(point => point.ConfirmedAt));
        SwingPoint[] highs = normalized
            .Where(point => point.Type == SwingType.High)
            .ToArray();
        SwingPoint[] lows = normalized
            .Where(point => point.Type == SwingType.Low)
            .ToArray();

        SwingPoint? lastHigh = highs.LastOrDefault();
        SwingPoint? lastLow = lows.LastOrDefault();
        decimal tolerance = atr is > 0m
            ? atr.Value * _directionToleranceAtr
            : 0m;

        int higherHighs = CountDirectionalRun(highs, tolerance, comparison: 1);
        int lowerHighs = CountDirectionalRun(highs, tolerance, comparison: -1);
        int higherLows = CountDirectionalRun(lows, tolerance, comparison: 1);
        int lowerLows = CountDirectionalRun(lows, tolerance, comparison: -1);

        MarketStructureDirection direction = DetermineDirection(
            highs,
            lows,
            tolerance,
            previous.Direction);
        bool changed = direction != MarketStructureDirection.Unknown &&
            direction != previous.Direction;
        DateTimeOffset? segmentStartedAt = changed
            ? DetermineSegmentStart(highs, lows)
            : previous.SegmentStartedAt ?? DetermineSegmentStart(highs, lows);

        MarketStructureDirection breakReferenceDirection =
            previous.Direction is MarketStructureDirection.Rising or
                MarketStructureDirection.Falling
                ? previous.Direction
                : direction;
        MarketStructureBreak structureBreak = DetermineBreak(
            breakReferenceDirection,
            latestCandle.Prices.Close,
            lastHigh,
            lastLow,
            tolerance);
        decimal strength = CalculateStrength(
            direction,
            highs,
            lows,
            atr,
            higherHighs,
            higherLows,
            lowerHighs,
            lowerLows);

        return new MarketStructureSnapshot
        {
            Direction = direction,
            PreviousDirection = previous.Direction,
            Break = structureBreak,
            DirectionChanged = changed,
            SegmentStartedAt = segmentStartedAt,
            ChangedAt = changed
                ? availableAt
                : previous.ChangedAt,
            LastSwingHigh = lastHigh,
            LastSwingLow = lastLow,
            ConsecutiveHigherHighs = higherHighs,
            ConsecutiveHigherLows = higherLows,
            ConsecutiveLowerHighs = lowerHighs,
            ConsecutiveLowerLows = lowerLows,
            Strength = strength
        };
    }

    /// <summary>
    /// Converts noisy consecutive highs or lows into an alternating structural
    /// sequence. When two same-type swings occur before an opposite swing, only the
    /// more extreme one is structurally relevant.
    /// </summary>
    private static SwingPoint[] NormalizeSwings(IEnumerable<SwingPoint> swings)
    {
        var normalized = new List<SwingPoint>();

        foreach (SwingPoint point in swings)
        {
            if (normalized.Count == 0)
            {
                normalized.Add(point);
                continue;
            }

            SwingPoint previous = normalized[^1];
            if (previous.Type != point.Type ||
                previous.PivotTime == point.PivotTime)
            {
                normalized.Add(point);
                continue;
            }

            bool replace = point.Type switch
            {
                SwingType.High => point.Price >= previous.Price,
                SwingType.Low => point.Price <= previous.Price,
                _ => false
            };

            if (replace)
            {
                normalized[^1] = point;
            }
        }

        return normalized.ToArray();
    }

    private static MarketStructureDirection DetermineDirection(
        IReadOnlyList<SwingPoint> highs,
        IReadOnlyList<SwingPoint> lows,
        decimal tolerance,
        MarketStructureDirection previous)
    {
        if (highs.Count < 2 || lows.Count < 2)
        {
            return MarketStructureDirection.Unknown;
        }

        int highDirection = Compare(highs[^1].Price, highs[^2].Price, tolerance);
        int lowDirection = Compare(lows[^1].Price, lows[^2].Price, tolerance);

        if (highDirection > 0 && lowDirection > 0)
        {
            return MarketStructureDirection.Rising;
        }

        if (highDirection < 0 && lowDirection < 0)
        {
            return MarketStructureDirection.Falling;
        }

        if (highDirection == 0 && lowDirection == 0)
        {
            return MarketStructureDirection.Sideways;
        }

        // One unresolved or opposing leg is a transition, not yet a confirmed
        // reversal. Preserve an established direction until the opposite high/low
        // pair is confirmed; otherwise report sideways.
        return previous is MarketStructureDirection.Rising or
            MarketStructureDirection.Falling
            ? previous
            : MarketStructureDirection.Sideways;
    }

    private static MarketStructureBreak DetermineBreak(
        MarketStructureDirection referenceDirection,
        decimal close,
        SwingPoint? lastHigh,
        SwingPoint? lastLow,
        decimal tolerance)
    {
        if (referenceDirection == MarketStructureDirection.Rising &&
            lastLow is not null &&
            close < lastLow.Price - tolerance)
        {
            return MarketStructureBreak.Bearish;
        }

        if (referenceDirection == MarketStructureDirection.Falling &&
            lastHigh is not null &&
            close > lastHigh.Price + tolerance)
        {
            return MarketStructureBreak.Bullish;
        }

        return MarketStructureBreak.None;
    }

    private static DateTimeOffset? DetermineSegmentStart(
        IReadOnlyList<SwingPoint> highs,
        IReadOnlyList<SwingPoint> lows)
    {
        if (highs.Count < 2 || lows.Count < 2)
        {
            return null;
        }

        return new[]
        {
            highs[^2].PivotTime,
            lows[^2].PivotTime
        }.Min();
    }

    private static int CountDirectionalRun(
        IReadOnlyList<SwingPoint> points,
        decimal tolerance,
        int comparison)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        int count = 0;
        for (int index = points.Count - 1; index > 0; index--)
        {
            if (Compare(points[index].Price, points[index - 1].Price, tolerance) != comparison)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static int Compare(decimal current, decimal previous, decimal tolerance)
    {
        decimal difference = current - previous;
        if (Math.Abs(difference) <= tolerance)
        {
            return 0;
        }

        return Math.Sign(difference);
    }

    private static decimal CalculateStrength(
        MarketStructureDirection direction,
        IReadOnlyList<SwingPoint> highs,
        IReadOnlyList<SwingPoint> lows,
        decimal? atr,
        int higherHighs,
        int higherLows,
        int lowerHighs,
        int lowerLows)
    {
        if (direction == MarketStructureDirection.Unknown ||
            highs.Count < 2 ||
            lows.Count < 2)
        {
            return 0m;
        }

        decimal displacement =
            Math.Abs(highs[^1].Price - highs[^2].Price) +
            Math.Abs(lows[^1].Price - lows[^2].Price);
        decimal normalizedDisplacement = atr is > 0m
            ? Math.Min(4m, displacement / atr.Value)
            : 0m;

        if (direction == MarketStructureDirection.Sideways)
        {
            // Smaller displacement relative to ATR is stronger sideways evidence.
            return Math.Clamp(
                60m - Math.Min(35m, normalizedDisplacement * 12m),
                20m,
                60m);
        }

        int run = direction == MarketStructureDirection.Rising
            ? Math.Min(higherHighs, higherLows)
            : Math.Min(lowerHighs, lowerLows);

        return Math.Clamp(
            25m + run * 15m + normalizedDisplacement * 10m,
            0m,
            100m);
    }
}
