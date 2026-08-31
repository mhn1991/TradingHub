using Microsoft.ML;
using Microsoft.ML.Data;
using TradingClassifier.Features;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Inference;

internal sealed class MlPredictionRow
{
    [ColumnName("Score")]
    public float[] Score { get; set; } = [];
}

/// <summary>
/// An <see cref="ITradingModel"/> backed by a trained ML.NET pipeline.
/// <para>
/// This is the only place a model file becomes probabilities. It lives here rather than in the
/// core library so that <c>Agent</c> - which is AOT-published by Simulator.AotSmoke - never
/// references Microsoft.ML.
/// </para>
/// </summary>
public sealed class MlNetTradingModel : ITradingModel, IDisposable
{
    private readonly PredictionEngine<MlFeatureRow, MlPredictionRow> _engine;
    private readonly int[] _scoreIndexByLabel;
    private readonly Lock _gate = new();
    private readonly int _featureCount;

    private MlNetTradingModel(
        PredictionEngine<MlFeatureRow, MlPredictionRow> engine,
        int[] scoreIndexByLabel,
        IReadOnlyList<string> featureNames)
    {
        _engine = engine;
        _scoreIndexByLabel = scoreIndexByLabel;
        _featureCount = featureNames.Count;
        FeatureNames = featureNames;
    }

    public IReadOnlyList<string> FeatureNames { get; }

    /// <summary>
    /// Wraps a fitted transformer. <c>inputSchema</c> is unused for scoring - the engine is built
    /// from a runtime SchemaDefinition instead - but stays on the signature so callers hold on to
    /// the schema the model was fitted against rather than losing it before they save.
    /// </summary>
    public static MlNetTradingModel Create(
        MLContext context,
        ITransformer model,
        DataViewSchema inputSchema,
        IReadOnlyList<string> featureNames,
        IReadOnlyCollection<TradeLabel>? trainedClasses = null)
    {
        _ = inputSchema;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(featureNames);

        SchemaDefinition definition = SchemaDefinition.Create(typeof(MlFeatureRow));
        definition[nameof(MlFeatureRow.Features)].ColumnType =
            new VectorDataViewType(NumberDataViewType.Single, featureNames.Count);

        // The SchemaDefinition overload, not the DataViewSchema one: the feature vector's width is
        // only known at runtime, so the engine has to be told it explicitly.
        PredictionEngine<MlFeatureRow, MlPredictionRow> engine =
            context.Model.CreatePredictionEngine<MlFeatureRow, MlPredictionRow>(
                model,
                ignoreMissingColumns: false,
                inputSchemaDefinition: definition,
                outputSchemaDefinition: null);

        return new MlNetTradingModel(
            engine, ResolveScoreOrder(engine.OutputSchema, trainedClasses), featureNames);
    }

    /// <summary>
    /// Loads a model together with its metadata sidecar, validating that the supplied configuration
    /// matches the one it was trained under.
    /// <para>
    /// Feature names are read from the sidecar rather than supplied by the caller, so a model can no
    /// longer be scored against a schema it never saw.
    /// </para>
    /// </summary>
    public static (MlNetTradingModel Model, ClassifierModelArtifact Metadata) Load(
        string path,
        TradingClassifier.Configuration.ClassifierOptions options,
        int candleIntervalMinutes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        ClassifierModelArtifact metadata = ClassifierModelArtifact.Load(path);
        metadata.EnsureCompatible(options, candleIntervalMinutes, metadata.FeatureNames);

        MLContext context = new(seed: 0);
        ITransformer model = context.Model.Load(path, out DataViewSchema schema);

        TradeLabel[] trained = [.. metadata.TrainedClasses.Select(Enum.Parse<TradeLabel>)];
        return (Create(context, model, schema, metadata.FeatureNames, trained), metadata);
    }

