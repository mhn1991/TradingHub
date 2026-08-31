using System.Globalization;
using System.Text.Json;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Experiments;

/// <summary>
/// One realised trade from a primary strategy's trade log.
/// <para>
/// <c>SignalAt</c> is when the strategy actually decided, and is the bar the classifier must be
/// asked about. It is deliberately distinct from <c>Opened</c>: the fill lands one execution bar
/// later, so scoring the open bar would hand the filter a candle the primary strategy never saw.
/// </para>
/// </summary>
public readonly record struct PrimaryTrade(
    int Number,
    TradeLabel Side,
    DateTimeOffset SignalAt,
    DateTimeOffset Opened,
    DateTimeOffset Closed,
    decimal R,
    decimal Net);

/// <summary>How a candidate trade is judged against the classifier's opinion.</summary>
public enum FilterMode
{
    /// <summary>Keep only trades the classifier actively agrees with at the threshold.</summary>
    Agreement,

    /// <summary>Keep everything except trades the classifier confidently contradicts.</summary>
    NotOpposed
}

public sealed record FilterOutcome
{
    public required string Label { get; init; }
    public required int Kept { get; init; }
    public required int Dropped { get; init; }
    public required TradingReport Report { get; init; }
}

/// <summary>One frozen decision rule: which feature set, which mode, which threshold.</summary>
public readonly record struct FilterRule(string FeatureSet, FilterMode Mode, double Threshold)
{
    public override string ToString() => $"{FeatureSet} / {Mode} >= {Threshold:F2}";
}

/// <summary>What one walk-forward fold selected on validation, and what that rule then did on test.</summary>
public sealed record FoldSelection
{
    public required int Fold { get; init; }
    public required DateTimeOffset TestStart { get; init; }
    public required DateTimeOffset TestEnd { get; init; }
    /// <summary>Null when no rule cleared the minimum-kept guard, in which case the fold takes nothing.</summary>
    public required FilterRule? Rule { get; init; }
    public required int ValidationCandidates { get; init; }
    public required int ValidationKept { get; init; }
    /// <summary>Primary metric: R is sizing-independent, so it is what selection optimises.</summary>
    public required decimal ValidationNetR { get; init; }
    public required int TestCandidates { get; init; }
    public required int TestKept { get; init; }
    public required decimal TestNetR { get; init; }
    public required decimal TestBaselineNetR { get; init; }
    /// <summary>Secondary metric, reported but never optimised - see <see cref="NestedFilterResult"/>.</summary>
    public required decimal TestNet { get; init; }
    public required decimal TestBaselineNet { get; init; }
}

/// <summary>
/// The out-of-sample result of a nested-selection run: the per-fold choices, plus the pooled
/// accepted and unfiltered trade sets - the only two columns the V2 Phase 0 gate compares.
/// </summary>
public sealed record NestedFilterResult
{
    public required IReadOnlyList<FoldSelection> Folds { get; init; }
    public required IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> Baseline { get; init; }
    public required IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> Accepted { get; init; }
    public required int Uncovered { get; init; }

    /// <summary>
    /// PRIMARY. Profit factor and net are expressed in R.
    /// <para>
    /// The candidate source is sized by `FixedFractionalRisk` on a decaying equity curve - the
    /// 3.5-year run took the account from 100,000 to under 24,000 - so a currency evaluation
    /// silently weights early candidates several times more heavily than late ones and credits the
    /// filter with the de-risking that the drawdown itself produced. R removes both effects.
    /// </para>
    /// </summary>
    public TradingReport BaselineReport => Report(Baseline, r: true);
    public TradingReport AcceptedReport => Report(Accepted, r: true);

    /// <summary>SECONDARY. Account-currency view, reported for continuity with §3.13. Never optimised.</summary>
    public TradingReport BaselineReportCurrency => Report(Baseline, r: false);
    public TradingReport AcceptedReportCurrency => Report(Accepted, r: false);

