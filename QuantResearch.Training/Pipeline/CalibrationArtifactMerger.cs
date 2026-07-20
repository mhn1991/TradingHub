using Agent.Configuration;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;

namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Merges per-strategy calibration artifacts produced by parallel catalog-strategy training
/// chains into single artifacts the simulator runtime can attach (one setup / meta / management
/// object per backtest). Buckets and cohorts stay keyed by <c>StrategyId</c>, so lookups remain
/// strategy-specific after the merge.
/// </summary>
public static class CalibrationArtifactMerger
{
    public static SetupCalibrationArtifact MergeSetup(IReadOnlyList<SetupCalibrationArtifact> parts, string calibrationId)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("At least one setup artifact is required.", nameof(parts));
        foreach (SetupCalibrationArtifact part in parts)
            part.Validate(MetaLabelFeatureFactory.SchemaVersion);
        if (parts.Count == 1)
            return parts[0] with
            {
                CalibrationId = calibrationId,
                StrategyVersion = NormalizeStrategyScope(parts[0].StrategyVersion),
                Buckets = parts[0].Buckets.Select(Normalize).ToArray()
            };

        SetupCalibrationArtifact[] ordered = parts
            .OrderBy(item => item.StrategyVersion, StringComparer.Ordinal)
            .ToArray();
        var merged = new SetupCalibrationArtifact
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            TrainingFrom = ordered.Min(item => item.TrainingFrom),
            TrainingTo = ordered.Max(item => item.TrainingTo),
            Instruments = ordered
                .SelectMany(item => item.Instruments)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StrategyVersion = string.Join(",", ordered
                .SelectMany(item => item.StrategyVersion.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(NormalizeStrategyId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)),
            FeatureSchemaHash = ordered[0].FeatureSchemaHash,
            Parameters = new Dictionary<string, string>(ordered[0].Parameters)
            {
                ["mergedFrom"] = string.Join(";", ordered.Select(item => item.CalibrationId))
            },
            TotalSamples = ordered.Sum(item => item.TotalSamples),
            CreatedAt = DateTimeOffset.UtcNow,
            DataHash = string.Join("|", ordered.Select(item => item.DataHash)),
            Buckets = ordered.SelectMany(item => item.Buckets).Select(Normalize).ToArray()
        };
        merged.Validate(MetaLabelFeatureFactory.SchemaVersion);
        return merged;
    }

    public static MetaModelArtifact MergeMetaModel(IReadOnlyList<MetaModelArtifact> parts, string calibrationId)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("At least one meta-model artifact is required.", nameof(parts));
        foreach (MetaModelArtifact part in parts)
            part.Validate(MetaLabelFeatureFactory.SchemaVersion);
        if (parts.Count == 1)
            return parts[0] with
            {
                CalibrationId = calibrationId,
                ModelVersion = calibrationId,
                Buckets = parts[0].Buckets.Select(Normalize).ToArray()
            };

        MetaModelArtifact[] ordered = parts
            .OrderBy(item => item.CalibrationId, StringComparer.Ordinal)
            .ToArray();
        var merged = new MetaModelArtifact
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            ModelVersion = calibrationId,
            FeatureSchemaHash = ordered[0].FeatureSchemaHash,
            TrainingFrom = ordered.Min(item => item.TrainingFrom),
            TrainingTo = ordered.Max(item => item.TrainingTo),
            DataHash = string.Join("|", ordered.Select(item => item.DataHash)),
            CreatedAt = DateTimeOffset.UtcNow,
            Buckets = ordered.SelectMany(item => item.Buckets).Select(Normalize).ToArray()
        };
        merged.Validate(MetaLabelFeatureFactory.SchemaVersion);
        return merged;
    }

    public static TradeManagementCalibration MergeManagement(
        IReadOnlyList<TradeManagementCalibration> parts,
        string calibrationId)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("At least one management artifact is required.", nameof(parts));
        foreach (TradeManagementCalibration part in parts)
            part.Validate();
        if (parts.Count == 1)
            return parts[0] with
            {
                CalibrationId = calibrationId,
                Cohorts = parts[0].Cohorts.Select(Normalize).ToArray()
            };

        TradeManagementCalibration[] ordered = parts
            .OrderBy(item => item.CalibrationId, StringComparer.Ordinal)
            .ToArray();
        var merged = new TradeManagementCalibration
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            Cohorts = ordered.SelectMany(item => item.Cohorts).Select(Normalize).ToArray(),
            SourceDataHash = string.Join("|", ordered.Select(item => item.SourceDataHash)),
            CreatedAt = DateTimeOffset.UtcNow
        };
        merged.Validate();
        return merged;
    }

    private static string NormalizeStrategyId(string strategyId) =>
        TradingAgentTypeIds.Format(TradingAgentTypeIds.Parse(strategyId));

    private static string NormalizeStrategyScope(string scope) => string.Join(",", scope
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(NormalizeStrategyId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));

    private static SetupCalibrationBucket Normalize(SetupCalibrationBucket bucket) =>
        bucket with { StrategyId = NormalizeStrategyId(bucket.StrategyId) };

    private static MetaModelBucket Normalize(MetaModelBucket bucket) =>
        bucket with { StrategyId = NormalizeStrategyId(bucket.StrategyId) };

    private static TradeManagementCohort Normalize(TradeManagementCohort cohort) =>
        cohort with { StrategyId = NormalizeStrategyId(cohort.StrategyId) };
}
