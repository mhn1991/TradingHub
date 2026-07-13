using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record RansacOptions(
    int MaximumIterations = 200,
    decimal DistanceThresholdAtr = 0.25m,
    int MinimumInliers = 3,
    int MaximumPivots = 300,
    int RandomSeed = 12_345,
    int MaximumLinesPerType = 4,
    int RecentAnchorPivots = 16,
    int MaximumEndPivotAge = 12,
    decimal MaximumViolationRatio = 0.20m);

/// <summary>
/// Finds support lines from confirmed swing lows and resistance lines from confirmed
/// swing highs. Candidate pairs are evaluated deterministically from recent pivots
/// backwards so unchanged input cannot produce a different set of lines.
/// </summary>
public sealed class RansacTrendlineDetector
{
    private readonly RansacOptions _options;

    public RansacTrendlineDetector(RansacOptions? options = null)
    {
        _options = options ?? new RansacOptions();
        if (_options.MaximumIterations < 1 ||
            _options.DistanceThresholdAtr <= 0m ||
            _options.MinimumInliers < 2 ||
            _options.MaximumPivots < _options.MinimumInliers ||
            _options.MaximumLinesPerType < 1 ||
            _options.RecentAnchorPivots < 1 ||
            _options.MaximumEndPivotAge < 0 ||
            _options.MaximumViolationRatio is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public IReadOnlyList<Trendline> Detect(
        IReadOnlyList<SwingPoint> swings,
        decimal atr,
        long version = 0,
        MarketStructureDirection direction = MarketStructureDirection.Unknown)
    {
        ArgumentNullException.ThrowIfNull(swings);
        if (atr <= 0m)
        {
            return [];
        }

        // Kept in the public signature for backwards compatibility. Candidate
        // selection intentionally does not depend on engine version because doing
        // so made unchanged trendlines jump whenever periodic analysis ran.
        _ = version;
        _ = _options.RandomSeed;

        SwingPoint[] ordered = swings
            .OrderBy(point => point.PivotTime)
            .ThenBy(point => point.ConfirmedAt)
            .ToArray();

        var result = new List<Trendline>(_options.MaximumLinesPerType * 2);
        result.AddRange(DetectMany(
            ordered
                .Where(point => point.Type == SwingType.Low)
                .TakeLast(_options.MaximumPivots)
                .ToArray(),
            TrendlineType.Support,
            atr,
            direction));
        result.AddRange(DetectMany(
            ordered
                .Where(point => point.Type == SwingType.High)
                .TakeLast(_options.MaximumPivots)
                .ToArray(),
            TrendlineType.Resistance,
            atr,
            direction));

        // Active lines are returned first. This also makes downstream recent-first
        // channel discovery independent of the original swing collection ordering.
        return result
            .OrderByDescending(line => line.EndTime)
            .ThenByDescending(line => line.FitScore)
            .ThenBy(line => line.Type)
            .ToArray();
    }

    private IReadOnlyList<Trendline> DetectMany(
        SwingPoint[] points,
        TrendlineType type,
        decimal atr,
        MarketStructureDirection direction)
    {
        if (points.Length < _options.MinimumInliers)
        {
            return [];
        }

        decimal threshold = atr * _options.DistanceThresholdAtr;
        var remaining = new List<SwingPoint>(points);
        var lines = new List<Trendline>(_options.MaximumLinesPerType);

        while (lines.Count < _options.MaximumLinesPerType &&
               remaining.Count >= _options.MinimumInliers)
        {
            Candidate? best = FindBest(
                remaining,
                points,
                type,
                threshold,
                direction);
            if (best is null)
            {
                break;
            }

            lines.Add(new Trendline
            {
                StartTime = best.StartTime,
                EndTime = best.EndTime,
                OriginTime = best.OriginTime,
                OriginPrice = best.OriginPrice,
                SlopePerSecond = best.Slope,
                InlierCount = best.Inliers.Count,
                MeanAbsoluteError = best.MeanError,
                FitScore = best.Quality,
                Type = type
            });

            // Removing the selected consensus set allows older or distinct regimes
            // to be found without returning several near-identical versions of the
            // same line.
            var usedPivots = new HashSet<SwingPoint>(best.Inliers);
            remaining.RemoveAll(usedPivots.Contains);
        }

        return lines;
    }

    private Candidate? FindBest(
        IReadOnlyList<SwingPoint> points,
        IReadOnlyList<SwingPoint> recencyReference,
        TrendlineType type,
        decimal threshold,
        MarketStructureDirection direction)
    {
        int lastIndex = points.Count - 1;
        int firstAnchor = Math.Max(1, points.Count - _options.RecentAnchorPivots);
        Candidate? best = null;
        int evaluated = 0;

        // Expand backwards by gap while cycling through recent anchors. This tests
        // the newest pivots first but does not spend the whole budget on only the
        // single latest pivot when that pivot is an outlier.
        for (int gap = 1;
             gap < points.Count && evaluated < _options.MaximumIterations;
             gap++)
        {
            for (int anchorIndex = lastIndex;
                 anchorIndex >= firstAnchor && evaluated < _options.MaximumIterations;
                 anchorIndex--)
            {
                int olderIndex = anchorIndex - gap;
                if (olderIndex < 0)
                {
                    continue;
                }

                evaluated++;
                SwingPoint older = points[olderIndex];
                SwingPoint anchor = points[anchorIndex];
                decimal seconds =
                    (decimal)(anchor.PivotTime - older.PivotTime).TotalSeconds;
                if (seconds <= 0m)
                {
                    continue;
                }

                decimal slope = (anchor.Price - older.Price) / seconds;
                if (!SlopeMatchesDirection(slope, direction))
                {
                    continue;
                }

                Candidate? candidate = BuildCandidate(
                    points,
                    recencyReference,
                    older.PivotTime,
                    older.Price,
                    slope,
                    type,
                    threshold);
                if (candidate is null ||
                    !SlopeMatchesDirection(candidate.Slope, direction))
                {
                    continue;
                }

                if (best is null || IsBetter(candidate, best))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private Candidate? BuildCandidate(
        IReadOnlyList<SwingPoint> fitPoints,
        IReadOnlyList<SwingPoint> recencyReference,
        DateTimeOffset originTime,
        decimal originPrice,
        decimal initialSlope,
        TrendlineType type,
        decimal threshold)
    {
        SwingPoint[] initialInliers = FindInliers(
            fitPoints,
            originTime,
            originPrice,
            initialSlope,
            threshold);
        if (initialInliers.Length < _options.MinimumInliers)
        {
            return null;
        }

        FittedLine firstFit = Refit(initialInliers);
        SwingPoint[] refinedInliers = FindInliers(
            fitPoints,
            firstFit.OriginTime,
            firstFit.OriginPrice,
            firstFit.Slope,
            threshold);
        if (refinedInliers.Length < _options.MinimumInliers)
        {
            return null;
        }

        FittedLine fit = Refit(refinedInliers);
        SwingPoint[] finalInliers = FindInliers(
            fitPoints,
            fit.OriginTime,
            fit.OriginPrice,
            fit.Slope,
            threshold);
        if (finalInliers.Length < _options.MinimumInliers)
        {
            return null;
        }

        // Always refit and verify the final consensus. Membership can change without
        // its count changing, so comparing only array lengths is not sufficient.
        fit = Refit(finalInliers);
        SwingPoint[] stabilizedInliers = FindInliers(
            fitPoints,
            fit.OriginTime,
            fit.OriginPrice,
            fit.Slope,
            threshold);
        if (stabilizedInliers.Length < _options.MinimumInliers)
        {
            return null;
        }

        if (!finalInliers.SequenceEqual(stabilizedInliers))
        {
            finalInliers = stabilizedInliers;
            fit = Refit(finalInliers);
        }

        DateTimeOffset startTime = finalInliers.Min(point => point.PivotTime);
        DateTimeOffset endTime = finalInliers.Max(point => point.PivotTime);
        int endPivotAge = recencyReference.Count(
            point => point.PivotTime > endTime);
        if (endPivotAge > _options.MaximumEndPivotAge)
        {
            return null;
        }

        SwingPoint[] relevantPoints = recencyReference
            .Where(point => point.PivotTime >= startTime)
            .ToArray();
        int violations = CountBoundaryViolations(
            relevantPoints,
            fit,
            type,
            threshold);
        decimal violationRatio = relevantPoints.Length == 0
            ? 0m
            : (decimal)violations / relevantPoints.Length;
        if (violationRatio > _options.MaximumViolationRatio)
        {
            return null;
        }

        decimal meanError = finalInliers
            .Average(point => Math.Abs(point.Price - PriceAt(fit, point.PivotTime)));
        int spanPivotCount = recencyReference.Count(point =>
            point.PivotTime >= startTime && point.PivotTime <= endTime);
        decimal quality = CalculateQuality(
            finalInliers.Length,
            recencyReference.Count,
            meanError,
            threshold,
            endPivotAge,
            spanPivotCount,
            violationRatio);

        return new Candidate(
            fit.OriginTime,
            fit.OriginPrice,
            fit.Slope,
            meanError,
            startTime,
            endTime,
            finalInliers,
            endPivotAge,
            violationRatio,
            quality);
    }

    private static SwingPoint[] FindInliers(
        IReadOnlyList<SwingPoint> points,
        DateTimeOffset originTime,
        decimal originPrice,
        decimal slope,
        decimal threshold) => points
        .Where(point =>
        {
            decimal prediction = originPrice +
                slope * (decimal)(point.PivotTime - originTime).TotalSeconds;
            return Math.Abs(point.Price - prediction) <= threshold;
        })
        .ToArray();

    private static int CountBoundaryViolations(
        IReadOnlyList<SwingPoint> points,
        FittedLine line,
        TrendlineType type,
        decimal threshold)
    {
        int violations = 0;
        foreach (SwingPoint point in points)
        {
            decimal prediction = PriceAt(line, point.PivotTime);
            bool violated = type switch
            {
                TrendlineType.Support => point.Price < prediction - threshold,
                TrendlineType.Resistance => point.Price > prediction + threshold,
                _ => false
            };

            if (violated)
            {
                violations++;
            }
        }

        return violations;
    }

    private decimal CalculateQuality(
        int inlierCount,
        int totalPointCount,
        decimal meanError,
        decimal threshold,
        int endPivotAge,
        int spanPivotCount,
        decimal violationRatio)
    {
        decimal inlierScore = Math.Min(
            35m,
            (decimal)inlierCount / Math.Max(_options.MinimumInliers, 1) * 15m);
        decimal fitScore =
            (1m - Math.Min(1m, meanError / threshold)) * 30m;
        decimal recencyScore = Math.Clamp(
            1m - (decimal)endPivotAge / (_options.MaximumEndPivotAge + 1m),
            0m,
            1m) * 25m;
        decimal coverageScore =
            (decimal)inlierCount / Math.Max(totalPointCount, 1) * 5m;
        decimal spanScore = Math.Min(
            5m,
            (decimal)spanPivotCount / Math.Max(_options.MinimumInliers * 3, 1) * 5m);
        decimal violationPenalty = _options.MaximumViolationRatio == 0m
            ? (violationRatio > 0m ? 20m : 0m)
            : violationRatio / _options.MaximumViolationRatio * 20m;

        return Math.Clamp(
            inlierScore +
            fitScore +
            recencyScore +
            coverageScore +
            spanScore -
            violationPenalty,
            0m,
            100m);
    }

    private static bool IsBetter(Candidate candidate, Candidate best) =>
        candidate.Quality > best.Quality ||
        candidate.Quality == best.Quality && candidate.EndTime > best.EndTime ||
        candidate.Quality == best.Quality && candidate.EndTime == best.EndTime &&
        candidate.Inliers.Count > best.Inliers.Count ||
        candidate.Quality == best.Quality && candidate.EndTime == best.EndTime &&
        candidate.Inliers.Count == best.Inliers.Count && candidate.MeanError < best.MeanError;

    private static bool SlopeMatchesDirection(
        decimal slope,
        MarketStructureDirection direction) => direction switch
        {
            MarketStructureDirection.Rising => slope >= 0m,
            MarketStructureDirection.Falling => slope <= 0m,
            _ => true
        };

    private static FittedLine Refit(IReadOnlyList<SwingPoint> points)
    {
        DateTimeOffset originTime = points.Min(point => point.PivotTime);
        decimal[] x = points
            .Select(point =>
                (decimal)(point.PivotTime - originTime).TotalSeconds)
            .ToArray();
        decimal[] y = points.Select(point => point.Price).ToArray();
        decimal meanX = x.Average();
        decimal meanY = y.Average();
        decimal numerator = 0m;
        decimal denominator = 0m;

        for (int index = 0; index < x.Length; index++)
        {
            decimal dx = x[index] - meanX;
            numerator += dx * (y[index] - meanY);
            denominator += dx * dx;
        }

        decimal slope = denominator == 0m ? 0m : numerator / denominator;
        decimal intercept = meanY - slope * meanX;
        return new FittedLine(originTime, intercept, slope);
    }

    private static decimal PriceAt(FittedLine line, DateTimeOffset time) =>
        line.OriginPrice +
        line.Slope * (decimal)(time - line.OriginTime).TotalSeconds;

    private readonly record struct FittedLine(
        DateTimeOffset OriginTime,
        decimal OriginPrice,
        decimal Slope);

    private sealed record Candidate(
        DateTimeOffset OriginTime,
        decimal OriginPrice,
        decimal Slope,
        decimal MeanError,
        DateTimeOffset StartTime,
        DateTimeOffset EndTime,
        IReadOnlyList<SwingPoint> Inliers,
        int EndPivotAge,
        decimal ViolationRatio,
        decimal Quality);
}