    private static TradingReport Report(
        IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> items, bool r) =>
        ClassifierBacktester.Summarise([.. items.Select(item => new ClassifierTrade(
            item.Trade.Opened, item.Trade.Closed, item.Trade.Side, 0m, 0m,
            r ? item.Trade.R : item.Trade.Net, 0))]);
}

/// <summary>
/// Meta-labelling: use the classifier as a filter over another strategy's signals rather than as a
/// signal source of its own.
/// <para>
/// This is a strictly out-of-sample exercise. Each walk-forward window trains on its own past and
/// judges only the primary trades that opened inside its test period, so a trade is never scored by
/// a model that saw it. Trades falling outside every test period are excluded from *both* the
/// baseline and the filtered figures, which is what keeps the comparison honest - reporting the
/// strategy's full-period PnL against a filtered subset would flatter the filter for free.
/// </para>
/// </summary>
public static class MetaFilter
{
    /// <summary>
    /// Reads the trade table this repo's report writer emits
    /// (<c>#,Side,Opened (UTC),Closed,Signal,Entry,Stop,Target,Exit,R,Net,...</c>).
    /// </summary>
    public static IReadOnlyList<PrimaryTrade> LoadTrades(string csvPath, TimeSpan signalLead = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        if (signalLead == default)
            signalLead = TimeSpan.FromMinutes(1);
        List<PrimaryTrade> trades = [];

        foreach (string line in File.ReadLines(csvPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] parts = line.Split(',');
            if (parts.Length < 11)
                continue;
            if (!int.TryParse(parts[0], out int number))
                continue;

            TradeLabel side = parts[1].Trim().Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? TradeLabel.Buy
                : TradeLabel.Sell;

            DateTimeOffset opened = ParseTime(parts[2]);
            trades.Add(new PrimaryTrade(
                number,
                side,
                // The markdown report does not carry the decision time, so it is reconstructed as
                // one execution bar before the fill. LoadFromSimulationResult reads the real value.
                opened - signalLead,
                opened,
                ParseTime(parts[3]),
                decimal.Parse(parts[9], CultureInfo.InvariantCulture),
                decimal.Parse(parts[10], CultureInfo.InvariantCulture)));
        }

        return trades.OrderBy(trade => trade.Opened).ToArray();
    }

