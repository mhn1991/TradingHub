using QuantResearch.Models;
using TradeManager;

namespace QuantResearch.Calibration;

public sealed record TradePathPoint
{
    public required int BarsAfterEntry { get; init; }
    public required decimal MfeR { get; init; }
    public required decimal MaeR { get; init; }
}

public sealed record TradePathObservation
{
    public required ResearchTrade Trade { get; init; }
    public required IReadOnlyList<TradePathPoint> Path { get; init; }
}

public static class TradeManagementCohortAnalyzer
{
    private static readonly int[] Horizons = [1, 3, 5, 8, 12];

    public static TradeManagementCalibration Analyze(
        IReadOnlyList<TradePathObservation> observations,
        string calibrationId,
        string sourceDataHash,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var cohorts = observations.GroupBy(item => (
                item.Trade.StrategyId,
                item.Trade.InstrumentGroup,
                item.Trade.Regime,
                item.Trade.SetupType,
                item.Trade.Direction,
                item.Trade.Session,
                item.Trade.VolatilityBucket,
                ConfidenceBucket: (int)(item.Trade.Confidence / 10m) * 10))
            .OrderBy(group => group.Key.StrategyId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.InstrumentGroup, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Regime, StringComparer.Ordinal)
            .Select(group => Build(group.Key, group.ToArray()))
            .ToArray();
        return new TradeManagementCalibration
        {
            CalibrationId = calibrationId,
            Cohorts = cohorts,
            SourceDataHash = sourceDataHash,
            CreatedAt = createdAt
        };
    }

    private static TradeManagementCohort Build(
        (string StrategyId, string InstrumentGroup, string Regime, string SetupType, string Direction,
            string Session, string VolatilityBucket, int ConfidenceBucket) key,
        TradePathObservation[] sample)
    {
        TradePathObservation[] winners = sample.Where(item => item.Trade.RMultiple > 0m).ToArray();
        var mfe = new Dictionary<int, decimal>();
        foreach (int horizon in Horizons)
        {
            decimal[] values = winners.Select(item => item.Path
                    .Where(point => point.BarsAfterEntry <= horizon)
                    .Select(point => point.MfeR).DefaultIfEmpty().Max())
                .Order().ToArray();
            mfe[horizon] = Quantile(values, 0.80m);
        }
        decimal[] maeBeforeHalfR = sample.Select(item => item.Path
                .TakeWhile(point => point.MfeR < 0.5m)
                .Select(point => point.MaeR).DefaultIfEmpty().Min())
            .Order().ToArray();
        decimal[] durations = sample.Select(item => (decimal)item.Path.Select(point => point.BarsAfterEntry).DefaultIfEmpty().Max()).Order().ToArray();
        decimal[] efficiency = sample.Select(item => item.Trade.MaximumFavourableExcursionR > 0m
            ? item.Trade.RMultiple / item.Trade.MaximumFavourableExcursionR
            : 0m).Order().ToArray();
        decimal[] stops = sample.Select(item => item.Trade.StopDistance).Order().ToArray();
        decimal[] partialContributions = sample.Select(item => item.Trade.PartialExitContributionR).Order().ToArray();
        decimal[] runnerContributions = sample.Select(item => item.Trade.RunnerContributionR).Order().ToArray();
        decimal[] thresholds = [0.5m, 1m, 2m];
        var timeToThreshold = thresholds.ToDictionary(
            threshold => threshold,
            threshold => Quantile(sample
                .Select(item => item.Path.FirstOrDefault(point => point.MfeR >= threshold)?.BarsAfterEntry)
                .Where(value => value is not null)
                .Select(value => (decimal)value!.Value)
                .Order()
                .ToArray(), 0.5m));
        var giveback = thresholds.ToDictionary(
            threshold => threshold,
            threshold => Quantile(sample
                .Select(item => MaximumGivebackAfter(item.Path, threshold))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .Order()
                .ToArray(), 0.5m));
        return new TradeManagementCohort
        {
            CohortId = string.Join("|", key.StrategyId, key.InstrumentGroup, key.Regime, key.SetupType,
                key.Direction, key.Session, key.VolatilityBucket, key.ConfidenceBucket),
            StrategyId = key.StrategyId,
            InstrumentGroup = key.InstrumentGroup,
            Regime = key.Regime,
            SetupType = key.SetupType,
            Direction = key.Direction,
            Session = key.Session,
            VolatilityBucket = key.VolatilityBucket,
            ConfidenceBucket = key.ConfidenceBucket,
            Samples = sample.Length,
            WinnerMfe80PercentileByBar = mfe,
            MedianBarsToMfeThreshold = timeToThreshold,
            MedianMaximumGivebackAfterMfe = giveback,
            MedianMaeBeforeHalfR = Quantile(maeBeforeHalfR, 0.5m),
            MedianDurationBars = Quantile(durations, 0.5m),
            MedianExitEfficiency = Quantile(efficiency, 0.5m),
            MedianStopDistance = Quantile(stops, 0.5m),
            StopDistance10Percentile = Quantile(stops, 0.1m),
            StopDistance90Percentile = Quantile(stops, 0.9m),
            MedianPartialExitContributionR = Quantile(partialContributions, 0.5m),
            MedianRunnerContributionR = Quantile(runnerContributions, 0.5m)
        };
    }

    private static decimal? MaximumGivebackAfter(IReadOnlyList<TradePathPoint> path, decimal threshold)
    {
        int index = path.ToList().FindIndex(point => point.MfeR >= threshold);
        if (index < 0) return null;
        decimal peak = path[index].MfeR;
        decimal maximumGiveback = 0m;
        for (int i = index + 1; i < path.Count; i++)
        {
            peak = Math.Max(peak, path[i].MfeR);
            maximumGiveback = Math.Max(maximumGiveback, peak - path[i].MfeR);
        }
        return maximumGiveback;
    }

    private static decimal Quantile(decimal[] sorted, decimal percentile)
    {
        if (sorted.Length == 0) return 0m;
        int index = (int)decimal.Floor((sorted.Length - 1) * percentile);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
