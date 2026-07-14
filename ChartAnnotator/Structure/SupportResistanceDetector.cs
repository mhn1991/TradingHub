using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record DbscanOptions(
    decimal EpsilonAtr = 0.30m,
    int MinimumPoints = 2,
    int MaximumPivots = 500,

    // When consecutive touches in a price cluster are separated by more than this
    // many intervening swings, the cluster is split into separate temporal zones.
    // This stops a decade-old low and a fresh low from forming one "active" level.
    int MaximumTemporalGapSwings = 30,

    // How many of the newest touches dominate Support/Resistance role typing when
    // a cluster contains both highs and lows.
    int RecentRoleTouchCount = 3,

    // Distance beyond a zone edge (in ATR) that counts as a decisive break.
    decimal BreakToleranceAtr = 0.15m,

    // Zones farther than this from the latest close are treated as inactive.
    decimal MaximumActiveDistanceAtr = 8m,

    // Drop weak / broken zones below this strength after scoring.
    decimal MinimumStrength = 28m,

    int MaximumZones = 16);

/// <summary>
/// Builds horizontal support/resistance zones by clustering confirmed swing prices
/// (1D DBSCAN), then refining clusters with temporal splits, role polarity, and
/// optional invalidation against the latest close.
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
            _options.MaximumPivots < _options.MinimumPoints ||
            _options.MaximumTemporalGapSwings < 1 ||
            _options.RecentRoleTouchCount < 1 ||
            _options.BreakToleranceAtr < 0m ||
            _options.MaximumActiveDistanceAtr <= 0m ||
            _options.MinimumStrength is < 0m or > 100m ||
            _options.MaximumZones < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>
    /// Compatibility overload without a live price. Prefer the overload that
    /// supplies the latest close so broken/inactive zones can be demoted.
    /// </summary>
    public IReadOnlyList<PriceZone> Detect(
        IReadOnlyList<SwingPoint> swings,
        decimal atr) =>
        DetectCore(swings, atr, currentPrice: null);

    public IReadOnlyList<PriceZone> Detect(
        IReadOnlyList<SwingPoint> swings,
        decimal atr,
        decimal currentPrice) =>
        DetectCore(swings, atr, currentPrice);

    private IReadOnlyList<PriceZone> DetectCore(
        IReadOnlyList<SwingPoint> swings,
        decimal atr,
        decimal? currentPrice)
    {
        ArgumentNullException.ThrowIfNull(swings);
        if (swings.Count == 0 || atr <= 0m)
        {
            return [];
        }

        // Chronological view for recency / temporal-gap measurement. DBSCAN works
        // on a price-sorted view; never confuse price order with time order.
        SwingPoint[] chronological = swings
            .OrderBy(point => point.PivotTime)
            .ThenBy(point => point.ConfirmedAt)
            .TakeLast(_options.MaximumPivots)
            .ToArray();

        if (chronological.Length < _options.MinimumPoints)
        {
            return [];
        }

        // Work on (price, chronologicalIndex) pairs so temporal gaps use stable
        // indexes even when two swings share identical price/time fields.
        IndexedSwing[] points = chronological
            .Select((point, index) => new IndexedSwing(point, index))
            .OrderBy(item => item.Point.Price)
            .ThenBy(item => item.Point.PivotTime)
            .ThenBy(item => item.ChronologicalIndex)
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

        var zones = new List<ZoneCandidate>();
        for (int id = 0; id < clusterId; id++)
        {
            IndexedSwing[] cluster = points
                .Where((_, index) => labels[index] == id)
                .ToArray();
            if (cluster.Length < _options.MinimumPoints)
            {
                continue;
            }

            foreach (IndexedSwing[] segment in SplitByTemporalGaps(cluster))
            {
                if (segment.Length < _options.MinimumPoints)
                {
                    continue;
                }

                ZoneCandidate? candidate = BuildZone(
                    segment,
                    chronological.Length,
                    epsilon,
                    atr,
                    currentPrice);
                if (candidate is not null)
                {
                    zones.Add(candidate);
                }
            }
        }

        return zones
            .OrderByDescending(candidate => candidate.Zone.Strength)
            .ThenByDescending(candidate => candidate.LastTouch)
            .ThenBy(candidate => candidate.Zone.CentrePrice)
            .Take(_options.MaximumZones)
            .Select(candidate => candidate.Zone)
            .ToArray();
    }

    private ZoneCandidate? BuildZone(
        IReadOnlyList<IndexedSwing> cluster,
        int chronologicalLength,
        decimal epsilon,
        decimal atr,
        decimal? currentPrice)
    {
        // Tighten the band around the median so long DBSCAN chains do not produce
        // multi-ATR "zones" that are useless for trading.
        decimal rawCentre = Median(cluster.Select(item => item.Point.Price));
        IndexedSwing[] core = cluster
            .Where(item => Math.Abs(item.Point.Price - rawCentre) <= epsilon)
            .ToArray();
        if (core.Length < _options.MinimumPoints)
        {
            core = cluster
                .OrderBy(item => Math.Abs(item.Point.Price - rawCentre))
                .Take(_options.MinimumPoints)
                .ToArray();
        }

        decimal lower = core.Min(item => item.Point.Price);
        decimal upper = core.Max(item => item.Point.Price);
        decimal centre = Median(core.Select(item => item.Point.Price));
        // Guarantee a usable band even for near-identical pivot prices.
        if (upper - lower < atr * 0.02m)
        {
            decimal half = Math.Max(atr * 0.05m, epsilon * 0.25m);
            lower = centre - half;
            upper = centre + half;
        }

        int highs = core.Count(item => item.Point.Type == SwingType.High);
        int lows = core.Length - highs;
        DateTimeOffset lastTouch = core.Max(item => item.Point.PivotTime);
        DateTimeOffset firstTouch = core.Min(item => item.Point.PivotTime);

        PriceZoneType type = ClassifyType(core, highs, lows, centre, currentPrice, atr);
        ZoneActivity activity = EvaluateActivity(
            type,
            lower,
            upper,
            centre,
            currentPrice,
            atr);

        if (activity.Drop)
        {
            return null;
        }

        if (activity.FlippedType is PriceZoneType flipped)
        {
            type = flipped;
        }

        decimal strength = CalculateStrength(
            core,
            chronologicalLength,
            lower,
            upper,
            epsilon,
            highs,
            lows,
            type,
            activity);
        if (strength < _options.MinimumStrength)
        {
            return null;
        }

        return new ZoneCandidate(
            new PriceZone
            {
                LowerPrice = lower,
                UpperPrice = upper,
                CentrePrice = centre,
                TouchCount = core.Length,
                Strength = strength,
                Type = type
            },
            lastTouch,
            firstTouch);
    }

    private PriceZoneType ClassifyType(
        IReadOnlyList<IndexedSwing> core,
        int highs,
        int lows,
        decimal centre,
        decimal? currentPrice,
        decimal atr)
    {
        if (highs == 0)
        {
            return PriceZoneType.Support;
        }

        if (lows == 0)
        {
            return PriceZoneType.Resistance;
        }

        // Mixed historical touches: let the newest pivots decide the active role.
        IndexedSwing[] recent = core
            .OrderByDescending(item => item.ChronologicalIndex)
            .Take(_options.RecentRoleTouchCount)
            .ToArray();
        int recentHighs = recent.Count(item => item.Point.Type == SwingType.High);
        int recentLows = recent.Length - recentHighs;
        if (recentLows > recentHighs)
        {
            return PriceZoneType.Support;
        }

        if (recentHighs > recentLows)
        {
            return PriceZoneType.Resistance;
        }

        // Still tied: use position relative to price when available
        // (below price → support / demand; above price → resistance / supply).
        if (currentPrice is decimal price)
        {
            decimal breakTolerance = atr * _options.BreakToleranceAtr;
            if (price > centre + breakTolerance)
            {
                return PriceZoneType.Support;
            }

            if (price < centre - breakTolerance)
            {
                return PriceZoneType.Resistance;
            }
        }

        // Prefer the overall majority when recent touches are tied.
        if (lows > highs)
        {
            return PriceZoneType.Support;
        }

        if (highs > lows)
        {
            return PriceZoneType.Resistance;
        }

        return PriceZoneType.Mixed;
    }

    private ZoneActivity EvaluateActivity(
        PriceZoneType type,
        decimal lower,
        decimal upper,
        decimal centre,
        decimal? currentPrice,
        decimal atr)
    {
        if (currentPrice is not decimal price)
        {
            return new ZoneActivity(Drop: false, Broken: false, FlippedType: null, DistanceAtr: 0m);
        }

        decimal distance = DistanceToZone(price, lower, upper);
        decimal distanceAtr = distance / atr;
        if (distanceAtr > _options.MaximumActiveDistanceAtr)
        {
            return new ZoneActivity(Drop: true, Broken: true, FlippedType: null, DistanceAtr: distanceAtr);
        }

        decimal breakTolerance = atr * _options.BreakToleranceAtr;
        bool brokenBelow = price < lower - breakTolerance;
        bool brokenAbove = price > upper + breakTolerance;

        // Classic polarity flip: broken resistance becomes support if price is still
        // nearby; broken support becomes resistance. Far breaks are dropped.
        if (type == PriceZoneType.Resistance && brokenAbove)
        {
            return distanceAtr <= 1.5m
                ? new ZoneActivity(false, true, PriceZoneType.Support, distanceAtr)
                : new ZoneActivity(true, true, null, distanceAtr);
        }

        if (type == PriceZoneType.Support && brokenBelow)
        {
            return distanceAtr <= 1.5m
                ? new ZoneActivity(false, true, PriceZoneType.Resistance, distanceAtr)
                : new ZoneActivity(true, true, null, distanceAtr);
        }

        if (type == PriceZoneType.Mixed && (brokenAbove || brokenBelow))
        {
            PriceZoneType flipped = price > centre
                ? PriceZoneType.Support
                : PriceZoneType.Resistance;
            return new ZoneActivity(false, true, flipped, distanceAtr);
        }

        return new ZoneActivity(false, false, null, distanceAtr);
    }

    private decimal CalculateStrength(
        IReadOnlyList<IndexedSwing> core,
        int chronologicalLength,
        decimal lower,
        decimal upper,
        decimal epsilon,
        int highs,
        int lows,
        PriceZoneType type,
        ZoneActivity activity)
    {
        int touchCount = core.Count;
        decimal touchScore = Math.Min(50m, touchCount * 12m);

        int lastTouchIndex = core.Max(item => item.ChronologicalIndex);
        decimal recency = chronologicalLength <= 1
            ? 1m
            : (decimal)lastTouchIndex / (chronologicalLength - 1);
        decimal recencyScore = recency * 20m;

        // Reward zones whose touches span multiple swings without huge holes.
        int firstIndex = core.Min(item => item.ChronologicalIndex);
        int spanSwings = Math.Max(1, lastTouchIndex - firstIndex);
        decimal density = (decimal)touchCount / spanSwings;
        decimal densityScore = Math.Clamp(density * 15m, 0m, 15m);

        decimal compactness = 1m - Math.Min(
            1m,
            (upper - lower) / Math.Max(epsilon * 2m, 0.0000000001m));
        decimal compactnessScore = compactness * 10m;

        int dominant = Math.Max(highs, lows);
        decimal purity = type == PriceZoneType.Mixed
            ? (decimal)dominant / touchCount * 0.5m
            : (decimal)dominant / touchCount;
        decimal purityScore = purity * 10m;

        decimal brokenPenalty = activity.Broken ? 18m : 0m;
        decimal distancePenalty = activity.DistanceAtr > 1m
            ? Math.Min(15m, (activity.DistanceAtr - 1m) * 5m)
            : 0m;

        return Math.Clamp(
            touchScore +
            recencyScore +
            densityScore +
            compactnessScore +
            purityScore -
            brokenPenalty -
            distancePenalty,
            0m,
            100m);
    }

    private IEnumerable<IndexedSwing[]> SplitByTemporalGaps(IReadOnlyList<IndexedSwing> cluster)
    {
        IndexedSwing[] ordered = cluster
            .OrderBy(item => item.ChronologicalIndex)
            .ThenBy(item => item.Point.PivotTime)
            .ToArray();

        if (ordered.Length == 0)
        {
            yield break;
        }

        var segment = new List<IndexedSwing> { ordered[0] };
        for (int index = 1; index < ordered.Length; index++)
        {
            int gap = ordered[index].ChronologicalIndex - ordered[index - 1].ChronologicalIndex;
            if (gap > _options.MaximumTemporalGapSwings)
            {
                if (segment.Count >= _options.MinimumPoints)
                {
                    yield return segment.ToArray();
                }

                segment = [ordered[index]];
            }
            else
            {
                segment.Add(ordered[index]);
            }
        }

        if (segment.Count >= _options.MinimumPoints)
        {
            yield return segment.ToArray();
        }
    }

    private void Expand(
        IndexedSwing[] points,
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
        IReadOnlyList<IndexedSwing> points,
        int index,
        decimal epsilon)
    {
        var result = new List<int>();
        decimal price = points[index].Point.Price;
        for (int candidate = 0; candidate < points.Count; candidate++)
        {
            if (Math.Abs(points[candidate].Point.Price - price) <= epsilon)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static decimal DistanceToZone(decimal price, decimal lower, decimal upper)
    {
        if (price < lower)
        {
            return lower - price;
        }

        if (price > upper)
        {
            return price - upper;
        }

        return 0m;
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
        DateTimeOffset LastTouch,
        DateTimeOffset FirstTouch);

    private readonly record struct IndexedSwing(
        SwingPoint Point,
        int ChronologicalIndex);

    private readonly record struct ZoneActivity(
        bool Drop,
        bool Broken,
        PriceZoneType? FlippedType,
        decimal DistanceAtr);
}