    /// <summary>
    /// Works out which slot of the Score vector belongs to which class.
    /// <para>
    /// Not cosmetic: a training slice that happens to contain no SELL row yields a two-class model
    /// whose Score vector is length 2, and reading it positionally would silently report NO_TRADE
    /// probabilities as SELL probabilities. The key values are read back from the schema so the
    /// mapping is whatever the model actually learned.
    /// </para>
    /// </summary>
    private static int[] ResolveScoreOrder(
        DataViewSchema outputSchema,
        IReadOnlyCollection<TradeLabel>? trainedClasses)
    {
        int[] map = [-1, -1, -1];

        // Preferred path: the trainer tells us which classes it actually saw. MapValueToKey is
        // configured with KeyOrdinality.ByValue, so the Score vector's slots are the present label
        // values in ascending order - slot 0 is the smallest class present, and a class that never
        // appeared has no slot at all.
        //
        // This is derived rather than read from the SlotNames annotation because that annotation's
        // contents are not guaranteed to be the original label values, and guessing wrong maps an
        // absent class onto another class's probability - fabricating signals for a direction the
        // model was never trained on.
        if (trainedClasses is { Count: > 0 })
        {
            TradeLabel[] ordered = [.. trainedClasses.Distinct().OrderBy(label => (int)label)];
            for (int slot = 0; slot < ordered.Length; slot++)
                map[(int)ordered[slot]] = slot;
            return map;
        }


        DataViewSchema.Column? score = outputSchema.GetColumnOrNull("Score");
        if (score is not null)
        {
            VBuffer<ReadOnlyMemory<char>> slotNames = default;
            try
            {
                score.Value.Annotations.GetValue("SlotNames", ref slotNames);
                ReadOnlySpan<ReadOnlyMemory<char>> names = slotNames.GetValues();
                for (int slot = 0; slot < names.Length; slot++)
                {
                    if (float.TryParse(names[slot].Span, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float value)
                        && value is >= 0 and <= 2)
                    {
                        map[(int)value] = slot;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // No SlotNames annotation on this pipeline; fall through to the positional default.
            }
        }

        // The positional fallback applies ONLY when the annotation resolved nothing at all.
        //
        // Applying it per-label is a correctness bug: training legitimately produces a two-class
        // model when a window contains no SELL rows, and its Score vector then has two slots with
        // SlotNames ["1","2"] -> NoTrade=slot 0, Buy=slot 1. Filling the unresolved Sell with its
        // positional index would point it at slot 0 - NoTrade's probability - manufacturing SELL
        // signals out of NoTrade confidence and making the three probabilities sum above 1.
        //
        // A label left unresolved after a successful annotation read is genuinely absent from
        // training, so it stays at -1 and Read() reports 0 for it.
        bool resolvedAnything = false;
        foreach (int slot in map)
        {
            if (slot >= 0) { resolvedAnything = true; break; }
        }

        if (!resolvedAnything)
        {
            for (int label = 0; label < map.Length; label++)
                map[label] = label;
        }

        return map;
    }

    public Prediction Predict(FeatureVector features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Values.Length != _featureCount)
        {
            throw new ArgumentException(
                $"Model expects {_featureCount} features but was given {features.Values.Length}. " +
                "The agent's ClassifierOptions must match the options the model was trained with.",
                nameof(features));
        }

        MlPredictionRow row;
        // PredictionEngine is explicitly not thread-safe and an agent supervisor may evaluate
        // several instruments in parallel against one shared model.
        lock (_gate)
        {
            row = _engine.Predict(new MlFeatureRow { Features = features.Values });
        }

        float[] scores = row.Score;
        return new Prediction
        {
            SellProbability = Read(scores, _scoreIndexByLabel[(int)TradeLabel.Sell]),
            NoTradeProbability = Read(scores, _scoreIndexByLabel[(int)TradeLabel.NoTrade]),
            BuyProbability = Read(scores, _scoreIndexByLabel[(int)TradeLabel.Buy])
        };

        // A class absent from training has no slot at all; its probability is 0, not an exception -
        // the signal generator then simply never clears that side's threshold.
        static double Read(float[] scores, int index) =>
            index >= 0 && index < scores.Length ? scores[index] : 0d;
    }

    public void Dispose() => _engine.Dispose();
}
