using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record ChannelOptions(
    // Distance from a boundary that is considered a pivot touch.
    decimal TouchToleranceAtr = 0.25m,

    // Distance outside the channel that is tolerated before a pivot or the
    // latest close is treated as a channel break.
    decimal BreakToleranceAtr = 0.35m,

    decimal MinimumWidthAtr = 0.5m,
    decimal MaximumWidthAtr = 20m,

    // When pairing two independently fitted trendlines, the maximum amount by
    // which their slopes may diverge over the shared lifetime (in ATR).
    // Parallel construction does not use this gate.
    decimal MaximumBoundaryDriftAtr = 1.50m,

    // Prevents strongly expanding or contracting paired lines from being
    // classified as parallel channels.
    decimal MaximumWidthChangeRatio = 0.50m,

    // A valid channel needs evidence from both swing lows and swing highs.
    int MinimumTouchesPerBoundary = 2,

    // At least one boundary must have been touched within this many confirmed
    // swings, otherwise the channel is considered stale.
    int MaximumRecentSwingAge = 12,

    int MaximumBreakViolations = 2,
    decimal MaximumBreakViolationRatio = 0.15m,

    // Total projected movement below this amount is treated as sideways.
    decimal SidewaysMaximumMoveAtr = 0.50m,

    decimal DuplicateBoundaryToleranceAtr = 0.25m,
    int MaximumChannels = 6);

/// <summary>
/// Builds active price channels primarily by projecting a parallel opposite
/// boundary from a fitted trendline through opposite-side swing pivots.
/// Independently fitted support/resistance pairs are also accepted when their
/// slopes remain near-parallel over the shared lifetime.
/// </summary>
public sealed class ChannelDetector
{
    private readonly ChannelOptions _options;

