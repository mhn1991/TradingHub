using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.Evaluation;

public readonly record struct FeatureImportance(string Feature, double MacroF1Drop, int Rank);

/// <summary>
/// Section 23's feature importance, by permutation.
/// <para>
/// Permutation importance is used in preference to LightGBM's own split/gain counts because it
/// measures what section 23 actually asks about - whether a feature carries predictive information
/// on held-out data - rather than how often the tree builder happened to split on it. A
/// high-cardinality noise column can accumulate a large split count while contributing nothing.
/// </para>
/// <para>
/// It also works against <see cref="ITradingModel"/> rather than a specific ML.NET type, so the
/// section 14 baseline and the LightGBM model are measured by the identical procedure.
/// </para>
/// </summary>
public static class PermutationImportance
{
    /// <summary>
    /// Shuffles each feature column in turn and reports how far macro-F1 falls. Section 23 closes
    /// with a warning that matters here: a high ranking on one period is not evidence, so run this
    /// across several walk-forward windows before believing it.
    /// </summary>
    public static IReadOnlyList<FeatureImportance> Compute(
        ITradingModel model,
        IReadOnlyList<LabeledFeatureRow> rows,
        FeatureSchema schema,
        int seed = 0,
        int repeats = 1)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(schema);
        if (repeats <= 0)
            throw new ArgumentOutOfRangeException(nameof(repeats));
        if (rows.Count == 0)
            return [];

        TradeLabel[] actual = rows.Select(row => row.Label).ToArray();
        double baseline = ClassificationEvaluator
            .Evaluate(actual, rows.Select(row => model.Predict(row.Features)).ToArray())
            .MacroF1;

        // One clone reused across every feature and repeat: permuting a column in place and then
        // restoring it keeps allocation flat over a 40-plus column schema.
        float[][] working = rows.Select(row => (float[])row.Features.Values.Clone()).ToArray();
        FeatureVector[] vectors = rows
            .Select((row, index) => new FeatureVector
            {
                Timestamp = row.Features.Timestamp,
                Close = row.Features.Close,
                LabelAtr = row.Features.LabelAtr,
                Values = working[index]
            })
            .ToArray();

        Random random = new(seed);
        List<(string Feature, double Drop)> drops = [];

        for (int column = 0; column < schema.Count; column++)
        {
            double total = 0;
            for (int repeat = 0; repeat < repeats; repeat++)
            {
                float[] original = new float[working.Length];
                for (int row = 0; row < working.Length; row++)
                    original[row] = working[row][column];

                Shuffle(working, column, random);

                double permuted = ClassificationEvaluator
                    .Evaluate(actual, vectors.Select(model.Predict).ToArray())
                    .MacroF1;
                total += baseline - permuted;

                for (int row = 0; row < working.Length; row++)
                    working[row][column] = original[row];
            }

            drops.Add((schema.Features[column].Name, total / repeats));
        }

        return drops
            .OrderByDescending(item => item.Drop)
            .Select((item, index) => new FeatureImportance(item.Feature, item.Drop, index + 1))
            .ToArray();
    }

    private static void Shuffle(float[][] rows, int column, Random random)
    {
        for (int index = rows.Length - 1; index > 0; index--)
        {
            int swap = random.Next(index + 1);
            (rows[index][column], rows[swap][column]) = (rows[swap][column], rows[index][column]);
        }
    }
}
