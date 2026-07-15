using RiskManager.Calibration;

namespace QuantResearch.Calibration;

public sealed record SetupOutcome
{
    public required string StrategyId { get; init; }
    public required string InstrumentGroup { get; init; }
    public required string Regime { get; init; }
    public required decimal Confidence { get; init; }
    public required bool Won { get; init; }
    public required decimal RMultiple { get; init; }
}

public sealed record ConfidenceReliabilityPoint
{
    public required decimal ConfidenceFrom { get; init; }
    public required decimal ConfidenceTo { get; init; }
    public required int Samples { get; init; }
    public required decimal PredictedProbability { get; init; }
    public required decimal ActualWinRate { get; init; }
    public required decimal ExpectedR { get; init; }
    public required decimal BrierScore { get; init; }
    public required decimal CalibrationError { get; init; }
}

public static class ConfidenceCalibrator
{
    public static IReadOnlyList<ConfidenceReliabilityPoint> Reliability(
        IReadOnlyList<SetupOutcome> outcomes,
        decimal bucketWidth)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (bucketWidth <= 0m || bucketWidth > 100m || 100m % bucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(bucketWidth));
        var result = new List<ConfidenceReliabilityPoint>();
        for (decimal from = 0m; from < 100m; from += bucketWidth)
        {
            decimal to = from + bucketWidth;
            SetupOutcome[] sample = outcomes.Where(item => item.Confidence >= from &&
                (item.Confidence < to || to == 100m && item.Confidence <= to)).ToArray();
            if (sample.Length == 0) continue;
            decimal predicted = sample.Average(item => item.Confidence) / 100m;
            decimal actual = sample.Count(item => item.Won) / (decimal)sample.Length;
            decimal brier = sample.Average(item =>
            {
                decimal observed = item.Won ? 1m : 0m;
                return (predicted - observed) * (predicted - observed);
            });
            result.Add(new ConfidenceReliabilityPoint
            {
                ConfidenceFrom = from,
                ConfidenceTo = to,
                Samples = sample.Length,
                PredictedProbability = predicted,
                ActualWinRate = actual,
                ExpectedR = sample.Average(item => item.RMultiple),
                BrierScore = brier,
                CalibrationError = Math.Abs(predicted - actual)
            });
        }
        return result;
    }

    public static SetupCalibrationArtifact Calibrate(
        IReadOnlyList<SetupOutcome> outcomes,
        decimal bucketWidth,
        string calibrationId,
        DateTimeOffset trainingFrom,
        DateTimeOffset trainingTo,
        string strategyVersion,
        string featureSchemaHash,
        string dataHash,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (bucketWidth <= 0m || bucketWidth > 100m || 100m % bucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(bucketWidth));
        var buckets = new List<SetupCalibrationBucket>();
        foreach (IGrouping<(string StrategyId, string InstrumentGroup, string Regime), SetupOutcome> cohort in outcomes
                     .GroupBy(item => (item.StrategyId, item.InstrumentGroup, item.Regime))
                     .OrderBy(item => item.Key.StrategyId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.InstrumentGroup, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Regime, StringComparer.Ordinal))
        {
            for (decimal from = 0m; from < 100m; from += bucketWidth)
            {
                decimal to = from + bucketWidth;
                SetupOutcome[] sample = cohort.Where(item => item.Confidence >= from &&
                    (item.Confidence < to || to == 100m && item.Confidence <= to)).ToArray();
                if (sample.Length == 0) continue;
                decimal winRate = sample.Count(item => item.Won) / (decimal)sample.Length;
                decimal predicted = sample.Average(item => item.Confidence) / 100m;
                decimal brier = sample.Average(item =>
                {
                    decimal outcome = item.Won ? 1m : 0m;
                    return (predicted - outcome) * (predicted - outcome);
                });
                buckets.Add(new SetupCalibrationBucket
                {
                    StrategyId = cohort.Key.StrategyId,
                    InstrumentGroup = cohort.Key.InstrumentGroup,
                    Regime = cohort.Key.Regime,
                    ConfidenceFrom = from,
                    ConfidenceTo = to,
                    Samples = sample.Length,
                    WinRate = winRate,
                    AverageR = sample.Average(item => item.RMultiple),
                    ExpectedR = sample.Average(item => item.RMultiple),
                    BrierScore = brier
                });
            }
        }

        var artifact = new SetupCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            TrainingFrom = trainingFrom,
            TrainingTo = trainingTo,
            Instruments = outcomes.Select(item => item.InstrumentGroup).Distinct(StringComparer.Ordinal).Order().ToArray(),
            StrategyVersion = strategyVersion,
            FeatureSchemaHash = featureSchemaHash,
            Parameters = new Dictionary<string, string> { ["bucketWidth"] = bucketWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            TotalSamples = outcomes.Count,
            CreatedAt = createdAt,
            DataHash = dataHash,
            Buckets = buckets
        };
        artifact.Validate(featureSchemaHash);
        return artifact;
    }
}