    public ChannelDetector(ChannelOptions? options = null)
    {
        _options = options ?? new ChannelOptions();

        if (_options.TouchToleranceAtr <= 0m ||
            _options.BreakToleranceAtr < 0m ||
            _options.MinimumWidthAtr <= 0m ||
            _options.MaximumWidthAtr < _options.MinimumWidthAtr ||
            _options.MaximumBoundaryDriftAtr <= 0m ||
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
            .Select(line => BuildEvidence(line, orderedSwings, SwingType.Low, at, atr))
            .OfType<BoundaryEvidence>()
            .OrderByDescending(evidence => evidence.LastTouch)
            .ThenByDescending(evidence => evidence.Line.FitScore)
            .ToArray();

        BoundaryEvidence[] resistances = trendlines
            .Where(line => line.Type == TrendlineType.Resistance)
            .Select(line => BuildEvidence(line, orderedSwings, SwingType.High, at, atr))
            .OfType<BoundaryEvidence>()
            .OrderByDescending(evidence => evidence.LastTouch)
            .ThenByDescending(evidence => evidence.Line.FitScore)
            .ToArray();

        var candidates = new List<ChannelCandidate>();

        // Primary path: force a parallel opposite boundary from each fitted line.
        // This is the correct geometric model for price channels and does not
        // require independently fitted support/resistance slopes to match.
        foreach (BoundaryEvidence support in supports)
        {
            if (TryBuildParallelFromPrimary(
                    support,
                    orderedSwings,
                    primaryIsLower: true,
                    at,
                    atr,
                    currentPrice,
                    expectedDirection,
                    out ChannelCandidate? candidate) &&
                candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        foreach (BoundaryEvidence resistance in resistances)
        {
            if (TryBuildParallelFromPrimary(
                    resistance,
                    orderedSwings,
                    primaryIsLower: false,
                    at,
                    atr,
                    currentPrice,
                    expectedDirection,
                    out ChannelCandidate? candidate) &&
                candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        // Secondary path: accept pre-fitted support/resistance pairs that are
        // already near-parallel (e.g. synthetic or clean market regimes).
        foreach (BoundaryEvidence lowerEvidence in supports)
        {
            foreach (BoundaryEvidence upperEvidence in resistances)
            {
                TryAddPairedCandidate(
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

    private bool TryBuildParallelFromPrimary(
        BoundaryEvidence primaryEvidence,
        IReadOnlyList<SwingPoint> orderedSwings,
        bool primaryIsLower,
        DateTimeOffset at,
        decimal atr,
        decimal? currentPrice,
        MarketStructureDirection expectedDirection,
        out ChannelCandidate? candidate)
    {
        candidate = null;
        Trendline primary = primaryEvidence.Line;
        DateTimeOffset startTime = primary.StartTime;
        if (startTime >= at)
        {
            return false;
        }

        SwingType oppositeType = primaryIsLower ? SwingType.High : SwingType.Low;
        decimal touchTolerance = atr * _options.TouchToleranceAtr;
        decimal minWidth = atr * _options.MinimumWidthAtr;
        decimal maxWidth = atr * _options.MaximumWidthAtr;

        SwingPoint[] oppositeSwings = orderedSwings
            .Where(swing =>
                swing.Type == oppositeType &&
                swing.PivotTime >= startTime &&
                swing.PivotTime <= at)
            .ToArray();

        if (oppositeSwings.Length < _options.MinimumTouchesPerBoundary)
        {
            return false;
        }

        // Residual of each opposite pivot relative to the primary line.
        // For a lower (support) primary we want positive residuals; for an upper
        // (resistance) primary we want negative residuals that become the width.
        var residualPoints = new List<(SwingPoint Swing, decimal Residual, decimal Width)>(
            oppositeSwings.Length);
        foreach (SwingPoint swing in oppositeSwings)
        {
            decimal residual = swing.Price - primary.PriceAt(swing.PivotTime);
            decimal width = primaryIsLower ? residual : -residual;
            if (width < minWidth || width > maxWidth)
            {
                continue;
            }

            residualPoints.Add((swing, residual, width));
        }

        if (residualPoints.Count < _options.MinimumTouchesPerBoundary)
        {
            return false;
        }

        // Search candidate parallel offsets by using each residual as a seed and
        // counting how many opposite pivots sit near that parallel copy.
        ParallelOffset? best = null;
        foreach ((_, decimal seedResidual, _) in residualPoints)
        {
            List<(SwingPoint Swing, decimal Residual, decimal Width)> inliers = residualPoints
                .Where(point => Math.Abs(point.Residual - seedResidual) <= touchTolerance)
                .ToList();

            if (inliers.Count < _options.MinimumTouchesPerBoundary)
            {
                continue;
            }

            decimal medianResidual = Median(inliers.Select(point => point.Residual));
            // Re-collect against the median for a stable consensus offset.
            inliers = residualPoints
                .Where(point => Math.Abs(point.Residual - medianResidual) <= touchTolerance)
                .ToList();
            if (inliers.Count < _options.MinimumTouchesPerBoundary)
            {
                continue;
            }

            medianResidual = Median(inliers.Select(point => point.Residual));
            decimal medianWidth = primaryIsLower ? medianResidual : -medianResidual;
            if (medianWidth < minWidth || medianWidth > maxWidth)
            {
                continue;
            }

            decimal meanError = inliers.Average(point =>
                Math.Abs(point.Residual - medianResidual));
            DateTimeOffset lastOppositeTouch = inliers.Max(point => point.Swing.PivotTime);
            var offset = new ParallelOffset(
                medianResidual,
                medianWidth,
                meanError,
                inliers.Select(point => point.Swing).ToArray(),
                lastOppositeTouch);

            if (best is null ||
                offset.Touches.Length > best.Touches.Length ||
                offset.Touches.Length == best.Touches.Length && offset.MeanError < best.MeanError ||
                offset.Touches.Length == best.Touches.Length &&
                offset.MeanError == best.MeanError &&
                offset.LastTouch > best.LastTouch)
            {
                best = offset;
            }
        }

        if (best is null)
        {
            return false;
        }

        Trendline opposite = BuildParallelLine(
            primary,
            best.Residual,
            primaryIsLower ? TrendlineType.Resistance : TrendlineType.Support,
            best.Touches,
            best.MeanError);

        Trendline lower = primaryIsLower ? primary : opposite;
        Trendline upper = primaryIsLower ? opposite : primary;
        SwingPoint[] lowerTouches = primaryIsLower
            ? primaryEvidence.Touches
            : best.Touches;
        SwingPoint[] upperTouches = primaryIsLower
            ? best.Touches
            : primaryEvidence.Touches;

        if (!TryFinalizeChannel(
                lower,
                upper,
                lowerTouches,
                upperTouches,
                orderedSwings,
                startTime,
                at,
                atr,
                currentPrice,
                expectedDirection,
                enforceParallelismGates: false,
                out candidate) ||
            candidate is null)
        {
            return false;
        }

        return true;
    }

    private bool TryAddPairedCandidate(
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
            return false;
        }

        SwingPoint[] lowerTouches = lowerEvidence.Touches
            .Where(touch => touch.PivotTime >= startTime)
            .ToArray();
        SwingPoint[] upperTouches = upperEvidence.Touches
            .Where(touch => touch.PivotTime >= startTime)
            .ToArray();

        // Within the common lifetime require at least one observed touch on each
        // side; each line already satisfied the configured minimum on its own.
        if (lowerTouches.Length == 0 || upperTouches.Length == 0)
        {
            return false;
        }

        if (!TryFinalizeChannel(
                lower,
                upper,
                lowerTouches,
                upperTouches,
                orderedSwings,
                startTime,
                at,
                atr,
                currentPrice,
                expectedDirection,
                enforceParallelismGates: true,
                out ChannelCandidate? candidate) ||
            candidate is null)
        {
            return false;
        }

        candidates.Add(candidate);
        return true;
    }

    private bool TryFinalizeChannel(
        Trendline lower,
        Trendline upper,
        SwingPoint[] lowerTouches,
        SwingPoint[] upperTouches,
        IReadOnlyList<SwingPoint> orderedSwings,
        DateTimeOffset startTime,
        DateTimeOffset at,
        decimal atr,
        decimal? currentPrice,
        MarketStructureDirection expectedDirection,
        bool enforceParallelismGates,
        out ChannelCandidate? candidate)
    {
        candidate = null;

        // Touches may not be recent-first (parallel opposite side). Use max time.
        DateTimeOffset lastTouch = MaxTime(
            lowerTouches.Max(touch => touch.PivotTime),
            upperTouches.Max(touch => touch.PivotTime));

        int recentSwingAge = orderedSwings.Count(swing => swing.PivotTime > lastTouch);
        if (recentSwingAge > _options.MaximumRecentSwingAge)
        {
            return false;
        }

        decimal spanSeconds = (decimal)(at - startTime).TotalSeconds;
        if (spanSeconds <= 0m)
        {
            return false;
        }

        decimal boundaryDriftAtr =
            Math.Abs(lower.SlopePerSecond - upper.SlopePerSecond) *
            spanSeconds /
            atr;

        if (enforceParallelismGates &&
            boundaryDriftAtr > _options.MaximumBoundaryDriftAtr)
        {
            return false;
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
            return false;
        }

        decimal minimumWidth = Math.Min(startWidth, Math.Min(middleWidth, currentWidth));
        decimal maximumWidth = Math.Max(startWidth, Math.Max(middleWidth, currentWidth));

        if (minimumWidth / atr < _options.MinimumWidthAtr ||
            maximumWidth / atr > _options.MaximumWidthAtr)
        {
            return false;
        }

        decimal averageWidth = (startWidth + middleWidth + currentWidth) / 3m;
        decimal widthChangeRatio = (maximumWidth - minimumWidth) / averageWidth;
        if (enforceParallelismGates &&
            widthChangeRatio > _options.MaximumWidthChangeRatio)
        {
            return false;
        }

        decimal averageSlope = (lower.SlopePerSecond + upper.SlopePerSecond) / 2m;
        ChannelDirection direction = DetermineDirection(averageSlope, spanSeconds, atr);
        if (!MatchesExpectedDirection(direction, expectedDirection))
        {
            return false;
        }

        SwingPoint[] structuralSwings = orderedSwings
            .Where(swing => swing.PivotTime >= startTime)
            .ToArray();

        if (structuralSwings.Length < _options.MinimumTouchesPerBoundary * 2)
        {
            return false;
        }

        int breakViolations = CountBreakViolations(structuralSwings, lower, upper, atr);
        decimal breakViolationRatio =
            (decimal)breakViolations / structuralSwings.Length;

        if (breakViolations > _options.MaximumBreakViolations ||
            breakViolationRatio > _options.MaximumBreakViolationRatio)
        {
            return false;
        }

        if (currentPrice is decimal latestPrice)
        {
            decimal breakTolerance = atr * _options.BreakToleranceAtr;
            if (latestPrice < currentLower - breakTolerance ||
                latestPrice > currentUpper + breakTolerance)
            {
                return false;
            }
        }

        decimal confidence = CalculateConfidence(
            lower,
            upper,
            lowerTouches.Length,
            upperTouches.Length,
            recentSwingAge,
            structuralSwings.Length,
            enforceParallelismGates ? boundaryDriftAtr : 0m,
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

        candidate = new ChannelCandidate(
            channel,
            lastTouch,
            currentLower,
            currentUpper);
        return true;
    }

    private static Trendline BuildParallelLine(
        Trendline primary,
        decimal residualOffset,
        TrendlineType type,
        IReadOnlyList<SwingPoint> touches,
        decimal meanError)
    {
        DateTimeOffset startTime = touches.Min(touch => touch.PivotTime);
        if (primary.StartTime < startTime)
        {
            // Keep the channel pair sharing the primary's established span when the
            // opposite side only has later touches.
            startTime = primary.StartTime;
        }

        DateTimeOffset endTime = touches.Max(touch => touch.PivotTime);
        if (primary.EndTime > endTime)
        {
            endTime = primary.EndTime;
        }

        // Same origin/slope as primary, shifted by residual: a true parallel copy.
        decimal originPrice = primary.OriginPrice + residualOffset;
        decimal fitScore = Math.Clamp(
            40m + touches.Count * 12m - meanError * 20m,
            0m,
            100m);

        return new Trendline
        {
            StartTime = startTime,
            EndTime = endTime,
            OriginTime = primary.OriginTime,
            OriginPrice = originPrice,
            SlopePerSecond = primary.SlopePerSecond,
            InlierCount = touches.Count,
            MeanAbsoluteError = meanError,
            FitScore = fitScore,
            Type = type
        };
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
        decimal driftPenalty = _options.MaximumBoundaryDriftAtr <= 0m
            ? 0m
            : boundaryDriftAtr / _options.MaximumBoundaryDriftAtr * 15m;
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

    private static DateTimeOffset MaxTime(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static decimal Median(IEnumerable<decimal> values)
    {
        decimal[] ordered = values.OrderBy(value => value).ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2m;
    }

    private sealed record BoundaryEvidence(
        Trendline Line,
        SwingPoint[] Touches,
        DateTimeOffset LastTouch);

    private sealed record ParallelOffset(
        decimal Residual,
        decimal Width,
        decimal MeanError,
        SwingPoint[] Touches,
        DateTimeOffset LastTouch);

    private sealed record ChannelCandidate(
        PriceChannel Channel,
        DateTimeOffset LastTouch,
        decimal CurrentLowerPrice,
        decimal CurrentUpperPrice);
}
