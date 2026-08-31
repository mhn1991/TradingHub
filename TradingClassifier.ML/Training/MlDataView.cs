using Microsoft.ML;
using Microsoft.ML.Data;
using TradingClassifier.Features;

namespace TradingClassifier.ML.Training;

/// <summary>
/// The row shape ML.NET sees. The feature vector's length is not known at compile time - it
/// depends on which groups section 35 has enabled - so the vector size is stamped onto a runtime
/// <see cref="SchemaDefinition"/> instead of a <c>[VectorType]</c> attribute.
/// </summary>
public sealed class MlFeatureRow
{
    public float[] Features { get; set; } = [];

    /// <summary>Raw 0/1/2. Mapped to an ML.NET key inside the pipeline, never before.</summary>
    public float Label { get; set; }

    /// <summary>Section 18's class weighting. 1 when weighting is off.</summary>
    public float Weight { get; set; } = 1f;
}

public static class MlDataView
{
    public const string FeatureColumn = "Features";
    public const string LabelColumn = "Label";
    public const string WeightColumn = "Weight";
    public const string KeyLabelColumn = "LabelKey";

    /// <summary>
    /// Wraps rows as an <see cref="IDataView"/>, declaring the feature vector's true width so
    /// LightGBM sees a fixed-size vector rather than a variable-length one.
    /// </summary>
    public static IDataView Create(
        MLContext context,
        IReadOnlyList<LabeledFeatureRow> rows,
        FeatureSchema schema,
        IReadOnlyDictionary<TradeLabel, float>? classWeights = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(schema);

        SchemaDefinition definition = SchemaDefinition.Create(typeof(MlFeatureRow));
        definition[nameof(MlFeatureRow.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, schema.Count);

        IEnumerable<MlFeatureRow> materialised = rows.Select(row =>
        {
            if (row.Features.Values.Length != schema.Count)
            {
                throw new ArgumentException(
                    $"Row at {row.Features.Timestamp:O} has {row.Features.Values.Length} features but the " +
                    $"schema declares {schema.Count}. The dataset and the schema were built from different options.",
                    nameof(rows));
            }

            return new MlFeatureRow
            {
                Features = row.Features.Values,
                Label = (float)row.Label,
                Weight = classWeights?.GetValueOrDefault(row.Label, 1f) ?? 1f
            };
        }).ToArray();

        return context.Data.LoadFromEnumerable(materialised, definition);
    }

    /// <summary>
    /// Section 18's class weights: inverse frequency, normalised so the mean weight is 1.
    /// <para>
    /// This reweights rather than resamples on purpose. Section 18 explicitly forbids dropping
    /// NO_TRADE rows to balance the classes - those rows are the majority of real market time, and
    /// a model that never saw them would treat every bar as an opportunity.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<TradeLabel, float> InverseFrequencyWeights(
        IReadOnlyList<LabeledFeatureRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new ArgumentException("Cannot weight an empty dataset.", nameof(rows));

        Dictionary<TradeLabel, int> counts = [];
        foreach (LabeledFeatureRow row in rows)
            counts[row.Label] = counts.GetValueOrDefault(row.Label) + 1;

        Dictionary<TradeLabel, float> weights = [];
        foreach ((TradeLabel label, int count) in counts)
            weights[label] = (float)rows.Count / (counts.Count * count);

        return weights;
    }
}
