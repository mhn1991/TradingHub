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
    private readonly DbscanOptions _options;

    public SupportResistanceDetector(DbscanOptions? options = null)
    {
        _options = options ?? new DbscanOptions();
        if (_options.EpsilonAtr <= 0m || _options.MinimumPoints < 1 || _options.MaximumPivots < 1)
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

        SwingPoint[] points = swings
            .TakeLast(_options.MaximumPivots)
            .OrderBy(point => point.Price)
            .ToArray();

        decimal epsilon = atr * _options.EpsilonAtr;
        int[] labels = Enumerable.Repeat(-99, points.Length).ToArray();
        int clusterId = 0;

        for (int index = 0; index < points.Length; index++)
        {
            if (labels[index] != -99)
            {
                continue;
            }

            List<int> neighbours = Region(points, index, epsilon);
            if (neighbours.Count < _options.MinimumPoints)
            {
                labels[index] = -1;
                continue;
            }

            Expand(points, labels, index, neighbours, clusterId, epsilon);
            clusterId++;
        }

        var zones = new List<PriceZone>(clusterId);
        for (int id = 0; id < clusterId; id++)
        {
            SwingPoint[] cluster = points
                .Where((_, index) => labels[index] == id)
                .ToArray();

            int highs = cluster.Count(point => point.Type == SwingType.High);
            int lows = cluster.Length - highs;
            decimal centre = cluster.Average(point => point.Price);
            decimal recencyWeight = cluster
                .Select((_, index) => 1m + ((decimal)index / Math.Max(1, cluster.Length - 1)))
                .Sum();

            zones.Add(new PriceZone
            {
                LowerPrice = cluster.Min(point => point.Price),
                UpperPrice = cluster.Max(point => point.Price),
                CentrePrice = centre,
                TouchCount = cluster.Length,
                Strength = Math.Min(100m, cluster.Length * 12m + recencyWeight),
                Type = highs == 0
                    ? PriceZoneType.Support
                    : lows == 0
                        ? PriceZoneType.Resistance
                        : PriceZoneType.Mixed
            });
        }

        return zones
            .OrderByDescending(zone => zone.Strength)
            .ThenBy(zone => zone.CentrePrice)
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
            if (labels[current] == -1)
            {
                labels[current] = clusterId;
            }

            if (labels[current] != -99)
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

    private static List<int> Region(SwingPoint[] points, int index, decimal epsilon)
    {
        var result = new List<int>();
        for (int candidate = 0; candidate < points.Length; candidate++)
        {
            if (Math.Abs(points[candidate].Price - points[index].Price) <= epsilon)
            {
                result.Add(candidate);
            }
        }

        return result;
    }
}
