using Brokers.Models;

namespace PortfolioManager.Correlation;

public sealed record CorrelationRiskOptions
{
    public BarInterval Interval { get; init; } = BarInterval.Hours(1);
    public int LookbackBars { get; init; } = 120;
    public int MinimumSamples { get; init; } = 60;
    public decimal SoftCorrelationThreshold { get; init; } = 0.50m;
    public decimal HardCorrelationThreshold { get; init; } = 0.75m;
    public decimal SoftRiskMultiplier { get; init; } = 0.70m;
    public decimal HardRiskMultiplier { get; init; } = 0.40m;
    public decimal InsufficientDataRiskMultiplier { get; init; } = 0.70m;
    public decimal MaximumHedgeCredit { get; init; } = 0.10m;

    public void Validate()
    {
        if (!Interval.IsValid || LookbackBars < 2 || MinimumSamples < 2 || MinimumSamples > LookbackBars ||
            SoftCorrelationThreshold is < 0m or > 1m ||
            HardCorrelationThreshold <= SoftCorrelationThreshold || HardCorrelationThreshold > 1m ||
            SoftRiskMultiplier is < 0m or > 1m || HardRiskMultiplier < 0m ||
            HardRiskMultiplier > SoftRiskMultiplier ||
            InsufficientDataRiskMultiplier is < 0m or > 1m || MaximumHedgeCredit is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(CorrelationRiskOptions));
    }
}

public sealed record CorrelationSnapshot
{
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyDictionary<string, decimal> PairCorrelations { get; init; }
    public required IReadOnlyDictionary<InstrumentKey, string> ClusterByInstrument { get; init; }
    public required int MinimumPairSamples { get; init; }
}

public sealed record CorrelationPenaltyDecision
{
    public required decimal RiskMultiplier { get; init; }
    public required decimal MaximumRelevantCorrelation { get; init; }
    public required string ClusterId { get; init; }
    public required string ReasonCode { get; init; }
}

public sealed class RollingCorrelationClusters
{
    private readonly CorrelationRiskOptions _options;
    private readonly Dictionary<InstrumentKey, Queue<(DateTimeOffset Time, decimal Return)>> _returns = [];
    private DateTimeOffset _lastAvailableAt;

    public RollingCorrelationClusters(CorrelationRiskOptions? options = null)
    {
        _options = options ?? new CorrelationRiskOptions();
        _options.Validate();
    }

    public CorrelationSnapshot Update(DateTimeOffset availableAt, IReadOnlyDictionary<InstrumentKey, decimal> completedReturns)
    {
        ArgumentNullException.ThrowIfNull(completedReturns);
        if (availableAt <= _lastAvailableAt)
            throw new InvalidOperationException("Correlation observations must be chronological.");
        _lastAvailableAt = availableAt;
        foreach ((InstrumentKey instrument, decimal value) in completedReturns.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            if (!_returns.TryGetValue(instrument, out Queue<(DateTimeOffset, decimal)>? queue))
            {
                queue = new Queue<(DateTimeOffset, decimal)>();
                _returns[instrument] = queue;
            }
            queue.Enqueue((availableAt, value));
            while (queue.Count > _options.LookbackBars)
                queue.Dequeue();
        }
        return BuildSnapshot(availableAt);
    }

    public CorrelationPenaltyDecision Evaluate(
        InstrumentKey candidate,
        int candidateDirection,
        IReadOnlyDictionary<InstrumentKey, int> existingDirections)
    {
        CorrelationSnapshot snapshot = BuildSnapshot(_lastAvailableAt);
        decimal maxRelevant = decimal.MinValue;
        string cluster = snapshot.ClusterByInstrument.GetValueOrDefault(candidate) ?? $"cluster:{candidate.Value}";
        bool any = false;
        foreach ((InstrumentKey existing, int direction) in existingDirections.OrderBy(item => item.Key.Value, StringComparer.Ordinal))
        {
            if (existing == candidate) continue;
            string key = PairKey(candidate, existing);
            if (!snapshot.PairCorrelations.TryGetValue(key, out decimal correlation)) continue;
            any = true;
            decimal relevant = correlation * Math.Sign(candidateDirection) * Math.Sign(direction);
            maxRelevant = Math.Max(maxRelevant, relevant);
        }

        if (!any)
            return new CorrelationPenaltyDecision
            {
                RiskMultiplier = _options.InsufficientDataRiskMultiplier,
                MaximumRelevantCorrelation = 0m,
                ClusterId = cluster,
                ReasonCode = "CorrelationDataInsufficient"
            };
        if (maxRelevant >= _options.HardCorrelationThreshold)
            return Penalty(_options.HardRiskMultiplier, maxRelevant, cluster, "HardCorrelationPenalty");
        if (maxRelevant >= _options.SoftCorrelationThreshold)
            return Penalty(_options.SoftRiskMultiplier, maxRelevant, cluster, "SoftCorrelationPenalty");
        // Negative relevance is a hedge. Credit is deliberately capped and the first
        // production risk multiplier may never exceed one.
        return Penalty(1m, maxRelevant, cluster, maxRelevant < 0m ? "CappedHedgeCredit" : "NoCorrelationPenalty");
    }

