using QuantResearch.Models;

namespace QuantResearch.Analysis;

/// <summary>
/// Cohort-level, descriptive attribution for entry-time NEoWave evidence. This does not
/// infer causality and must not be used to tune the same evaluation window. Compare reports
/// across walk-forward validation/test folds before allowing the evidence to affect trading.
/// </summary>
public sealed record NeoWaveAttributionOptions
{
    public decimal StructuralScoreBucketSize { get; init; } = 10m;
    public decimal ConflictScoreBucketSize { get; init; } = 10m;
    public int MinimumCohortSamples { get; init; } = 5;

    public void Validate()
    {
        if (StructuralScoreBucketSize is <= 0m or > 100m ||
            ConflictScoreBucketSize is <= 0m or > 100m ||
            MinimumCohortSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(NeoWaveAttributionOptions));
        }
    }
}

public sealed record NeoWaveAttributionCohort
{
    public required string PatternType { get; init; }
    public required string StructuralScoreBucket { get; init; }
    public required string ConflictScoreBucket { get; init; }
    public required int TradeCount { get; init; }
    public required decimal WinRate { get; init; }
    public required decimal AverageR { get; init; }
    public required decimal ProfitFactor { get; init; }
    public required decimal AverageMfeR { get; init; }
    public required decimal AverageMaeR { get; init; }
    public required decimal AverageRiskMultiplier { get; init; }
    public required bool MeetsMinimumSamples { get; init; }
}

public sealed record NeoWaveAttributionReport
{
    public required int TotalTrades { get; init; }
    public required int TradesWithEvidence { get; init; }
    public required int TradesWithoutEvidence { get; init; }
    public required ResearchPerformance WithEvidencePerformance { get; init; }
    public required ResearchPerformance WithoutEvidencePerformance { get; init; }
    public required IReadOnlyList<NeoWaveAttributionCohort> Cohorts { get; init; }
    public required DateTimeOffset CalculatedAt { get; init; }
    public required string Warning { get; init; }
}

public static class NeoWaveAttribution
{
    public static NeoWaveAttributionReport Analyze(
        IReadOnlyList<ResearchTrade> trades,
        NeoWaveAttributionOptions? options = null,
        DateTimeOffset? calculatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(trades);
        options ??= new NeoWaveAttributionOptions();
        options.Validate();

        ResearchTrade[] withEvidence = trades
            .Where(HasEvidence)
            .OrderBy(item => item.OpenedAt)
            .ThenBy(item => item.TradeId, StringComparer.Ordinal)
            .ToArray();
        ResearchTrade[] withoutEvidence = trades
            .Where(item => !HasEvidence(item))
            .OrderBy(item => item.OpenedAt)
            .ThenBy(item => item.TradeId, StringComparer.Ordinal)
            .ToArray();

        NeoWaveAttributionCohort[] cohorts = withEvidence
            .GroupBy(item => new CohortKey(
                item.NeoWavePatternType ?? "Unknown",
                Bucket(item.NeoWaveStructuralScore!.Value, options.StructuralScoreBucketSize),
                Bucket(item.NeoWaveConflictScore!.Value, options.ConflictScoreBucketSize)))
            .OrderBy(group => group.Key.PatternType, StringComparer.Ordinal)
            .ThenBy(group => group.Key.StructuralBucketStart)
            .ThenBy(group => group.Key.ConflictBucketStart)
            .Select(group => BuildCohort(group.Key, group.ToArray(), options))
            .ToArray();

        return new NeoWaveAttributionReport
        {
            TotalTrades = trades.Count,
            TradesWithEvidence = withEvidence.Length,
            TradesWithoutEvidence = withoutEvidence.Length,
            WithEvidencePerformance = ResearchMetrics.Calculate(withEvidence),
            WithoutEvidencePerformance = ResearchMetrics.Calculate(withoutEvidence),
            Cohorts = cohorts,
            CalculatedAt = calculatedAt ?? DateTimeOffset.UtcNow,
            Warning =
                "Descriptive attribution only. It does not establish incremental causal edge. " +
                "Validate on purged walk-forward folds and an untouched test window, and compare " +
                "against existing regime, structure and price-action features."
        };
    }

    private static bool HasEvidence(ResearchTrade trade) =>
        !string.IsNullOrWhiteSpace(trade.NeoWaveHypothesisId) &&
        !string.IsNullOrWhiteSpace(trade.NeoWavePatternType) &&
        trade.NeoWaveStructuralScore is >= 0m and <= 100m &&
        trade.NeoWaveConflictScore is >= 0m and <= 100m;

    private static NeoWaveAttributionCohort BuildCohort(
        CohortKey key,
        IReadOnlyList<ResearchTrade> trades,
        NeoWaveAttributionOptions options)
    {
        decimal gains = trades.Where(item => item.RMultiple > 0m).Sum(item => item.RMultiple);
        decimal losses = -trades.Where(item => item.RMultiple < 0m).Sum(item => item.RMultiple);
        return new NeoWaveAttributionCohort
        {
            PatternType = key.PatternType,
            StructuralScoreBucket = FormatBucket(key.StructuralBucketStart, options.StructuralScoreBucketSize),
            ConflictScoreBucket = FormatBucket(key.ConflictBucketStart, options.ConflictScoreBucketSize),
            TradeCount = trades.Count,
            WinRate = trades.Count == 0
                ? 0m
                : 100m * trades.Count(item => item.RMultiple > 0m) / trades.Count,
            AverageR = trades.Count == 0 ? 0m : trades.Average(item => item.RMultiple),
            ProfitFactor = losses > 0m ? gains / losses : gains > 0m ? decimal.MaxValue : 0m,
            AverageMfeR = trades.Count == 0 ? 0m : trades.Average(item => item.MaximumFavourableExcursionR),
            AverageMaeR = trades.Count == 0 ? 0m : trades.Average(item => item.MaximumAdverseExcursionR),
            AverageRiskMultiplier = trades.Count == 0 ? 1m : trades.Average(item => item.NeoWaveRiskMultiplier),
            MeetsMinimumSamples = trades.Count >= options.MinimumCohortSamples
        };
    }

    private static decimal Bucket(decimal value, decimal size)
    {
        decimal clamped = Math.Clamp(value, 0m, 100m);
        decimal start = decimal.Floor(clamped / size) * size;
        decimal maximumStart = decimal.Floor((100m - 0.00000001m) / size) * size;
        return Math.Min(start, maximumStart);
    }

    private static string FormatBucket(decimal start, decimal size) =>
        $"{start:0.##}-{Math.Min(100m, start + size):0.##}";

    private sealed record CohortKey(
        string PatternType,
        decimal StructuralBucketStart,
        decimal ConflictBucketStart);
}
