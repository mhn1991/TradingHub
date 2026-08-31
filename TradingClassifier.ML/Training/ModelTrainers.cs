using Microsoft.ML;
using Microsoft.ML.Data;
using TradingClassifier.Configuration;
using TradingClassifier.Features;
using TradingClassifier.ML.Inference;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Training;

/// <summary>Section 30's <c>IModelTrainer</c>.</summary>
public interface IModelTrainer
{
    string Name { get; }

    TrainedModel Train(IReadOnlyList<LabeledFeatureRow> train, FeatureSchema schema);
}

/// <summary>A fitted pipeline plus everything needed to score or persist it.</summary>
public sealed class TrainedModel : IDisposable
{
    internal TrainedModel(
        MLContext context,
        ITransformer transformer,
        DataViewSchema inputSchema,
        FeatureSchema featureSchema,
        string trainerName,
        IReadOnlyCollection<TradeLabel>? trainedClasses = null)
    {
        TrainedClasses = trainedClasses ?? [TradeLabel.Sell, TradeLabel.NoTrade, TradeLabel.Buy];
        Context = context;
        Transformer = transformer;
        InputSchema = inputSchema;
        FeatureSchema = featureSchema;
        TrainerName = trainerName;
        Model = MlNetTradingModel.Create(
            context, transformer, inputSchema, featureSchema.Names, TrainedClasses);
    }

    public MLContext Context { get; }
    public ITransformer Transformer { get; }
    public DataViewSchema InputSchema { get; }
    public FeatureSchema FeatureSchema { get; }
    public string TrainerName { get; }
    public ITradingModel Model { get; }

    /// <summary>
    /// The classes present in the training slice. A window legitimately containing only two of the
    /// three drives a two-slot Score vector, and inference must know that to avoid reading an
    /// absent class off another class's slot.
    /// </summary>
    public IReadOnlyCollection<TradeLabel> TrainedClasses { get; }

    /// <summary>
    /// Writes the fitted pipeline and, alongside it, the metadata sidecar without which the model
    /// cannot be used safely. The overload taking no metadata is deliberately absent: a model saved
    /// without its timeframe, horizon, label definition and tuned thresholds is a liability.
    /// </summary>
    public void Save(
        string path,
        ClassifierOptions options,
        int candleIntervalMinutes,
        string instrument,
        DateTimeOffset trainedFrom,
        DateTimeOffset trainedTo,
        int trainingRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        Context.Model.Save(Transformer, InputSchema, path);

        new ClassifierModelArtifact
        {
            SchemaVersion = ClassifierModelArtifact.CurrentSchemaVersion,
            TrainerName = TrainerName,
            CreatedAt = DateTimeOffset.UtcNow,
            CandleIntervalMinutes = candleIntervalMinutes,
            Instrument = instrument,
            TrainedFrom = trainedFrom,
            TrainedTo = trainedTo,
            TrainingRows = trainingRows,
            TrainedClasses = [.. TrainedClasses.Select(label => label.ToString())],
            PredictionHorizon = options.PredictionHorizon,
            AtrTargetMultiplier = options.AtrTargetMultiplier,
            UseMaximumExcursionLabels = options.UseMaximumExcursionLabels,
            TrainingStride = options.TrainingStride,
            EnabledGroups = options.EnabledGroups.ToString(),
            FeatureNames = FeatureSchema.Names,
            BuyProbabilityThreshold = options.BuyProbabilityThreshold,
            SellProbabilityThreshold = options.SellProbabilityThreshold,
            ConfigurationHash = ClassifierModelArtifact.ComputeHash(
                options, candleIntervalMinutes, FeatureSchema.Names)
        }.Save(path);
    }

    public IReadOnlyList<Prediction> PredictAll(IReadOnlyList<LabeledFeatureRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Select(row => Model.Predict(row.Features)).ToArray();
    }

    public void Dispose() => (Model as IDisposable)?.Dispose();
}

/// <summary>
/// Guards a training slice before it reaches a trainer.
/// </summary>
internal static class TrainingPreconditions
{
    /// <summary>
    /// A slice containing one distinct class cannot train a multiclass model. LightGBM's own
    /// failure is <c>"Number of classes should be specified and greater than 1"</c> thrown from
    /// native code, which says nothing about which window or why - and a quiet walk-forward period
    /// where every row labels NO_TRADE reaches it legitimately, so this is a data condition to
    /// report, not a bug to crash on.
    /// </summary>
    public static void RequireMultipleClasses(IReadOnlyList<LabeledFeatureRow> rows, string trainerName)
    {
        HashSet<TradeLabel> present = [.. rows.Select(row => row.Label)];
        if (present.Count >= 2)
            return;

        throw new InvalidOperationException(
            $"{trainerName} cannot train on {rows.Count} rows that are all " +
            $"{(present.Count == 1 ? present.First().ToString() : "of no class")}: a multiclass " +
            "model needs at least two classes. Widen the training window, or lower " +
            "AtrTargetMultiplier / raise PredictionHorizon so that some rows label BUY or SELL.");
    }
}

/// <summary>Knobs worth exposing per section 34; the rest stay at LightGBM's defaults.</summary>
public sealed record LightGbmTrainingOptions
{
    public int NumberOfLeaves { get; init; } = 31;
    public int NumberOfIterations { get; init; } = 200;
    public double LearningRate { get; init; } = 0.1;
    public int MinimumExampleCountPerLeaf { get; init; } = 40;

