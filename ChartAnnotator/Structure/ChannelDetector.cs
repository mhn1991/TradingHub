using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record ChannelOptions(
    // Maximum amount by which the two boundaries may drift apart over the
    // active lifetime of the channel, measured in ATR.
    decimal MaximumBoundaryDriftAtr = 0.75m,

    // Distance from a boundary that is considered a pivot touch.
    decimal TouchToleranceAtr = 0.20m,

    // Distance outside the channel that is tolerated before a pivot or the
    // latest close is treated as a channel break.
    decimal BreakToleranceAtr = 0.35m,

    decimal MinimumWidthAtr = 0.5m,
    decimal MaximumWidthAtr = 20m,

    // Prevents strongly expanding or contracting line pairs from being
    // classified as parallel channels.
    decimal MaximumWidthChangeRatio = 0.35m,

    // A valid channel needs evidence from both swing lows and swing highs.
    int MinimumTouchesPerBoundary = 2,

    // At least one boundary must have been touched within this many confirmed
    // swings, otherwise the channel is considered stale.
    int MaximumRecentSwingAge = 8,

    int MaximumBreakViolations = 1,
    decimal MaximumBreakViolationRatio = 0.10m,

    // Total projected movement below this amount is treated as sideways.
    decimal SidewaysMaximumMoveAtr = 0.50m,

    decimal DuplicateBoundaryToleranceAtr = 0.20m,
    int MaximumChannels = 6);

/// <summary>
/// Builds active price channels from support/resistance trendlines and validates
/// them against confirmed swings. Candidates and pivots are processed recent-first.
/// </summary>
public sealed class ChannelDetector
{
    private readonly ChannelOptions _options;

