using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;

namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Merges per-strategy calibration artifacts produced by parallel legacy/improved training
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
        if (parts.Count == 1)
            return parts[0] with { CalibrationId = calibrationId };

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
            Buckets = ordered.SelectMany(item => item.Buckets).ToArray()
        };
        merged.Validate(MetaLabelFeatureFactory.SchemaVersion);
        return merged;
    }

    public static MetaModelArtifact MergeMetaModel(IReadOnlyList<MetaModelArtifact> parts, string calibrationId)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("At least one meta-model artifact is required.", nameof(parts));
        if (parts.Count == 1)
            return parts[0] with { CalibrationId = calibrationId, ModelVersion = calibrationId };

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
            Buckets = ordered.SelectMany(item => item.Buckets).ToArray()
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
        if (parts.Count == 1)
            return parts[0] with { CalibrationId = calibrationId };

        TradeManagementCalibration[] ordered = parts
            .OrderBy(item => item.CalibrationId, StringComparer.Ordinal)
            .ToArray();
        var merged = new TradeManagementCalibration
        {
            SchemaVersion = 1,
            CalibrationId = calibrationId,
            Cohorts = ordered.SelectMany(item => item.Cohorts).ToArray(),
            SourceDataHash = string.Join("|", ordered.Select(item => item.SourceDataHash)),
            CreatedAt = DateTimeOffset.UtcNow
        };
        merged.Validate();
        return merged;
    }
}