    private CorrelationSnapshot BuildSnapshot(DateTimeOffset availableAt)
    {
        InstrumentKey[] instruments = _returns.Keys.OrderBy(item => item.Value, StringComparer.Ordinal).ToArray();
        var correlations = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var parent = instruments.ToDictionary(item => item, item => item);
        int minimumSamples = int.MaxValue;
        for (int i = 0; i < instruments.Length; i++)
        for (int j = i + 1; j < instruments.Length; j++)
        {
            (decimal? correlation, int samples) = Correlation(instruments[i], instruments[j]);
            minimumSamples = Math.Min(minimumSamples, samples);
            if (correlation is not decimal value || samples < _options.MinimumSamples) continue;
            correlations[PairKey(instruments[i], instruments[j])] = value;
            if (Math.Abs(value) >= _options.HardCorrelationThreshold)
                Union(parent, instruments[i], instruments[j]);
        }

        var cluster = new Dictionary<InstrumentKey, string>();
        foreach (InstrumentKey instrument in instruments)
        {
            InstrumentKey root = Find(parent, instrument);
            cluster[instrument] = $"cluster:{root.Value}";
        }
        return new CorrelationSnapshot
        {
            AvailableAt = availableAt,
            PairCorrelations = correlations,
            ClusterByInstrument = cluster,
            MinimumPairSamples = minimumSamples == int.MaxValue ? 0 : minimumSamples
        };
    }

    private (decimal? Value, int Samples) Correlation(InstrumentKey left, InstrumentKey right)
    {
        Dictionary<DateTimeOffset, decimal> rightByTime = _returns[right].ToDictionary(item => item.Time, item => item.Return);
        (decimal X, decimal Y)[] pairs = _returns[left]
            .Where(item => rightByTime.ContainsKey(item.Time))
            .Select(item => (item.Return, rightByTime[item.Time]))
            .ToArray();
        if (pairs.Length < 2) return (null, pairs.Length);
        decimal meanX = pairs.Average(item => item.X);
        decimal meanY = pairs.Average(item => item.Y);
        decimal numerator = pairs.Sum(item => (item.X - meanX) * (item.Y - meanY));
        decimal denominatorX = pairs.Sum(item => (item.X - meanX) * (item.X - meanX));
        decimal denominatorY = pairs.Sum(item => (item.Y - meanY) * (item.Y - meanY));
        if (denominatorX <= 0m || denominatorY <= 0m) return (0m, pairs.Length);
        decimal denominator = (decimal)Math.Sqrt((double)(denominatorX * denominatorY));
        return (Math.Clamp(numerator / denominator, -1m, 1m), pairs.Length);
    }

    private static CorrelationPenaltyDecision Penalty(decimal multiplier, decimal correlation, string cluster, string code) => new()
    {
        RiskMultiplier = Math.Clamp(multiplier, 0m, 1m),
        MaximumRelevantCorrelation = correlation,
        ClusterId = cluster,
        ReasonCode = code
    };

    private static string PairKey(InstrumentKey left, InstrumentKey right) =>
        string.CompareOrdinal(left.Value, right.Value) <= 0
            ? $"{left.Value}|{right.Value}"
            : $"{right.Value}|{left.Value}";

    private static InstrumentKey Find(Dictionary<InstrumentKey, InstrumentKey> parent, InstrumentKey value)
    {
        while (parent[value] != value) value = parent[value];
        return value;
    }

    private static void Union(Dictionary<InstrumentKey, InstrumentKey> parent, InstrumentKey left, InstrumentKey right)
    {
        InstrumentKey leftRoot = Find(parent, left);
        InstrumentKey rightRoot = Find(parent, right);
        if (leftRoot == rightRoot) return;
        if (string.CompareOrdinal(leftRoot.Value, rightRoot.Value) <= 0) parent[rightRoot] = leftRoot;
        else parent[leftRoot] = rightRoot;
    }
}
