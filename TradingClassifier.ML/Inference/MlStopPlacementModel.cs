using Microsoft.ML;
using Microsoft.ML.Data;
using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Inference;

internal sealed class StopInput
{
    [VectorType]
    public float[] Features { get; set; } = [];
    public float Label { get; set; }
}

internal sealed class StopOutput
{
    public float Score { get; set; }
}

/// <summary>
/// Regression model predicting adverse excursion in ATR — the ML catalogue's Phase 5 "MAE
/// prediction" put to the use it was built for: placing the stop.
/// <para>
/// Trained on candidates whose outcome is already known, and scored on candidates that follow them.
/// The target is deliberately ATR-denominated rather than R-denominated: R is defined by the stop
/// distance, so predicting adverse excursion in R to choose a stop is circular.
/// </para>
/// </summary>
public sealed class MlStopPlacementModel : IStopPlacementModel
{
    private readonly PredictionEngine<StopInput, StopOutput> _engine;
    private readonly int _width;

    private MlStopPlacementModel(
        PredictionEngine<StopInput, StopOutput> engine,
        IReadOnlyList<string> featureNames)
    {
        _engine = engine;
        FeatureNames = featureNames;
        _width = featureNames.Count;
    }

    /// <summary>
    /// Persists the model plus its feature names.
    /// <para>
    /// Persistence rather than fitting at backtest time is the whole point: fitting inside the run
    /// would train on the period being scored, which is the look-ahead that made the classifier's
    /// +5,926 meaningless (§3.23). A saved model forces the training window to be chosen explicitly.
    /// </para>
    /// </summary>
    public static void Save(
        string path, ITransformer model, DataViewSchema schema, IReadOnlyList<string> featureNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        new MLContext(seed: 0).Model.Save(model, schema, path);
        File.WriteAllLines(path + ".features", featureNames);
    }

    public static MlStopPlacementModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string namesPath = path + ".features";
        if (!File.Exists(namesPath))
            throw new FileNotFoundException($"Stop model feature list '{namesPath}' is missing.", namesPath);

        string[] names = File.ReadAllLines(namesPath);
        MLContext context = new(seed: 0);
        ITransformer model = context.Model.Load(path, out _);

        SchemaDefinition schema = SchemaDefinition.Create(typeof(StopInput));
        schema[nameof(StopInput.Features)].ColumnType =
            new VectorDataViewType(NumberDataViewType.Single, names.Length);

        return new MlStopPlacementModel(
            context.Model.CreatePredictionEngine<StopInput, StopOutput>(
                model, inputSchemaDefinition: schema),
            names);
    }

    public IReadOnlyList<string> FeatureNames { get; }

    /// <summary>Last fitted transformer and schema, so a caller can persist what it just trained.</summary>
    public static ITransformer? Fitted { get; private set; }

    public static DataViewSchema? FittedSchema { get; private set; }

    /// <summary>
    /// Fits on rows whose adverse excursion is known. Returns null when the sample is too small to
    /// fit anything trustworthy — an unfitted model that silently returns a constant would make the
    /// A/B against the structural stop meaningless.
    /// </summary>
    public static MlStopPlacementModel? Fit(
        IReadOnlyList<(float[] Features, float AdverseAtr)> rows,
        IReadOnlyList<string> featureNames,
        int minimumRows = 100,
        int seed = 11,
        bool useLightGbm = true)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(featureNames);
        if (rows.Count < minimumRows)
            return null;

        MLContext context = new(seed);
        SchemaDefinition schema = SchemaDefinition.Create(typeof(StopInput));
        schema[nameof(StopInput.Features)].ColumnType =
            new VectorDataViewType(NumberDataViewType.Single, featureNames.Count);

        IDataView view = context.Data.LoadFromEnumerable(
            rows.Select(row => new StopInput { Features = row.Features, Label = row.AdverseAtr }),
            schema);

        // Constrained on purpose: adverse excursion is weakly predictable (rho ~0.06), and a model
        // with capacity to chase that signal will chase noise instead.
        //
        // The regularised linear option is not a fallback — §3.18 measured Sdca at rho 0.0252
        // against LightGBM's 0.0044 on identical rows, i.e. the boosted model lost to linear by 6x.
        // When the signal is this weak, capacity is a liability rather than an advantage.
        var estimator = context.Transforms.NormalizeMeanVariance("Features")
            .Append(useLightGbm
                ? context.Regression.Trainers.LightGbm(
                    labelColumnName: "Label", featureColumnName: "Features",
                    numberOfLeaves: 8, minimumExampleCountPerLeaf: 40,
                    numberOfIterations: 100, learningRate: 0.05)
                : (IEstimator<ITransformer>)context.Regression.Trainers.Sdca(
                    labelColumnName: "Label", featureColumnName: "Features",
                    l2Regularization: 0.5f, maximumNumberOfIterations: 100));

        ITransformer model = estimator.Fit(view);
        Fitted = model;
        FittedSchema = view.Schema;
        return new MlStopPlacementModel(
            context.Model.CreatePredictionEngine<StopInput, StopOutput>(
                model, inputSchemaDefinition: schema),
            featureNames);
    }

    public decimal PredictAdverseExcursionAtr(FeatureVector features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Values.Length != _width)
        {
            throw new ArgumentException(
                $"Stop model expects {_width} features but was given {features.Values.Length}. " +
                "Scoring anyway would place a stop from misaligned columns.",
                nameof(features));
        }

        StopOutput output = _engine.Predict(new StopInput { Features = features.Values, Label = 0f });
        return float.IsFinite(output.Score) && output.Score > 0 ? (decimal)output.Score : 0m;
    }
}
