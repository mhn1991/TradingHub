using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;

namespace QuantResearch.Training.Experiments;

/// <summary>
/// Buckets closed trades by playbook-aware confidence, alignment, CCI, and structural cohorts into a
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
        foreach (IGrouping<(string StrategyId, string PlaybookId, string Regime), SimulatedTradeRecord> cohort in trades
                     .Where(trade => trade.ClosedAt is not null && trade.EntryMultiTimeframeAlignment is not null)
                     .GroupBy(trade => (
                         StrategyId: string.IsNullOrWhiteSpace(trade.StrategyId) ? "unknown" : trade.StrategyId,
                         PlaybookId: MetaModelCohorts.NormalizePlaybook(trade.PlaybookId),
                         Regime: string.IsNullOrWhiteSpace(trade.EntryRegime.ToString())
                             ? "Unknown"
                             : trade.EntryRegime.ToString()))
                     .OrderBy(item => item.Key.StrategyId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.PlaybookId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Regime, StringComparer.Ordinal))
        {
            for (decimal confidenceFrom = 0m; confidenceFrom < 100m; confidenceFrom += confidenceBucketWidth)
            {
                decimal confidenceTo = confidenceFrom + confidenceBucketWidth;
                SimulatedTradeRecord[] confidenceSample = cohort.Where(trade =>
                    trade.EntryConfidence >= confidenceFrom &&
                    (trade.EntryConfidence < confidenceTo || confidenceTo == 100m && trade.EntryConfidence <= confidenceTo))
                    .ToArray();
                if (confidenceSample.Length == 0)
                    continue;

                for (decimal alignmentFrom = 0m; alignmentFrom < 1m; alignmentFrom += alignmentBucketWidth)
                {
                    decimal alignmentTo = alignmentFrom + alignmentBucketWidth;
                    SimulatedTradeRecord[] sample = confidenceSample.Where(trade =>
                        trade.EntryMultiTimeframeAlignment!.Value >= alignmentFrom &&
                        (trade.EntryMultiTimeframeAlignment.Value < alignmentTo || alignmentTo == 1m && trade.EntryMultiTimeframeAlignment.Value <= alignmentTo))
                        .ToArray();
                    if (sample.Length == 0)
                        continue;

                    AddBucket(buckets, cohort.Key.StrategyId, cohort.Key.PlaybookId, cohort.Key.Regime,
                        confidenceFrom, confidenceTo, alignmentFrom, alignmentTo, null, null, sample);

                    foreach (var structuralSample in sample
                                 .Select(trade => new
                                 {
                                     Trade = trade,
                                     CciState = MetaModelCohorts.CoarseCciState(trade.EntryCciConfirmationState),
                                     ConfluenceState = MetaModelCohorts.CoarseConfluenceState(trade.EntryStructuralConfluenceState)
                                 })
                                 .Where(item => item.CciState is not null || item.ConfluenceState is not null)
                                 .GroupBy(item => (item.CciState, item.ConfluenceState))
                                 .OrderBy(item => item.Key.CciState, StringComparer.Ordinal)
                                 .ThenBy(item => item.Key.ConfluenceState, StringComparer.Ordinal))
                    {
                        AddBucket(buckets, cohort.Key.StrategyId, cohort.Key.PlaybookId, cohort.Key.Regime,
                            confidenceFrom, confidenceTo, alignmentFrom, alignmentTo,
                            structuralSample.Key.CciState, structuralSample.Key.ConfluenceState,
                            structuralSample.Select(item => item.Trade).ToArray());
                    }
                }

                if (alignmentBucketWidth < 1m)
                {
                    AddBucket(buckets, cohort.Key.StrategyId, cohort.Key.PlaybookId, cohort.Key.Regime,
                        confidenceFrom, confidenceTo, 0m, 1m, null, null, confidenceSample);
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

    private static void AddBucket(
        ICollection<MetaModelBucket> buckets,
        string strategyId,
        string playbookId,
        string regime,
        decimal confidenceFrom,
        decimal confidenceTo,
        decimal alignmentFrom,
        decimal alignmentTo,
        string? cciState,
        string? confluenceState,
        IReadOnlyList<SimulatedTradeRecord> sample)
    {
        decimal winRate = sample.Count(trade => (trade.RMultiple ?? 0m) > 0m) / (decimal)sample.Count;
        decimal predicted = sample.Average(trade => trade.EntryConfidence) / 100m;
        decimal brier = sample.Average(trade =>
        {
            decimal outcome = (trade.RMultiple ?? 0m) > 0m ? 1m : 0m;
            return (predicted - outcome) * (predicted - outcome);
        });
        buckets.Add(new MetaModelBucket
        {
            StrategyId = strategyId,
            PlaybookId = playbookId,
            Regime = regime,
            ConfidenceFrom = confidenceFrom,
            ConfidenceTo = confidenceTo,
            AlignmentFrom = alignmentFrom,
            AlignmentTo = alignmentTo,
            CciState = cciState,
            StructuralConfluenceState = confluenceState,
            Samples = sample.Count,
            WinRate = winRate,
            BrierScore = brier,
            ExpectedR = sample.Average(trade => trade.RMultiple ?? 0m)
        });
    }
}
