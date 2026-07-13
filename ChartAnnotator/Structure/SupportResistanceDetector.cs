using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record DbscanOptions(
    decimal EpsilonAtr = 0.35m,
    int MinimumPoints = 2,
    int MaximumPivots = 500);

/// <summary>
/// One-dimensional DBSCAN over confirmed swing prices. Complexity is O(n²),
/// intentionally bounded by MaximumPivots and normally run only when pivots change.
/// </summary>
public sealed class SupportResistanceDetector
{
    private const int Unvisited = -99;
    private const int Noise = -1;
    private readonly DbscanOptions _options;

    public SupportResistanceDetector(DbscanOptions? options = null)
    {
        _options = options ?? new DbscanOptions();
        if (_options.EpsilonAtr <= 0m ||
            _options.MinimumPoints < 1 ||
            _options.MaximumPivots < _options.MinimumPoints)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public IReadOnlyList<PriceZone> Detect(
        IReadOnlyList<SwingPoint> swings,
        decimal atr)
    {
        ArgumentNullException.ThrowIfNull(swings);
        if (swings.Count == 0 || atr <= 0m)
        {
            return [];
        }

        // Keep a chronological copy for recency scoring. DBSCAN itself works on a
        // price-sorted copy, but price order must never be mistaken for time order.
        SwingPoint[] chronological = swings
            .OrderBy(point => point.PivotTime)
            .ThenBy(point => point.ConfirmedAt)
            .TakeLast(_options.MaximumPivots)
            .ToArray();
        SwingPoint[] points = chronological
            .OrderBy(point => point.Price)
            .ThenBy(point => point.PivotTime)
            .ToArray();

        decimal epsilon = atr * _options.EpsilonAtr;
        int[] labels = Enumerable.Repeat(Unvisited, points.Length).ToArray();
        int clusterId = 0;

        for (int index = 0; index < points.Length; index++)
        {
            if (labels[index] != Unvisited)
            {
                continue;
            }

            List<int> neighbours = Region(points, index, epsilon);
            if (neighbours.Count < _options.MinimumPoints)
            {
                labels[index] = Noise;
                continue;
            }

            Expand(points, labels, index, neighbours, clusterId, epsilon);
            clusterId++;
        }

        var zones = new List<ZoneCandidate>(clusterId);
        for (int id = 0; id < clusterId; id++)
        {
            SwingPoint[] cluster = points
                .Where((_, index) => labels[index] == id)
                .ToArray();
            if (cluster.Length == 0)
            {
                continue;
            }

            int highs = cluster.Count(point => point.Type == SwingType.High);
            int lows = cluster.Length - highs;
            decimal lower = cluster.Min(point => point.Price);
            decimal upper = cluster.Max(point => point.Price);
            decimal centre = Median(cluster.Select(point => point.Price));
            DateTimeOffset lastTouch = cluster.Max(point => point.PivotTime);
            int lastTouchIndex = Array.FindLastIndex(
                chronological,
                point => point.PivotTime <= lastTouch);
            decimal recency = chronological.Length <= 1
                ? 1m
                : (decimal)Math.Max(lastTouchIndex, 0) /
                  (chronological.Length - 1);
            decimal compactness = 1m - Math.Min(
                1m,
                (upper - lower) / Math.Max(epsilon * 2m, 0.0000000001m));
            decimal purity =
                (decimal)Math.Max(highs, lows) / cluster.Length;
            decimal strength = Math.Clamp(
                Math.Min(60m, cluster.Length * 12m) +
                recency * 20m +
                compactness * 10m +
                purity * 10m,
                0m,
                100m);

            zones.Add(new ZoneCandidate(
                new PriceZone
                {
                    LowerPrice = lower,
                    UpperPrice = upper,
                    CentrePrice = centre,
                    TouchCount = cluster.Length,
                    Strength = strength,
                    Type = highs == 0
                        ? PriceZoneType.Support
                        : lows == 0
                            ? PriceZoneType.Resistance
                            : PriceZoneType.Mixed
                },
                lastTouch));
        }

        return zones
            .OrderByDescending(candidate => candidate.Zone.Strength)
            .ThenByDescending(candidate => candidate.LastTouch)
            .ThenBy(candidate => candidate.Zone.CentrePrice)
            .Select(candidate => candidate.Zone)
            .ToArray();
    }

    private void Expand(
        SwingPoint[] points,
        int[] labels,
        int seed,
        List<int> neighbours,
        int clusterId,
        decimal epsilon)
    {
        labels[seed] = clusterId;
        var queue = new Queue<int>(neighbours);
        var queued = new HashSet<int>(neighbours);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (labels[current] == Noise)
            {
                labels[current] = clusterId;
            }

            if (labels[current] != Unvisited)
            {
                continue;
            }

            labels[current] = clusterId;
            List<int> currentNeighbours = Region(points, current, epsilon);
            if (currentNeighbours.Count < _options.MinimumPoints)
            {
                continue;
            }

            foreach (int neighbour in currentNeighbours)
            {
                if (queued.Add(neighbour))
                {
                    queue.Enqueue(neighbour);
                }
            }
        }
    }

    private static List<int> Region(
        IReadOnlyList<SwingPoint> points,
        int index,
        decimal epsilon)
    {
        var result = new List<int>();
        for (int candidate = 0; candidate < points.Count; candidate++)
        {
            if (Math.Abs(points[candidate].Price - points[index].Price) <= epsilon)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        decimal[] ordered = values.OrderBy(value => value).ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2m;
    }

    private sealed record ZoneCandidate(
        PriceZone Zone,
        DateTimeOffset LastTouch);
}
