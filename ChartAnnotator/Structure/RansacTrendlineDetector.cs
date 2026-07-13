using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record RansacOptions(
    int MaximumIterations = 200,
    decimal DistanceThresholdAtr = 0.25m,
    int MinimumInliers = 3,
    int MaximumPivots = 300,
    int RandomSeed = 12_345);

public sealed class RansacTrendlineDetector
{
    private readonly RansacOptions _options;

    public RansacTrendlineDetector(RansacOptions? options = null)
    {
        _options = options ?? new RansacOptions();
        if (_options.MaximumIterations < 1 ||
            _options.DistanceThresholdAtr <= 0m ||
            _options.MinimumInliers < 2 ||
            _options.MaximumPivots < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public IReadOnlyList<Trendline> Detect(
        IReadOnlyList<SwingPoint> swings,
        decimal atr,
        long version = 0)
    {
        if (atr <= 0m)
        {
            return [];
        }

        var result = new List<Trendline>(2);
        Trendline? support = DetectOne(
            swings.Where(point => point.Type == SwingType.Low).TakeLast(_options.MaximumPivots).ToArray(),
            TrendlineType.Support,
            atr,
            version);

        Trendline? resistance = DetectOne(
            swings.Where(point => point.Type == SwingType.High).TakeLast(_options.MaximumPivots).ToArray(),
            TrendlineType.Resistance,
            atr,
            version + 17);

        if (support is not null)
        {
            result.Add(support);
        }

        if (resistance is not null)
        {
            result.Add(resistance);
        }

        return result;
    }

    private Trendline? DetectOne(
        SwingPoint[] points,
        TrendlineType type,
        decimal atr,
        long version)
    {
        if (points.Length < _options.MinimumInliers)
        {
            return null;
        }

        decimal threshold = atr * _options.DistanceThresholdAtr;
        var random = new Random(unchecked(_options.RandomSeed + (int)(version % int.MaxValue)));
        Candidate? best = null;

        for (int iteration = 0; iteration < _options.MaximumIterations; iteration++)
        {
            int first = random.Next(points.Length);
            int second = random.Next(points.Length - 1);
            if (second >= first)
            {
                second++;
            }

            SwingPoint a = points[first];
            SwingPoint b = points[second];
            decimal seconds = (decimal)(b.PivotTime - a.PivotTime).TotalSeconds;
            if (seconds == 0m)
            {
                continue;
            }

            decimal slope = (b.Price - a.Price) / seconds;
            var inliers = new List<SwingPoint>();
            decimal error = 0m;

            foreach (SwingPoint point in points)
            {
                decimal prediction = a.Price + slope * (decimal)(point.PivotTime - a.PivotTime).TotalSeconds;
                decimal residual = Math.Abs(point.Price - prediction);
                if (residual <= threshold)
                {
                    inliers.Add(point);
                    error += residual;
                }
            }

            if (inliers.Count < _options.MinimumInliers)
            {
                continue;
            }

            decimal meanError = error / inliers.Count;
            Candidate candidate = Refit(inliers, type, meanError, threshold);
            if (best is null ||
                candidate.InlierCount > best.InlierCount ||
                candidate.InlierCount == best.InlierCount && candidate.MeanError < best.MeanError)
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return null;
        }

        return new Trendline
        {
            OriginTime = best.OriginTime,
            OriginPrice = best.OriginPrice,
            SlopePerSecond = best.Slope,
            InlierCount = best.InlierCount,
            MeanAbsoluteError = best.MeanError,
            FitScore = Math.Clamp(
                (best.InlierCount * 15m) + (1m - Math.Min(1m, best.MeanError / threshold)) * 40m,
                0m,
                100m),
            Type = type
        };
    }

    private static Candidate Refit(
        IReadOnlyList<SwingPoint> points,
        TrendlineType type,
        decimal fallbackError,
        decimal threshold)
    {
        DateTimeOffset originTime = points.Min(point => point.PivotTime);
        decimal[] x = points
            .Select(point => (decimal)(point.PivotTime - originTime).TotalSeconds)
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
        decimal error = points.Count == 0
            ? fallbackError
            : points.Select((point, index) => Math.Abs(y[index] - (intercept + slope * x[index]))).Average();

        return new Candidate(
            originTime,
            intercept,
            slope,
            points.Count,
            Math.Min(error, threshold));
    }

    private sealed record Candidate(
        DateTimeOffset OriginTime,
        decimal OriginPrice,
        decimal Slope,
        int InlierCount,
        decimal MeanError);
}