    /// <summary>
    /// Reads trades straight out of a run's <c>simulation-result.json</c>, which records
    /// <c>signalCreatedAt</c> - the strategy's own decision timestamp. Preferred over the CSV
    /// path: no reconstruction of the decision bar is required, so the filter cannot be
    /// accidentally handed the fill bar.
    /// </summary>
    public static IReadOnlyList<PrimaryTrade> LoadFromSimulationResult(string path, string? strategyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream file = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(file);

        List<PrimaryTrade> trades = [];
        int number = 0;

        foreach (JsonElement strategy in document.RootElement.GetProperty("strategies").EnumerateArray())
        {
            if (strategyId is not null
                && strategy.TryGetProperty("strategyId", out JsonElement id)
                && !string.Equals(id.GetString(), strategyId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!strategy.TryGetProperty("trades", out JsonElement list))
                continue;

            foreach (JsonElement trade in list.EnumerateArray())
            {
                DateTimeOffset opened = trade.GetProperty("openedAt").GetDateTimeOffset();
                DateTimeOffset signal = trade.TryGetProperty("signalCreatedAt", out JsonElement signalAt)
                    && signalAt.ValueKind is not JsonValueKind.Null
                        ? signalAt.GetDateTimeOffset()
                        : opened;

                trades.Add(new PrimaryTrade(
                    ++number,
                    string.Equals(trade.GetProperty("side").GetString(), "Buy", StringComparison.OrdinalIgnoreCase)
                        ? TradeLabel.Buy
                        : TradeLabel.Sell,
                    signal,
                    opened,
                    trade.GetProperty("closedAt").GetDateTimeOffset(),
                    trade.GetProperty("rMultiple").GetDecimal(),
                    trade.GetProperty("netProfitLoss").GetDecimal()));
            }
        }

        return trades.OrderBy(trade => trade.Opened).ToArray();
    }

    /// <summary>
    /// Trains one model per walk-forward window and scores the primary trades opening inside that
    /// window's test period.
    /// </summary>
    public static (IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> Scored, int Uncovered) Score(
        IReadOnlyList<PrimaryTrade> trades,
        ClassifierDataset dataset,
        ClassifierOptions options,
        IModelTrainer trainer,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan)
    {
        IReadOnlyList<DatasetSplit> splits = DatasetSplitter.WalkForward(
            dataset.Rows, trainSpan, validationSpan, testSpan, embargoRows: options.PredictionHorizon);

        if (splits.Count == 0)
            throw new InvalidOperationException("The dataset is too short for a single walk-forward window.");

        List<(PrimaryTrade, Prediction)> scored = [];
        HashSet<int> covered = [];

        foreach (DatasetSplit split in splits)
        {
            if (split.Train.Select(row => row.Label).Distinct().Count() < 2)
                continue;

            DateTimeOffset testStart = split.Test[0].Features.Timestamp;
            DateTimeOffset testEnd = split.Test[^1].Features.Timestamp;

            using TrainedModel model = trainer.Train(split.Train, dataset.Schema);

            foreach (PrimaryTrade trade in trades)
            {
                // The classifier is asked about the DECISION bar, never the fill bar - the latter
                // is one candle of hindsight the primary strategy did not have.
                DateTimeOffset decisionTime = trade.SignalAt;
                if (decisionTime < testStart || decisionTime > testEnd)
                    continue;
                if (!covered.Add(trade.Number))
                    continue;

                LabeledFeatureRow? row = LatestAtOrBefore(split.Test, decisionTime);
                if (row is null)
                    continue;

                scored.Add((trade, model.Model.Predict(row.Features)));
            }
        }

        return (scored, trades.Count - covered.Count);
    }

    /// <summary>
    /// V2 §3.1 item 4: deterministically deduplicate candidates by source event.
    /// <para>
    /// `BreakoutDetectorAgent` has no last-processed-trigger guard - it wakes on the 1m interval but
    /// reads the latest 5m trigger candle, so one trigger snapshot can emit several candidates
    /// whenever the position guard is not holding. Measured duplication is 5.2% on the 31-day log
    /// and 5.9% on the 3.5-year log. The document allows deduplicating by source event instead of
    /// adding an agent-side guard; the first candidate in each trigger bucket wins, which is the one
    /// the guarded agent would have emitted.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<PrimaryTrade> Kept, int Removed) DeduplicateBySourceEvent(
        IReadOnlyList<PrimaryTrade> trades,
        int triggerMinutes)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (triggerMinutes <= 0)
            return (trades, 0);

        HashSet<(DateTimeOffset Bucket, TradeLabel Side)> seen = [];
        List<PrimaryTrade> kept = [];
        foreach (PrimaryTrade trade in trades.OrderBy(item => item.SignalAt))
        {
            DateTimeOffset at = trade.SignalAt;
            long ticks = TimeSpan.FromMinutes(triggerMinutes).Ticks;
            DateTimeOffset bucket = new(at.Ticks - (at.Ticks % ticks), at.Offset);
            if (seen.Add((bucket, trade.Side)))
                kept.Add(trade);
        }

