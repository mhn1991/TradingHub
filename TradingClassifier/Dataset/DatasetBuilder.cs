using TradingClassifier.Configuration;
using TradingClassifier.Features;
using TradingClassifier.Labels;

namespace TradingClassifier.Dataset;

/// <summary>A built dataset plus the schema its rows are aligned to.</summary>
public sealed record ClassifierDataset
{
    public required FeatureSchema Schema { get; init; }
    public required IReadOnlyList<LabeledFeatureRow> Rows { get; init; }

    /// <summary>Rows discarded because a data gap stretched their label window. See section 8 of the review.</summary>
    public int RowsDroppedForGaps { get; init; }

    /// <summary>The modal bar spacing inferred from the candles, used for the gap check.</summary>
    public TimeSpan? InferredSpacing { get; init; }

    /// <summary>Section 18: the class balance, which is expected to be NO_TRADE-heavy.</summary>
    public IReadOnlyDictionary<TradeLabel, int> ClassCounts => Rows
        .GroupBy(row => row.Label)
        .ToDictionary(group => group.Key, group => group.Count());

    public DateTimeOffset Start => Rows.Count > 0
        ? Rows[0].Features.Timestamp
        : throw new InvalidOperationException("The dataset is empty.");

    public DateTimeOffset End => Rows.Count > 0
        ? Rows[^1].Features.Timestamp
        : throw new InvalidOperationException("The dataset is empty.");
}

/// <summary>
/// Section 26's training pipeline, up to the point where a model is fitted: load, sort, compute
/// indicators and features, drop warm-up rows, generate labels, drop rows with no future.
/// </summary>
public sealed class DatasetBuilder
{
    private readonly ClassifierOptions _options;

    public DatasetBuilder(ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>Modal gap between consecutive candles - robust to weekends and holidays.</summary>
    private static TimeSpan? InferSpacing(IReadOnlyList<ClassifierCandle> candles)
    {
        if (candles.Count < 3)
            return null;

        Dictionary<long, int> counts = [];
        for (int index = 1; index < candles.Count; index++)
        {
            long ticks = (candles[index].Timestamp - candles[index - 1].Timestamp).Ticks;
            if (ticks > 0)
                counts[ticks] = counts.GetValueOrDefault(ticks) + 1;
        }

        if (counts.Count == 0)
            return null;

        long modal = counts.OrderByDescending(pair => pair.Value).First().Key;
        return TimeSpan.FromTicks(modal);
    }

    public ClassifierDataset Build(IReadOnlyList<ClassifierCandle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        // Section 26 step 2. Sorting a copy rather than demanding sorted input keeps callers from
        // having to know; FeatureEngine still rejects out-of-order candles as a second line.
        ClassifierCandle[] ordered = candles.OrderBy(candle => candle.Timestamp).ToArray();

        TimeSpan? spacing = InferSpacing(ordered);
        FeatureEngine engine = new(_options);
        AtrThresholdLabelGenerator labeller = new(_options, spacing);
        List<LabeledFeatureRow> rows = [];
        int droppedForGaps = 0;

        for (int index = 0; index < ordered.Length; index++)
        {
            FeatureVector? features = engine.Update(ordered[index]);
            if (features is null)
                continue;   // still warming up

            var labelled = labeller.Label(ordered, index, features.LabelAtr);
            if (labelled is null)
            {
                // Distinguish "ran off the end" from "a gap stretched the horizon" so the caller
                // can see data quality rather than inferring it from a short dataset.
                if (index + _options.PredictionHorizon < ordered.Length)
                    droppedForGaps++;
                continue;
            }

            rows.Add(new LabeledFeatureRow
            {
                Features = features,
                Label = labelled.Value.Label,
                LabelExcursionAtr = labelled.Value.ExcursionAtr
            });
        }

        return new ClassifierDataset
        {
            Schema = engine.Schema,
            Rows = rows,
            RowsDroppedForGaps = droppedForGaps,
            InferredSpacing = spacing
        };
    }
}
