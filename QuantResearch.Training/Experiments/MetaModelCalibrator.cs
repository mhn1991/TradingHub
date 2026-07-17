using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;

namespace QuantResearch.Training.Experiments;

/// <summary>
/// Buckets closed trades by (StrategyId, Regime, Confidence, MultiTimeframeAlignment) into a
/// <see cref="MetaModelArtifact"/> - the same win-rate/Brier-score/expected-R math
/// <c>ConfidenceCalibrator.Calibrate</c> already uses, with one extra dimension
/// (<see cref="SimulatedTradeRecord.EntryMultiTimeframeAlignment"/>) since the meta-model
/// buckets on more than confidence alone.
/// </summary>
public static class MetaModelCalibrator
{
    public static MetaModelArtifact Calibrate(
        IReadOnlyList<SimulatedTradeRecord> trades,
        decimal confidenceBucketWidth,
        decimal alignmentBucketWidth,
        string calibrationId,
        string modelVersion,
        DateTimeOffset trainingFrom,
        DateTimeOffset trainingTo,
        string dataHash,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (confidenceBucketWidth <= 0m || confidenceBucketWidth > 100m || 100m % confidenceBucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(confidenceBucketWidth));
        if (alignmentBucketWidth <= 0m || alignmentBucketWidth > 1m || 1m % alignmentBucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(alignmentBucketWidth));
        if (trainingTo <= trainingFrom)
            trainingTo = trainingFrom.AddSeconds(1);

        var buckets = new List<MetaModelBucket>();
        foreach (IGrouping<(string StrategyId, string Regime), SimulatedTradeRecord> cohort in trades
                     .Where(trade => trade.ClosedAt is not null && trade.EntryMultiTimeframeAlignment is not null)
                     .GroupBy(trade => (
                         StrategyId: string.IsNullOrWhiteSpace(trade.StrategyId) ? "unknown" : trade.StrategyId,
                         Regime: string.IsNullOrWhiteSpace(trade.EntryRegime.ToString())
                             ? "Unknown"
                             : trade.EntryRegime.ToString()))
                     .OrderBy(item => item.Key.StrategyId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Regime, StringComparer.Ordinal))
        {
            for (decimal confidenceFrom = 0m; confidenceFrom < 100m; confidenceFrom += confidenceBucketWidth)
            {
                decimal confidenceTo = confidenceFrom + confidenceBucketWidth;
                for (decimal alignmentFrom = 0m; alignmentFrom < 1m; alignmentFrom += alignmentBucketWidth)
                {
                    decimal alignmentTo = alignmentFrom + alignmentBucketWidth;
                    SimulatedTradeRecord[] sample = cohort.Where(trade =>
                        trade.EntryConfidence >= confidenceFrom &&
                        (trade.EntryConfidence < confidenceTo || confidenceTo == 100m && trade.EntryConfidence <= confidenceTo) &&
                        trade.EntryMultiTimeframeAlignment!.Value >= alignmentFrom &&
                        (trade.EntryMultiTimeframeAlignment.Value < alignmentTo || alignmentTo == 1m && trade.EntryMultiTimeframeAlignment.Value <= alignmentTo))
                        .ToArray();
                    if (sample.Length == 0)
                        continue;

                    decimal winRate = sample.Count(trade => (trade.RMultiple ?? 0m) > 0m) / (decimal)sample.Length;
                    decimal predicted = sample.Average(trade => trade.EntryConfidence) / 100m;
                    decimal brier = sample.Average(trade =>
                    {
                        decimal outcome = (trade.RMultiple ?? 0m) > 0m ? 1m : 0m;
                        return (predicted - outcome) * (predicted - outcome);
                    });
                    decimal expectedR = sample.Average(trade => trade.RMultiple ?? 0m);

                    buckets.Add(new MetaModelBucket
                    {
                        StrategyId = cohort.Key.StrategyId,
                        Regime = cohort.Key.Regime,
                        ConfidenceFrom = confidenceFrom,
                        ConfidenceTo = confidenceTo,
                        AlignmentFrom = alignmentFrom,
                        AlignmentTo = alignmentTo,
                        Samples = sample.Length,
                        WinRate = winRate,
                        BrierScore = brier,
                        ExpectedR = expectedR
                    });
                }
            }
        }

        return new MetaModelArtifact
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            ModelVersion = modelVersion,
            FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
            TrainingFrom = trainingFrom,
            TrainingTo = trainingTo,
            DataHash = dataHash,
            CreatedAt = createdAt,
            Buckets = buckets
        };
    }
}