        return (kept, trades.Count - kept.Count);
    }

    /// <summary>Applies one filter setting and summarises the surviving trades.</summary>
    public static FilterOutcome Apply(
        IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> scored,
        FilterMode mode,
        double threshold)
    {
        List<ClassifierTrade> kept = [];
        int dropped = 0;

        foreach ((PrimaryTrade trade, Prediction prediction) in scored)
        {
            double agreeing = trade.Side == TradeLabel.Buy
                ? prediction.BuyProbability
                : prediction.SellProbability;
            double opposing = trade.Side == TradeLabel.Buy
                ? prediction.SellProbability
                : prediction.BuyProbability;

            bool keep = mode == FilterMode.Agreement
                ? agreeing >= threshold
                : opposing < threshold;

            if (!keep)
            {
                dropped++;
                continue;
            }

            // The primary strategy's own realised Net is carried through unchanged: the filter
            // decides which trades happen, never how they are managed or priced.
            kept.Add(new ClassifierTrade(
                trade.Opened, trade.Closed, trade.Side, 0m, 0m, trade.Net, agreeing));
        }

        return new FilterOutcome
        {
            Label = $"{mode} >= {threshold:F2}",
            Kept = kept.Count,
            Dropped = dropped,
            Report = ClassifierBacktester.Summarise(kept)
        };
    }

    /// <summary>
    /// The control that decides whether any of this means anything.
    /// <para>
    /// The primary strategy loses money overall, so its losses are large and concentrated - which
    /// means dropping <i>any</i> sizeable fraction of its trades has a fair chance of removing
    /// enough of them to flip the sign. A filter that keeps 29 of 103 trades must therefore be
    /// compared against random subsets of 29 trades, not against zero. If the classifier's profit
    /// factor sits inside the random distribution, it has selected nothing; it has just traded
    /// less.
    /// </para>
    /// </summary>
    public static (double MedianProfitFactor, double Percentile, double P95) RandomControl(
        IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> scored,
        int keepCount,
        double actualProfitFactor,
        int iterations = 2000,
        int seed = 11,
        bool useRMultiple = true)
    {
        if (keepCount <= 0 || keepCount > scored.Count)
            return (0, 0, 0);

        Random random = new(seed);
        // Must be drawn on the SAME metric as `actualProfitFactor`, or the percentile compares an
        // R-based profit factor against a distribution of currency-based ones.
        decimal[] nets = scored.Select(item => useRMultiple ? item.Trade.R : item.Trade.Net).ToArray();
        double[] factors = new double[iterations];
        int[] order = [.. Enumerable.Range(0, nets.Length)];

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            // Partial Fisher-Yates: shuffle only the first keepCount slots.
            for (int index = 0; index < keepCount; index++)
            {
                int swap = random.Next(index, order.Length);
                (order[index], order[swap]) = (order[swap], order[index]);
            }

            decimal profit = 0m;
            decimal loss = 0m;
            for (int index = 0; index < keepCount; index++)
            {
                decimal net = nets[order[index]];
                if (net > 0m) profit += net; else loss -= net;
            }

            factors[iteration] = loss == 0m
                ? (profit > 0m ? double.PositiveInfinity : 0)
                : (double)(profit / loss);
        }

        Array.Sort(factors);
        double median = factors[iterations / 2];
        double p95 = factors[(int)(iterations * 0.95)];
        double beaten = factors.Count(value => value < actualProfitFactor);
        return (median, beaten / iterations, p95);
    }


    /// <summary>
    /// V2 Phase 0b: nested selection. Each walk-forward fold chooses its feature set, filter mode
    /// and threshold using <b>validation</b> candidates only, freezes that rule, and applies it once
    /// to the untouched test candidates.
    /// <para>
    /// This is what separates a gate from a sweep. <c>Score</c> + <c>Apply</c> report a whole grid
    /// of thresholds over the test period, so the reader picks the winner after seeing the answer -
    /// the exact selection effect that turned a 41-day "MET" verdict into PF 0.85-0.91 on 3.5 years.
    /// Here the test period never participates in choosing anything, so the pooled accepted column
    /// is a single honest out-of-sample number.
    /// </para>
    /// </summary>
    public static NestedFilterResult SelectNested(
        IReadOnlyList<PrimaryTrade> trades,
        IReadOnlyList<(string Name, ClassifierDataset Dataset)> featureSets,
        ClassifierOptions options,
        Func<IModelTrainer> trainerFactory,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan,
        IReadOnlyList<FilterMode>? modes = null,
        IReadOnlyList<double>? thresholds = null,
        int minimumValidationKept = 5)
    {
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(featureSets);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trainerFactory);
        if (featureSets.Count == 0)
            throw new ArgumentException("At least one feature set is required.", nameof(featureSets));

        modes ??= [FilterMode.Agreement, FilterMode.NotOpposed];
        thresholds ??= [0.30, 0.40, 0.50, 0.60, 0.70];

        Dictionary<string, IReadOnlyList<DatasetSplit>> splitsBySet = [];
        foreach ((string name, ClassifierDataset dataset) in featureSets)
        {
            splitsBySet[name] = DatasetSplitter.WalkForward(
                dataset.Rows, trainSpan, validationSpan, testSpan, embargoRows: options.PredictionHorizon);
        }

        int foldCount = splitsBySet.Values.Min(splits => splits.Count);
        if (foldCount == 0)
            throw new InvalidOperationException("The dataset is too short for a single walk-forward window.");

        List<FoldSelection> folds = [];
        List<(PrimaryTrade, Prediction)> baseline = [];
        List<(PrimaryTrade, Prediction)> accepted = [];
        HashSet<int> covered = [];

        for (int fold = 0; fold < foldCount; fold++)
        {
            // All feature sets share the same rows and therefore the same window boundaries; the
            // first one defines the fold's calendar.
            DatasetSplit reference = splitsBySet[featureSets[0].Name][fold];
            if (reference.Test.Count == 0 || reference.Validation.Count == 0)
                continue;

            DateTimeOffset testStart = reference.Test[0].Features.Timestamp;
            DateTimeOffset testEnd = reference.Test[^1].Features.Timestamp;

            // Attribute each trade to the first fold whose test window contains its DECISION bar,
            // so baseline and accepted always share one denominator.
            PrimaryTrade[] foldTrades = [.. trades.Where(trade =>
                trade.SignalAt >= testStart && trade.SignalAt <= testEnd && !covered.Contains(trade.Number))];

            FilterRule? best = null;
            double bestScore = double.NegativeInfinity;
            int bestValidationKept = 0;
            decimal bestValidationNetR = 0m;
            int validationCandidates = 0;
            List<(PrimaryTrade Trade, Prediction Prediction)> bestTestScored = [];

            // The fold's denominator is fixed by the FIRST feature set and does not depend on
            // whether any rule was eligible. Deriving it from the winning rule instead would drop
            // every guard-failing fold out of the baseline as well, leaving the gate to compare
            // accepted trades only against the folds that happened to produce a rule.
            List<(PrimaryTrade Trade, Prediction Prediction)> foldBaseline = [];

            foreach ((string name, ClassifierDataset dataset) in featureSets)
            {
                DatasetSplit split = splitsBySet[name][fold];
                if (split.Train.Select(row => row.Label).Distinct().Count() < 2)
                    continue;

                using TrainedModel model = trainerFactory().Train(split.Train, dataset.Schema);

                var validationScored = ScoreWindow(trades, split.Validation, model, exclude: null);
                var testScored = ScoreWindow(foldTrades, split.Test, model, exclude: covered);
                validationCandidates = Math.Max(validationCandidates, validationScored.Count);

                if (foldBaseline.Count == 0)
                    foldBaseline = testScored;

                foreach (FilterMode mode in modes)
                {
                    foreach (double threshold in thresholds)
                    {
                        (int kept, decimal netR, _) = Evaluate(validationScored, mode, threshold);
                        if (kept < minimumValidationKept)
                            continue;

                        // Total after-cost R on validation-accepted candidates: what the rule would
                        // actually have earned per unit of risk, not an expectancy that rewards
                        // keeping one lucky trade, and not a currency figure that would rank rules
                        // by where they happened to fall on the equity curve.
                        double score = (double)netR;
                        if (score <= bestScore)
                            continue;

                        bestScore = score;
                        best = new FilterRule(name, mode, threshold);
                        bestValidationKept = kept;
                        bestValidationNetR = netR;
                        bestTestScored = testScored;
                    }
                }
            }

            foreach (PrimaryTrade trade in foldTrades)
                covered.Add(trade.Number);

            // Accepted is always drawn from the baseline's membership, so the two columns cannot
            // diverge when feature sets differ by a handful of warm-up rows.
            HashSet<int> inBaseline = [.. foldBaseline.Select(item => item.Trade.Number)];
            List<(PrimaryTrade, Prediction)> foldAccepted = [];
            if (best is FilterRule rule)
            {
                foreach ((PrimaryTrade trade, Prediction prediction) in bestTestScored)
                {
                    if (inBaseline.Contains(trade.Number) && Keeps(trade, prediction, rule.Mode, rule.Threshold))
                        foldAccepted.Add((trade, prediction));
                }
            }

            baseline.AddRange(foldBaseline);
            accepted.AddRange(foldAccepted);

            folds.Add(new FoldSelection
            {
                Fold = fold,
                TestStart = testStart,
                TestEnd = testEnd,
                Rule = best,
                ValidationCandidates = validationCandidates,
                ValidationKept = bestValidationKept,
                ValidationNetR = bestValidationNetR,
                TestCandidates = foldBaseline.Count,
                TestKept = foldAccepted.Count,
                TestNetR = foldAccepted.Sum(item => item.Item1.R),
                TestBaselineNetR = foldBaseline.Sum(item => item.Item1.R),
                TestNet = foldAccepted.Sum(item => item.Item1.Net),
                TestBaselineNet = foldBaseline.Sum(item => item.Item1.Net)
            });
        }

        return new NestedFilterResult
        {
            Folds = folds,
            Baseline = baseline,
            Accepted = accepted,
            Uncovered = trades.Count - baseline.Count
        };
    }

    /// <summary>Scores every trade whose decision bar falls inside one window's row range.</summary>
    private static List<(PrimaryTrade Trade, Prediction Prediction)> ScoreWindow(
        IReadOnlyList<PrimaryTrade> trades,
        IReadOnlyList<LabeledFeatureRow> rows,
        TrainedModel model,
        ISet<int>? exclude)
    {
        List<(PrimaryTrade, Prediction)> scored = [];
        if (rows.Count == 0)
            return scored;

        DateTimeOffset start = rows[0].Features.Timestamp;
        DateTimeOffset end = rows[^1].Features.Timestamp;

        foreach (PrimaryTrade trade in trades)
        {
            if (trade.SignalAt < start || trade.SignalAt > end)
                continue;
            if (exclude is not null && exclude.Contains(trade.Number))
                continue;

            LabeledFeatureRow? row = LatestAtOrBefore(rows, trade.SignalAt);
            if (row is null)
                continue;

            scored.Add((trade, model.Model.Predict(row.Features)));
        }

        return scored;
    }

    private static bool Keeps(PrimaryTrade trade, Prediction prediction, FilterMode mode, double threshold)
    {
        double agreeing = trade.Side == TradeLabel.Buy
            ? prediction.BuyProbability
            : prediction.SellProbability;
        double opposing = trade.Side == TradeLabel.Buy
            ? prediction.SellProbability
            : prediction.BuyProbability;

        return mode == FilterMode.Agreement ? agreeing >= threshold : opposing < threshold;
    }

    private static (int Kept, decimal NetR, decimal Net) Evaluate(
        IReadOnlyList<(PrimaryTrade Trade, Prediction Prediction)> scored,
        FilterMode mode,
        double threshold)
    {
        int kept = 0;
        decimal netR = 0m, net = 0m;
        foreach ((PrimaryTrade trade, Prediction prediction) in scored)
        {
            if (!Keeps(trade, prediction, mode, threshold))
                continue;
            kept++;
            netR += trade.R;
            net += trade.Net;
        }
        return (kept, netR, net);
    }

    private static LabeledFeatureRow? LatestAtOrBefore(IReadOnlyList<LabeledFeatureRow> rows, DateTimeOffset when)
    {
        LabeledFeatureRow? best = null;
        foreach (LabeledFeatureRow row in rows)
        {
            if (row.Features.Timestamp > when)
                break;
            best = row;
        }
        return best;
    }

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(
        value.Trim(),
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