    /// <summary>Section 18: reweight the classes rather than resampling them.</summary>
    public bool UseClassWeights { get; init; } = true;

    public int Seed { get; init; }
}

/// <summary>
/// Section 13's primary model: a LightGBM multiclass classifier over the section 8 feature set.
/// </summary>
public sealed class LightGbmModelTrainer : IModelTrainer
{
    private readonly LightGbmTrainingOptions _options;

    public LightGbmModelTrainer(LightGbmTrainingOptions? options = null) =>
        _options = options ?? new LightGbmTrainingOptions();

    public string Name => "LightGBM";

    public TrainedModel Train(IReadOnlyList<LabeledFeatureRow> train, FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(schema);
        if (train.Count == 0)
            throw new ArgumentException("Cannot train on an empty set.", nameof(train));
        TrainingPreconditions.RequireMultipleClasses(train, Name);

        MLContext context = new(seed: _options.Seed);
        IReadOnlyDictionary<TradeLabel, float>? weights = _options.UseClassWeights
            ? MlDataView.InverseFrequencyWeights(train)
            : null;
        IDataView data = MlDataView.Create(context, train, schema, weights);

        // KeyOrdinality.ByValue keeps the key order tied to the numeric label (0 Sell, 1 NoTrade,
        // 2 Buy) instead of to whichever class happened to appear first in this slice, which would
        // otherwise reorder the Score vector between walk-forward windows.
        var pipeline = context.Transforms.Conversion
            .MapValueToKey(
                outputColumnName: MlDataView.KeyLabelColumn,
                inputColumnName: MlDataView.LabelColumn,
                keyOrdinality: Microsoft.ML.Transforms.ValueToKeyMappingEstimator.KeyOrdinality.ByValue)
            .Append(context.MulticlassClassification.Trainers.LightGbm(new Microsoft.ML.Trainers.LightGbm.LightGbmMulticlassTrainer.Options
            {
                LabelColumnName = MlDataView.KeyLabelColumn,
                FeatureColumnName = MlDataView.FeatureColumn,
                ExampleWeightColumnName = _options.UseClassWeights ? MlDataView.WeightColumn : null,
                NumberOfLeaves = _options.NumberOfLeaves,
                NumberOfIterations = _options.NumberOfIterations,
                LearningRate = _options.LearningRate,
                MinimumExampleCountPerLeaf = _options.MinimumExampleCountPerLeaf,
                Seed = _options.Seed,
                Verbose = false,
                Silent = true
            }));

        ITransformer transformer = pipeline.Fit(data);
        return new TrainedModel(context, transformer, data.Schema, schema, Name, PresentClasses(train));
    }

    internal static TradeLabel[] PresentClasses(IReadOnlyList<LabeledFeatureRow> rows) =>
        [.. rows.Select(row => row.Label).Distinct().OrderBy(label => (int)label)];
}

/// <summary>
/// Section 14's baseline: multinomial logistic regression.
/// <para>
/// Its purpose is diagnostic. Section 14 is explicit that if LightGBM cannot beat this, the
/// problem is the features or the target, and no amount of boosting-parameter tuning will fix it.
/// </para>
/// </summary>
public sealed class LogisticRegressionBaselineTrainer : IModelTrainer
{
    private readonly bool _useClassWeights;
    private readonly int _seed;

    public LogisticRegressionBaselineTrainer(bool useClassWeights = true, int seed = 0)
    {
        _useClassWeights = useClassWeights;
        _seed = seed;
    }

    public string Name => "LogisticRegression";

    public TrainedModel Train(IReadOnlyList<LabeledFeatureRow> train, FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(schema);
        if (train.Count == 0)
            throw new ArgumentException("Cannot train on an empty set.", nameof(train));
        TrainingPreconditions.RequireMultipleClasses(train, Name);

        MLContext context = new(seed: _seed);
        IReadOnlyDictionary<TradeLabel, float>? weights = _useClassWeights
            ? MlDataView.InverseFrequencyWeights(train)
            : null;
        IDataView data = MlDataView.Create(context, train, schema, weights);

        // Unlike LightGBM, a linear model genuinely needs the scaling section 17 describes as
        // merely preferable - unnormalised columns dominate the gradient. NormalizeMeanVariance is
        // fitted on the training slice only, so it cannot leak test-period statistics backwards.
        var pipeline = context.Transforms.Conversion
            .MapValueToKey(
                outputColumnName: MlDataView.KeyLabelColumn,
                inputColumnName: MlDataView.LabelColumn,
                keyOrdinality: Microsoft.ML.Transforms.ValueToKeyMappingEstimator.KeyOrdinality.ByValue)
            .Append(context.Transforms.NormalizeMeanVariance(MlDataView.FeatureColumn))
            .Append(context.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: MlDataView.KeyLabelColumn,
                featureColumnName: MlDataView.FeatureColumn,
                exampleWeightColumnName: _useClassWeights ? MlDataView.WeightColumn : null));

        ITransformer transformer = pipeline.Fit(data);
        return new TrainedModel(
            context, transformer, data.Schema, schema, Name, LightGbmModelTrainer.PresentClasses(train));
    }
}
