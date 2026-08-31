using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingClassifier.Configuration;

namespace TradingClassifier.ML.Training;

/// <summary>
/// Everything needed to reproduce a saved model and to prove that a caller is using it as trained.
/// <para>
/// The ML.NET <c>.zip</c> stores the fitted pipeline and nothing else. Without this sidecar a model
/// can be loaded and scored under a completely different configuration and nothing will complain:
/// feature-name validation cannot catch it because a 15m model and a 5m model produce identically
/// named columns. That is the concrete hazard - training defaults to unresampled input while the
/// live agent defaults to 5m candles, so a mismatch is the default outcome rather than an unlikely
/// accident.
/// </para>
/// </summary>
public sealed record ClassifierModelArtifact
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string TrainerName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Bar interval the model was trained on, in minutes. The field feature names cannot encode.</summary>
    public required int CandleIntervalMinutes { get; init; }

    public required string Instrument { get; init; }
    public required DateTimeOffset TrainedFrom { get; init; }
    public required DateTimeOffset TrainedTo { get; init; }
    public required int TrainingRows { get; init; }

    /// <summary>Classes present in training. A two-class model must not be scored as three.</summary>
    public required IReadOnlyList<string> TrainedClasses { get; init; }

    public required int PredictionHorizon { get; init; }
    public required decimal AtrTargetMultiplier { get; init; }
    public required bool UseMaximumExcursionLabels { get; init; }
    public required int TrainingStride { get; init; }
    public required string EnabledGroups { get; init; }
    public required IReadOnlyList<string> FeatureNames { get; init; }

    /// <summary>Thresholds selected on validation. Without these a caller silently reverts to defaults.</summary>
    public required double BuyProbabilityThreshold { get; init; }
    public required double SellProbabilityThreshold { get; init; }

    /// <summary>Hash over the fields that must match for the model to be valid.</summary>
    public required string ConfigurationHash { get; init; }

    public string ToText() =>
        $"  instrument={Instrument} interval={CandleIntervalMinutes}m horizon={PredictionHorizon} " +
        $"atr={AtrTargetMultiplier} stride={TrainingStride}\n" +
        $"  groups={EnabledGroups} features={FeatureNames.Count} classes={string.Join('/', TrainedClasses)}\n" +
        $"  thresholds buy={BuyProbabilityThreshold:F2} sell={SellProbabilityThreshold:F2}\n" +
        $"  trained {TrainedFrom:yyyy-MM-dd}..{TrainedTo:yyyy-MM-dd} on {TrainingRows:N0} rows  hash={ConfigurationHash[..12]}";

    public static string ComputeHash(
        ClassifierOptions options,
        int candleIntervalMinutes,
        IReadOnlyList<string> featureNames)
    {
        string raw = string.Join('|',
            CurrentSchemaVersion,
            candleIntervalMinutes,
            options.PredictionHorizon,
            options.AtrTargetMultiplier,
            options.UseMaximumExcursionLabels,
            options.TrainingStride,
            options.EnabledGroups,
            string.Join(',', featureNames));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public void Save(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        File.WriteAllText(SidecarPath(modelPath), JsonSerializer.Serialize(this, JsonOptions));
    }

    public static ClassifierModelArtifact Load(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        string path = SidecarPath(modelPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No metadata sidecar beside '{modelPath}'. The model cannot be used safely: its " +
                "timeframe, horizon, label definition and tuned thresholds are unknown, and none of " +
                "them can be recovered from the ML.NET archive.", path);
        }

        return JsonSerializer.Deserialize<ClassifierModelArtifact>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Metadata sidecar '{path}' is empty.");
    }

    /// <summary>
    /// Throws unless the supplied configuration is the one the model was trained under. This is the
    /// check that catches a timeframe mismatch, which feature names cannot.
    /// </summary>
    public void EnsureCompatible(
        ClassifierOptions options,
        int candleIntervalMinutes,
        IReadOnlyList<string> featureNames)
    {
        string actual = ComputeHash(options, candleIntervalMinutes, featureNames);
        if (string.Equals(actual, ConfigurationHash, StringComparison.Ordinal))
            return;

        List<string> differences = [];
        if (candleIntervalMinutes != CandleIntervalMinutes)
            differences.Add($"interval {CandleIntervalMinutes}m -> {candleIntervalMinutes}m");
        if (options.PredictionHorizon != PredictionHorizon)
            differences.Add($"horizon {PredictionHorizon} -> {options.PredictionHorizon}");
        if (options.AtrTargetMultiplier != AtrTargetMultiplier)
            differences.Add($"atr multiplier {AtrTargetMultiplier} -> {options.AtrTargetMultiplier}");
        if (options.UseMaximumExcursionLabels != UseMaximumExcursionLabels)
            differences.Add($"label mode {UseMaximumExcursionLabels} -> {options.UseMaximumExcursionLabels}");
        if (options.EnabledGroups.ToString() != EnabledGroups)
            differences.Add($"groups {EnabledGroups} -> {options.EnabledGroups}");
        if (featureNames.Count != FeatureNames.Count)
            differences.Add($"feature count {FeatureNames.Count} -> {featureNames.Count}");

        throw new InvalidOperationException(
            "This model was trained under a different configuration: " +
            (differences.Count > 0 ? string.Join("; ", differences) : "configuration hash mismatch") +
            ". Scoring it anyway would produce confident, meaningless predictions.");
    }

    private static string SidecarPath(string modelPath) =>
        Path.ChangeExtension(modelPath, null) + ".metadata.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