    public ChannelDetector(ChannelOptions? options = null)
    {
        _options = options ?? new ChannelOptions();

        if (_options.MaximumBoundaryDriftAtr <= 0m ||
            _options.TouchToleranceAtr <= 0m ||
            _options.BreakToleranceAtr < 0m ||
            _options.MinimumWidthAtr <= 0m ||
            _options.MaximumWidthAtr < _options.MinimumWidthAtr ||
            _options.MaximumWidthChangeRatio <= 0m ||
            _options.MinimumTouchesPerBoundary < 2 ||
            _options.MaximumRecentSwingAge < 0 ||
            _options.MaximumBreakViolations < 0 ||
            _options.MaximumBreakViolationRatio is < 0m or > 1m ||
            _options.SidewaysMaximumMoveAtr < 0m ||
            _options.DuplicateBoundaryToleranceAtr <= 0m ||
            _options.MaximumChannels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>
    /// Compatibility overload for callers that do not have the latest close.
    /// Supplying the latest close through the other overload is preferred because
    /// it lets the detector immediately discard a broken channel.
    /// </summary>
    public IReadOnlyList<PriceChannel> Detect(
        IReadOnlyList<Trendline> trendlines,
        IReadOnlyList<SwingPoint> swings,
        DateTimeOffset at,
        decimal atr,
        MarketStructureDirection expectedDirection = MarketStructureDirection.Unknown) =>
        DetectCore(trendlines, swings, at, atr, null, expectedDirection);

    public IReadOnlyList<PriceChannel> Detect(
        IReadOnlyList<Trendline> trendlines,
        IReadOnlyList<SwingPoint> swings,
        DateTimeOffset at,
        decimal atr,
        decimal currentPrice,
        MarketStructureDirection expectedDirection = MarketStructureDirection.Unknown) =>
        DetectCore(trendlines, swings, at, atr, currentPrice, expectedDirection);

    private IReadOnlyList<PriceChannel> DetectCore(
        IReadOnlyList<Trendline> trendlines,
        IReadOnlyList<SwingPoint> swings,
        DateTimeOffset at,
        decimal atr,
        decimal? currentPrice,
        MarketStructureDirection expectedDirection)
    {
        ArgumentNullException.ThrowIfNull(trendlines);
        ArgumentNullException.ThrowIfNull(swings);

        if (atr <= 0m || trendlines.Count == 0 || swings.Count < 4)
        {
            return [];
        }

        // SwingSnapshot contains confirmed pivots, but the time checks make the
        // detector safe when it is also called directly in a backtest.
        SwingPoint[] orderedSwings = swings
            .Where(swing => swing.PivotTime <= at && swing.ConfirmedAt <= at)
            .OrderByDescending(swing => swing.PivotTime)
            .ThenByDescending(swing => swing.ConfirmedAt)
            .ToArray();

        if (orderedSwings.Length < 4)
        {
            return [];
        }

        BoundaryEvidence[] supports = trendlines
            .Where(line => line.Type == TrendlineType.Support)
            .Select(line => BuildEvidence(
                line,
                orderedSwings,
                SwingType.Low,
                at,
                atr))
            .OfType<BoundaryEvidence>()
            .OrderByDescending(evidence => evidence.LastTouch)
            .ThenByDescending(evidence => evidence.Line.FitScore)
            .ToArray();

        BoundaryEvidence[] resistances = trendlines
            .Where(line => line.Type == TrendlineType.Resistance)
            .Select(line => BuildEvidence(
                line,
                orderedSwings,
                SwingType.High,
                at,
                atr))
            .OfType<BoundaryEvidence>()
            .OrderByDescending(evidence => evidence.LastTouch)
            .ThenByDescending(evidence => evidence.Line.FitScore)
            .ToArray();

        var candidates = new List<ChannelCandidate>();

        // Both collections are recent-first, so active structures are evaluated
        // before old support/resistance combinations.
        foreach (BoundaryEvidence lowerEvidence in supports)
        {
            foreach (BoundaryEvidence upperEvidence in resistances)
            {
                TryAddCandidate(
                    candidates,
                    lowerEvidence,
                    upperEvidence,
                    orderedSwings,
                    at,
                    atr,
                    currentPrice,
                    expectedDirection);
            }
        }

        var selected = new List<ChannelCandidate>(_options.MaximumChannels);

        foreach (ChannelCandidate candidate in candidates
                     .OrderByDescending(item => item.LastTouch)
                     .ThenByDescending(item => item.Channel.Confidence)
                     .ThenByDescending(item => item.Channel.StartTime))
        {
            if (selected.Any(existing => IsNearDuplicate(existing, candidate, atr)))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= _options.MaximumChannels)
            {
                break;
            }
        }

        return selected
            .Select(candidate => candidate.Channel)
            .ToArray();
    }

    private BoundaryEvidence? BuildEvidence(
        Trendline line,
        IReadOnlyList<SwingPoint> orderedSwings,
        SwingType requiredSwingType,
        DateTimeOffset at,
        decimal atr)
    {
        decimal tolerance = atr * _options.TouchToleranceAtr;

        SwingPoint[] touches = orderedSwings
            .Where(swing =>
                swing.Type == requiredSwingType &&
                swing.PivotTime >= line.StartTime &&
                swing.PivotTime <= at &&
                Math.Abs(swing.Price - line.PriceAt(swing.PivotTime)) <= tolerance)
            .ToArray();

        if (touches.Length < _options.MinimumTouchesPerBoundary)
        {
            return null;
        }

        return new BoundaryEvidence(line, touches, touches[0].PivotTime);
    }

    private void TryAddCandidate(
        ICollection<ChannelCandidate> candidates,
        BoundaryEvidence lowerEvidence,
        BoundaryEvidence upperEvidence,
        IReadOnlyList<SwingPoint> orderedSwings,
        DateTimeOffset at,
        decimal atr,
        decimal? currentPrice,
        MarketStructureDirection expectedDirection)
    {
        Trendline lower = lowerEvidence.Line;
        Trendline upper = upperEvidence.Line;

        DateTimeOffset startTime = lower.StartTime > upper.StartTime
            ? lower.StartTime
            : upper.StartTime;

        if (startTime >= at)
        {
            return;
        }

        SwingPoint[] lowerTouches = lowerEvidence.Touches
            .Where(touch => touch.PivotTime >= startTime)
            .ToArray();
        SwingPoint[] upperTouches = upperEvidence.Touches
            .Where(touch => touch.PivotTime >= startTime)
            .ToArray();

        // Each trendline already has the configured minimum evidence. Within the
        // common lifetime of the pair, require at least one observed touch on each
        // side; requiring two again here rejects valid channels whose boundaries
        // were established at slightly different times.
        if (lowerTouches.Length == 0 || upperTouches.Length == 0)
        {
            return;
        }

        DateTimeOffset lastTouch = lowerTouches[0].PivotTime >= upperTouches[0].PivotTime
            ? lowerTouches[0].PivotTime
            : upperTouches[0].PivotTime;

        int recentSwingAge = orderedSwings.Count(swing => swing.PivotTime > lastTouch);
        if (recentSwingAge > _options.MaximumRecentSwingAge)
        {
            return;
        }

        decimal spanSeconds = (decimal)(at - startTime).TotalSeconds;
        if (spanSeconds <= 0m)
        {
            return;
        }

        // Compare slope difference over the actual channel lifetime. This remains
        // stable for nearly-horizontal lines, unlike dividing by the line slope.
        decimal boundaryDriftAtr =
            Math.Abs(lower.SlopePerSecond - upper.SlopePerSecond) *
            spanSeconds /
            atr;

        if (boundaryDriftAtr > _options.MaximumBoundaryDriftAtr)
        {
            return;
        }

        DateTimeOffset midpoint = startTime +
            TimeSpan.FromTicks((at - startTime).Ticks / 2);

        decimal startWidth = upper.PriceAt(startTime) - lower.PriceAt(startTime);
        decimal middleWidth = upper.PriceAt(midpoint) - lower.PriceAt(midpoint);
        decimal currentLower = lower.PriceAt(at);
        decimal currentUpper = upper.PriceAt(at);
        decimal currentWidth = currentUpper - currentLower;

        if (startWidth <= 0m || middleWidth <= 0m || currentWidth <= 0m)
        {
            // The lines cross inside the candidate interval.
            return;
        }

        decimal minimumWidth = Math.Min(startWidth, Math.Min(middleWidth, currentWidth));
        decimal maximumWidth = Math.Max(startWidth, Math.Max(middleWidth, currentWidth));

        if (minimumWidth / atr < _options.MinimumWidthAtr ||
            maximumWidth / atr > _options.MaximumWidthAtr)
        {
            return;
        }

        decimal averageWidth = (startWidth + middleWidth + currentWidth) / 3m;
        decimal widthChangeRatio = (maximumWidth - minimumWidth) / averageWidth;
        if (widthChangeRatio > _options.MaximumWidthChangeRatio)
        {
            return;
        }

        decimal averageSlope = (lower.SlopePerSecond + upper.SlopePerSecond) / 2m;
        ChannelDirection direction = DetermineDirection(averageSlope, spanSeconds, atr);
        if (!MatchesExpectedDirection(direction, expectedDirection))
        {
            return;
        }

        SwingPoint[] structuralSwings = orderedSwings
            .Where(swing => swing.PivotTime >= startTime)
            .ToArray();

        if (structuralSwings.Length < _options.MinimumTouchesPerBoundary * 2)
        {
            return;
        }

        int breakViolations = CountBreakViolations(
            structuralSwings,
            lower,
            upper,
            atr);
        decimal breakViolationRatio =
            (decimal)breakViolations / structuralSwings.Length;

        if (breakViolations > _options.MaximumBreakViolations ||
            breakViolationRatio > _options.MaximumBreakViolationRatio)
        {
            return;
        }

        if (currentPrice is decimal latestPrice)
        {
            decimal breakTolerance = atr * _options.BreakToleranceAtr;
            if (latestPrice < currentLower - breakTolerance ||
                latestPrice > currentUpper + breakTolerance)
            {
                return;
            }
        }

        decimal confidence = CalculateConfidence(
            lower,
            upper,
            lowerTouches.Length,
            upperTouches.Length,
            recentSwingAge,
            structuralSwings.Length,
            boundaryDriftAtr,
            widthChangeRatio,
            breakViolationRatio);

        var channel = new PriceChannel
        {
            LowerLine = lower,
            UpperLine = upper,
            StartTime = startTime,
            EndTime = at,
            Direction = direction,
            Width = currentWidth,
            WidthAtr = currentWidth / atr,
            Confidence = confidence
        };

        candidates.Add(new ChannelCandidate(
            channel,
            lastTouch,
            currentLower,
            currentUpper));
    }

    private int CountBreakViolations(
        IReadOnlyList<SwingPoint> swings,
        Trendline lower,
        Trendline upper,
        decimal atr)
    {
        decimal tolerance = atr * _options.BreakToleranceAtr;
        int violations = 0;

        foreach (SwingPoint swing in swings)
        {
            decimal lowerPrice = lower.PriceAt(swing.PivotTime);
            decimal upperPrice = upper.PriceAt(swing.PivotTime);

            // Every confirmed pivot must remain inside the envelope. Checking both
            // sides also catches cases such as a swing high occurring below support.
            if (swing.Price < lowerPrice - tolerance ||
                swing.Price > upperPrice + tolerance)
            {
                violations++;
            }
        }

        return violations;
    }

    private ChannelDirection DetermineDirection(
        decimal averageSlope,
        decimal spanSeconds,
        decimal atr)
    {
        decimal totalMoveAtr = averageSlope * spanSeconds / atr;
        if (Math.Abs(totalMoveAtr) <= _options.SidewaysMaximumMoveAtr)
        {
            return ChannelDirection.Sideways;
        }

        return totalMoveAtr > 0m
            ? ChannelDirection.Rising
            : ChannelDirection.Falling;
    }

    private decimal CalculateConfidence(
        Trendline lower,
        Trendline upper,
        int lowerTouchCount,
        int upperTouchCount,
        int recentSwingAge,
        int structuralSwingCount,
        decimal boundaryDriftAtr,
        decimal widthChangeRatio,
        decimal breakViolationRatio)
    {
        decimal fitScore = Math.Clamp(
            (lower.FitScore + upper.FitScore) / 2m,
            0m,
            100m);
        decimal touchScore = Math.Clamp(
            (lowerTouchCount + upperTouchCount) / 8m * 100m,
            0m,
            100m);
        decimal recencyScore = Math.Clamp(
            100m * (1m -
                (decimal)recentSwingAge / (_options.MaximumRecentSwingAge + 1m)),
            0m,
            100m);
        decimal spanScore = Math.Clamp(
            structuralSwingCount / 12m * 100m,
            0m,
            100m);
        decimal driftPenalty =
            boundaryDriftAtr / _options.MaximumBoundaryDriftAtr * 15m;
        decimal widthChangePenalty =
            widthChangeRatio / _options.MaximumWidthChangeRatio * 10m;
        decimal breakPenalty = breakViolationRatio * 100m * 0.20m;

        return Math.Clamp(
            fitScore * 0.45m +
            touchScore * 0.25m +
            recencyScore * 0.20m +
            spanScore * 0.10m -
            driftPenalty -
            widthChangePenalty -
            breakPenalty,
            0m,
            100m);
    }

    private bool IsNearDuplicate(
        ChannelCandidate first,
        ChannelCandidate second,
        decimal atr)
    {
        if (first.Channel.Direction != second.Channel.Direction)
        {
            return false;
        }

        decimal tolerance = atr * _options.DuplicateBoundaryToleranceAtr;
        return
            Math.Abs(first.CurrentLowerPrice - second.CurrentLowerPrice) <= tolerance &&
            Math.Abs(first.CurrentUpperPrice - second.CurrentUpperPrice) <= tolerance;
    }

    private static bool MatchesExpectedDirection(
        ChannelDirection channel,
        MarketStructureDirection expected) => expected switch
        {
            MarketStructureDirection.Rising => channel == ChannelDirection.Rising,
            MarketStructureDirection.Falling => channel == ChannelDirection.Falling,
            MarketStructureDirection.Sideways => channel == ChannelDirection.Sideways,
            _ => true
        };

    private sealed record BoundaryEvidence(
        Trendline Line,
        SwingPoint[] Touches,
        DateTimeOffset LastTouch);

    private sealed record ChannelCandidate(
        PriceChannel Channel,
        DateTimeOffset LastTouch,
        decimal CurrentLowerPrice,
        decimal CurrentUpperPrice);
}
