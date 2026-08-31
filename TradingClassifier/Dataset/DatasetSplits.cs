using TradingClassifier.Features;

namespace TradingClassifier.Dataset;

/// <summary>A chronological train/validation/test partition - section 15.</summary>
public sealed record DatasetSplit
{
    public required IReadOnlyList<LabeledFeatureRow> Train { get; init; }
    public required IReadOnlyList<LabeledFeatureRow> Validation { get; init; }
    public required IReadOnlyList<LabeledFeatureRow> Test { get; init; }
    public string Label { get; init; } = "split";
}

/// <summary>
/// Section 15 and 16.
/// <para>
/// Both helpers here slice an already time-ordered row list by position or by date. Neither ever
/// shuffles: section 15 is explicit that random splitting destroys the temporal structure, and a
/// shuffled split is the single easiest way to produce a spectacular and completely fake result.
/// </para>
/// </summary>
public static class DatasetSplitter
{
    /// <summary>
    /// Section 15's chronological split, by fraction. <c>embargoRows</c> drops that many rows from
    /// the tail of the train and validation segments - see <see cref="Embargo"/>, which explains
    /// why leaving it at zero leaks the label horizon across the boundary.
    /// </summary>
    public static DatasetSplit Chronological(
        IReadOnlyList<LabeledFeatureRow> rows,
        double trainFraction = 0.6,
        double validationFraction = 0.2,
        int embargoRows = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (trainFraction <= 0 || validationFraction <= 0 || trainFraction + validationFraction >= 1)
            throw new ArgumentException("Train and validation fractions must be positive and leave room for a test set.");

        int trainEnd = (int)(rows.Count * trainFraction);
        int validationEnd = trainEnd + (int)(rows.Count * validationFraction);

        return new DatasetSplit
        {
            Train = Embargo(Slice(rows, 0, trainEnd), embargoRows),
            Validation = Embargo(Slice(rows, trainEnd, validationEnd), embargoRows),
            Test = Slice(rows, validationEnd, rows.Count),
            Label = "chronological"
        };
    }

    /// <summary>
    /// Section 16's rolling walk-forward windows. Each window trains on
    /// <paramref name="trainSpan"/>, validates on the next <paramref name="validationSpan"/> and
    /// tests on the <paramref name="testSpan"/> after that; the whole frame then advances by
    /// <paramref name="testSpan"/> so consecutive test periods tile the dataset without overlap.
    /// </summary>
    public static IReadOnlyList<DatasetSplit> WalkForward(
        IReadOnlyList<LabeledFeatureRow> rows,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan,
        int embargoRows = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (trainSpan <= TimeSpan.Zero || validationSpan <= TimeSpan.Zero || testSpan <= TimeSpan.Zero)
            throw new ArgumentException("Walk-forward spans must all be positive.");
        if (rows.Count == 0)
            return [];

        List<DatasetSplit> windows = [];
        DateTimeOffset datasetEnd = rows[^1].Features.Timestamp;
        DateTimeOffset trainStart = rows[0].Features.Timestamp;
        int window = 1;

        while (true)
        {
            DateTimeOffset trainEnd = trainStart + trainSpan;
            DateTimeOffset validationEnd = trainEnd + validationSpan;
            DateTimeOffset testEnd = validationEnd + testSpan;

            // Stop rather than emit a partial final window: a test period covering three days
            // instead of a month is not comparable to the others, and section 36 asks whether
            // results are stable *across periods*.
            if (testEnd > datasetEnd)
                break;

            DatasetSplit split = new()
            {
                Train = Embargo(Between(rows, trainStart, trainEnd), embargoRows),
                Validation = Embargo(Between(rows, trainEnd, validationEnd), embargoRows),
                Test = Between(rows, validationEnd, testEnd),
                Label = $"window-{window}"
            };

            if (split.Train.Count > 0 && split.Validation.Count > 0 && split.Test.Count > 0)
            {
                windows.Add(split);
                window++;
            }

            trainStart += testSpan;
        }

        return windows;
    }

    /// <summary>
    /// Drops the last <paramref name="rowCount"/> rows of a segment.
    /// <para>
    /// The blueprint does not call for this, and it is the one place this implementation
    /// deliberately goes beyond it. A row at time <c>t</c> carries a label built from candles up to
    /// <c>t + PredictionHorizon</c>. Without an embargo, the final <c>PredictionHorizon</c> rows of
    /// the train segment were labelled using candles that live inside the validation segment - so
    /// the model is fitted on information from the period it is about to be judged on. That is the
    /// same class of leak section 25 forbids, merely relocated from the features to the split
    /// boundary, and it inflates out-of-sample scores in exactly the way that makes a strategy look
    /// tradeable and then fail live. Pass <c>PredictionHorizon</c> here.
    /// </para>
    /// </summary>
    private static IReadOnlyList<LabeledFeatureRow> Embargo(IReadOnlyList<LabeledFeatureRow> rows, int rowCount)
    {
        if (rowCount <= 0)
            return rows;
        return rows.Count <= rowCount ? [] : Slice(rows, 0, rows.Count - rowCount);
    }

    private static IReadOnlyList<LabeledFeatureRow> Slice(IReadOnlyList<LabeledFeatureRow> rows, int start, int end)
    {
        List<LabeledFeatureRow> slice = new(Math.Max(0, end - start));
        for (int index = start; index < end && index < rows.Count; index++)
            slice.Add(rows[index]);
        return slice;
    }

    /// <summary>Half-open [start, end) so adjacent periods never share a row.</summary>
    private static IReadOnlyList<LabeledFeatureRow> Between(
        IReadOnlyList<LabeledFeatureRow> rows,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        List<LabeledFeatureRow> slice = [];
        foreach (LabeledFeatureRow row in rows)
        {
            if (row.Features.Timestamp >= start && row.Features.Timestamp < end)
                slice.Add(row);
        }
        return slice;
    }
}
