using System.Globalization;
using Brokers.Models;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.ML.Evaluation;
using TradingClassifier.ML.Candidates;
using TradingClassifier.ML.Experiments;
using TradingClassifier.ML.Inference;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;
using TradingClassifierRunner;

// Command-line entry point for the blueprint's section 26 training pipeline and section 35
// experiment ladder. Every number the blueprint says must be configurable (section 34) is a flag.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

string command = args[0];
Dictionary<string, string> flags = ParseFlags(args.Skip(1));

ClassifierOptions options = new()
{
    EnabledGroups = Groups("groups", FeatureGroups.All),
    // --atr-levels "" drops the raw atr{n}_pct columns, leaving only regime/change/percentile.
    AtrPeriods = flags.TryGetValue("atr-levels", out string? levels)
        ? (string.IsNullOrWhiteSpace(levels)
            ? []
            : levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToArray())
        : [],
    PredictionHorizon = Int("horizon", 10),
    // Overlapping labels: with one row per candle, consecutive labels share horizon-1 bars.
    // Defaulting to the horizon makes training labels non-overlapping; pass 1 to opt out.
    TrainingStride = Int("training-stride", Int("horizon", 10)),
    AtrTargetMultiplier = Dec("atr-multiplier", 0.75m),
    UseMaximumExcursionLabels = flags.ContainsKey("max-excursion"),
    // --normalize-by-atr divides level-like features by ATR instead of price, to remove the
    // volatility regime as well as the price level (§3.23 drift finding).
    NormalizeByAtr = flags.ContainsKey("normalize-by-atr"),

    BuyProbabilityThreshold = Dbl("buy-threshold", 0.70),
    SellProbabilityThreshold = Dbl("sell-threshold", 0.70)
};
options.Validate();

TradingCostModel costs = new()
{
    HalfSpread = Dec("half-spread", 0m),
    CommissionPerTrade = Dec("commission", 0m),
    SlippagePerSide = Dec("slippage", 0m)
};

if (command == "fetch")
    return Fetch();

if (costs.HalfSpread == 0m && costs.CommissionPerTrade == 0m && costs.SlippagePerSide == 0m)
{
    Console.WriteLine("WARNING: no costs configured (--half-spread/--commission/--slippage). " +
        "Results will not describe tradeable performance.");
}

// ONE execution policy for every command. Previously only `walk-forward` read this flag while
// `train`, `ladder` and `ablate` used the library default of "overlap allowed", so the commands
// reported results for different tradeable strategies and could not be compared with each other.
bool allowOverlap = flags.ContainsKey("allow-overlap");
if (allowOverlap)
{
    Console.WriteLine("WARNING: --allow-overlap is on. Up to `horizon` positions may be held at " +
        "once, which needs `horizon` times the capital and inflates apparent trade counts.");
}

IReadOnlyList<ClassifierCandle> candles = LoadCandles();

// --train-from / --train-to trim the candle series BEFORE any dataset is built.
// Without this the only way to train was on the whole file, so a model's training window always
// contained whatever window it was later scored on — the look-ahead that made a 5-trade rung C
// result unreadable and a 365-trade classifier backtest meaningless (PROJECT_STATE §3.23).
if (flags.TryGetValue("train-from", out string? trainFrom))
{
    DateTimeOffset from = DateTimeOffset.Parse(trainFrom, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    candles = [.. candles.Where(candle => candle.Timestamp >= from)];
}
if (flags.TryGetValue("train-to", out string? trainTo))
{
    DateTimeOffset to = DateTimeOffset.Parse(trainTo, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    candles = [.. candles.Where(candle => candle.Timestamp < to)];
}
if (flags.ContainsKey("train-from") || flags.ContainsKey("train-to"))
{
    if (candles.Count == 0)
    {
        Console.Error.WriteLine("The --train-from/--train-to window contains no candles.");
        return 1;
    }
    Console.WriteLine($"Trimmed to {candles.Count:N0} candles, " +
        $"{candles[0].Timestamp:yyyy-MM-dd} -> {candles[^1].Timestamp:yyyy-MM-dd}");
}
int resampleMinutes = Int("resample", 1);
if (resampleMinutes > 1 && candles.Count > 0)
{
    int before = candles.Count;
    candles = CandleSources.Resample(candles, resampleMinutes);
    Console.WriteLine($"Resampled {before:N0} 1m candles -> {candles.Count:N0} x {resampleMinutes}m candles");
}
if (candles.Count == 0)
{
    Console.Error.WriteLine("No candles were loaded.");
    return 1;
}

Console.WriteLine($"Loaded {candles.Count:N0} candles, {candles[0].Timestamp:yyyy-MM-dd} -> {candles[^1].Timestamp:yyyy-MM-dd}");

TimeSpan trainSpan = Days("train-days", 90);
TimeSpan validationSpan = Days("validation-days", 30);
TimeSpan testSpan = Days("test-days", 30);

switch (command)
{
    case "train": return Train();
    case "walk-forward": return WalkForward();
    case "ladder": return Ladder();
    case "ablate": return Ablate();
    case "select": return Select();
    case "diagnose": return Diagnose();
    case "train-stop": return TrainStop();
    case "filter-signals": return FilterSignals();
    case "filter-nested": return FilterNested();
    case "candidate-r": return CandidateR();
    case "fetch": return Fetch();
    case "trend-stats": return TrendStats();
    case "swing-entry": return SwingEntry();
    case "trend-walk-forward": return TrendWalkForward();
    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
}

int Train()
{
    ClassifierDataset dataset = BuildDataset();

    // Section 15: chronological, never shuffled; embargoed by the horizon so the train slice's
    // labels cannot reach into validation.
    DatasetSplit split = DatasetSplitter.Chronological(
        dataset.Rows, Dbl("train-fraction", 0.6), Dbl("validation-fraction", 0.2),
        embargoRows: options.PredictionHorizon);

    Console.WriteLine($"train={split.Train.Count:N0}  validation={split.Validation.Count:N0}  test={split.Test.Count:N0}");

    ModelEvaluator evaluator = new(options, costs, allowOverlappingPositions: allowOverlap);

    // Section 14: the baseline is trained first and on purpose. If LightGBM cannot beat it, the
    // blueprint says to look at the features and the target rather than at the hyperparameters.
    IReadOnlyList<LabeledFeatureRow> trainRows = SampleUniqueness.Stride(split.Train, options.TrainingStride);
    if (trainRows.Count != split.Train.Count)
    {
        Console.WriteLine($"Training stride {options.TrainingStride}: {split.Train.Count:N0} -> " +
            $"{trainRows.Count:N0} rows with non-overlapping labels");
    }

    using TrainedModel baseline = new LogisticRegressionBaselineTrainer().Train(trainRows, dataset.Schema);
    Console.WriteLine(evaluator.Evaluate(baseline, split.Test, "test").ToText());

    using TrainedModel model = new LightGbmModelTrainer(new LightGbmTrainingOptions
    {
        NumberOfLeaves = Int("leaves", 31),
        NumberOfIterations = Int("iterations", 200),
        LearningRate = Dbl("learning-rate", 0.1),
        MinimumExampleCountPerLeaf = Int("min-leaf", 40),
        UseClassWeights = !flags.ContainsKey("no-class-weights")
    }).Train(trainRows, dataset.Schema);

    ClassifierOptions tuned = flags.ContainsKey("no-tune")
        ? options
        : evaluator.TuneThresholds(model, split.Validation);
    if (!ReferenceEquals(tuned, options))
    {
        Console.WriteLine($"Tuned thresholds on validation: buy={tuned.BuyProbabilityThreshold:F2} " +
            $"sell={tuned.SellProbabilityThreshold:F2}");
    }

    EvaluationResult result = new ModelEvaluator(tuned, costs, allowOverlappingPositions: allowOverlap)
        .Evaluate(model, split.Test, "test", includeImportance: flags.ContainsKey("importance"));
    Console.WriteLine(result.ToText());

    if (result.Importance.Count > 0)
    {
        Console.WriteLine("  feature importance (macro-F1 drop when permuted, section 23):");
        foreach (FeatureImportance importance in result.Importance.Take(15))
            Console.WriteLine($"    {importance.Rank,3}. {importance.Feature,-24}{importance.MacroF1Drop,9:F5}");
    }

    if (flags.TryGetValue("save", out string? savePath))
    {
        // Refit on the FULL dataset before saving. The evaluated model was fitted on the oldest
        // 60% - correct for measuring, wrong for deployment, because it discards the most recent
        // and most relevant 40% and ships a model that is stale on the day it is created. Selection
        // happened above on validation; this refit changes no decision, only the data behind them.
        IReadOnlyList<LabeledFeatureRow> full = SampleUniqueness.Stride(dataset.Rows, options.TrainingStride);
        Console.WriteLine($"Refitting deployment model on all {full.Count:N0} rows " +
            $"(evaluation used {SampleUniqueness.Stride(split.Train, options.TrainingStride).Count:N0})");

        using TrainedModel deployment = new LightGbmModelTrainer(new LightGbmTrainingOptions
        {
            NumberOfLeaves = Int("leaves", 31),
            NumberOfIterations = Int("iterations", 200),
            LearningRate = Dbl("learning-rate", 0.1),
            MinimumExampleCountPerLeaf = Int("min-leaf", 40),
            UseClassWeights = !flags.ContainsKey("no-class-weights")
        }).Train(full, dataset.Schema);

        int intervalMinutes = resampleMinutes > 1 ? resampleMinutes : Int("interval-minutes", 1);
        deployment.Save(
            savePath,
            tuned,
            intervalMinutes,
            flags.GetValueOrDefault("instrument") ?? "UNKNOWN",
            dataset.Start,
            dataset.End,
            full.Count);

        Console.WriteLine($"Saved model + metadata sidecar to {savePath}");
        Console.WriteLine(ClassifierModelArtifact.Load(savePath).ToText());
    }

    return 0;
}

int WalkForward()
{
    ClassifierDataset dataset = BuildDataset();
    WalkForwardRunner runner = new(options, NewTrainer(), costs, allowOverlap);
    WalkForwardResult result = runner.Run(dataset, trainSpan, validationSpan, testSpan, !flags.ContainsKey("no-tune"));
    Console.WriteLine(result.ToText());
    return result.MeetsSuccessBar ? 0 : 2;
}

int Ladder()
{
    FeatureExperimentRunner runner = new(options, NewTrainer, costs,
        BarInterval.Minutes(resampleMinutes > 1 ? resampleMinutes : Int("interval-minutes", 15)),
        allowOverlap);
    Console.WriteLine("Section 35 experiment ladder\n");
    Console.WriteLine(FeatureExperimentRunner.ToTable(
        runner.RunLadder(candles, trainSpan, validationSpan, testSpan)));
    return 0;
}

int Ablate()
{
    FeatureExperimentRunner runner = new(options, NewTrainer, costs,
        BarInterval.Minutes(resampleMinutes > 1 ? resampleMinutes : Int("interval-minutes", 15)),
        allowOverlap);
    Console.WriteLine("Section 24 feature ablation\n");
    Console.WriteLine(FeatureExperimentRunner.ToTable(
        runner.RunAblation(candles, trainSpan, validationSpan, testSpan)));
    return 0;
}

// Section 19's decision layer applied to somebody else's signals: meta-labelling. Answers
// "can the classifier tell this strategy which of its own trades to skip", which is a different
// and much lower-bar question than "can the classifier trade profitably on its own".
int Fetch()
{
    string output = flags.GetValueOrDefault("out")
        ?? throw new ArgumentException("--out <file.jsonl.gz> is required.");
    return CandleFetcher.FetchAsync(
        flags.GetValueOrDefault("instrument") ?? "METAL:XAU/USD",
        flags.GetValueOrDefault("interval") ?? "15m",
        DateTimeOffset.Parse(flags.GetValueOrDefault("from") ?? "2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(flags.GetValueOrDefault("to") ?? "2026-07-24T00:00:00Z", CultureInfo.InvariantCulture),
        output,
        flags.ContainsKey("live"),
        CancellationToken.None).GetAwaiter().GetResult();
}

// Blueprint section 55 (Phase 2). Deliberately reports statistics only - section 53 says
// "do not implement trading yet", and nothing here produces a signal.
int TrendStats()
{
    TrendStatistics.Detection.TrendDetectorConfig trendConfig = new()
    {
        Timeframe = TimeSpan.FromMinutes(Int("trend-timeframe-minutes", 240)),
        EmaPeriod = Int("trend-ema", 20),
        AtrPeriod = Int("trend-atr", 14),
        CandidateMinDisplacementAtr = Dec("trend-candidate-atr", 0.75m),
        ConfirmationMinDisplacementAtr = Dec("trend-confirm-atr", 1.25m),
        ExhaustionRetracementAtr = Dec("trend-exhaustion-atr", 0.75m),
        EndRetracementAtr = Dec("trend-end-atr", 1.25m)
    };
    trendConfig.Validate();
    return TrendStatsReport.Run(candles, flags.GetValueOrDefault("instrument") ?? "METAL:XAU/USD", trendConfig);
}

// Blueprint sections 29/30: the entry-threshold sweep and the A/B/C experiments.
int SwingEntry()
{
    TrendStatistics.Detection.TrendDetectorConfig trendConfig = new()
    {
        Timeframe = TimeSpan.FromMinutes(Int("trend-timeframe-minutes", 240)),
        EmaPeriod = Int("trend-ema", 20),
        AtrPeriod = Int("trend-atr", 14)
    };
    trendConfig.Validate();
    return SwingEntryExperiment.Run(
        candles,
        flags.GetValueOrDefault("instrument") ?? "METAL:XAU/USD",
        trendConfig,
        Int("min-trend-samples", 20),
        Dec("round-trip-cost-pct", 0.03m));
}

// Blueprint sections 48 (V7) and 60 (V8): walk-forward with thresholds chosen per window on a
// validation slice, optionally regime-conditioned.
int TrendWalkForward()
{
    TrendStatistics.Detection.TrendDetectorConfig trendConfig = new()
    {
        Timeframe = TimeSpan.FromMinutes(Int("trend-timeframe-minutes", 240)),
        EmaPeriod = Int("trend-ema", 20),
        AtrPeriod = Int("trend-atr", 14)
    };
    trendConfig.Validate();

    string sym = flags.GetValueOrDefault("instrument") ?? "XAUUSD";
    var series = candles.Select(c => new TrendStatistics.Data.Candle
    {
        Symbol = sym,
        OpenTime = c.Timestamp - trendConfig.Timeframe,
        Open = c.Open, High = c.High, Low = c.Low, Close = c.Close
    }).ToArray();

    var costModel = new TrendStatistics.Evaluation.TrendCostModel
    {
        RoundTripPct = Dec("round-trip-cost-pct", 0.03m)
    };
    var runner = new TrendStatistics.Evaluation.WalkForwardRunner(
        trendConfig, costModel, Int("min-trend-samples", 30));

    foreach (bool conditioned in (bool[])[false, true])
    {
        var report = runner.Run(
            series, sym,
            testSpan: TimeSpan.FromDays(Int("test-days", 365)),
            validationSpan: TimeSpan.FromDays(Int("validation-days", 365)),
            minimumTrainSpan: TimeSpan.FromDays(Int("min-train-days", 1460)),
            regimeConditioned: conditioned);
        Console.WriteLine(report.ToText());
    }

    return 0;
}

int FilterSignals()
{
    string? tradesPath = flags.GetValueOrDefault("trades");
    string? resultPath = flags.GetValueOrDefault("simulation-result");
    if (tradesPath is null && resultPath is null)
    {
        Console.Error.WriteLine("--trades <csv> or --simulation-result <json> is required.");
        return 1;
    }

    IReadOnlyList<PrimaryTrade> trades = resultPath is not null
        ? MetaFilter.LoadFromSimulationResult(resultPath, flags.GetValueOrDefault("strategy-id"))
        : MetaFilter.LoadTrades(tradesPath!, TimeSpan.FromMinutes(Int("signal-lead-minutes", 1)));
    Console.WriteLine($"Primary trades: {trades.Count:N0}, {trades[0].Opened:yyyy-MM-dd} -> {trades[^1].Opened:yyyy-MM-dd}");

    ClassifierDataset dataset = BuildDataset();
    var (scored, uncovered) = MetaFilter.Score(
        trades, dataset, options, NewTrainer(), trainSpan, validationSpan, testSpan);

    if (scored.Count == 0)
    {
        Console.Error.WriteLine("No primary trade fell inside a walk-forward test period.");
        return 1;
    }

    // The baseline is the same trade subset, unfiltered. Comparing against the strategy's full-run
    // PnL instead would credit the filter with everything the walk-forward windows simply did not
    // cover.
    FilterOutcome baseline = MetaFilter.Apply(scored, FilterMode.NotOpposed, 1.01);
    Console.WriteLine($"\nOut-of-sample coverage: {scored.Count} of {trades.Count} trades scored " +
        $"({uncovered} fell outside every test window and are excluded from both columns).\n");
    Console.WriteLine($"{"filter",-26}{"kept",6}{"win%",8}{"PF",9}{"net",11}" +
        $"{"randPF",9}{"rand p95",10}{"pctile",9}");
    Console.WriteLine($"{"BASELINE (no filter)",-26}{baseline.Kept,6}" +
        $"{baseline.Report.WinRate,8:P1}{baseline.Report.ProfitFactor,9:F3}{baseline.Report.NetProfit,11:F2}");

    foreach (FilterMode mode in (FilterMode[])[FilterMode.Agreement, FilterMode.NotOpposed])
    {
        foreach (double threshold in (double[])[0.30, 0.40, 0.50, 0.60, 0.70])
        {
            FilterOutcome outcome = MetaFilter.Apply(scored, mode, threshold);
            if (outcome.Kept == 0)
                continue;

            // Every row is judged against random subsets of the same size. Without this column a
            // filter that merely trades less is indistinguishable from one that chooses well.
            var (median, percentile, p95) = MetaFilter.RandomControl(
                scored, outcome.Kept, outcome.Report.ProfitFactor);

            Console.WriteLine($"{outcome.Label,-26}{outcome.Kept,6}" +
                $"{outcome.Report.WinRate,8:P1}{outcome.Report.ProfitFactor,9:F3}" +
                $"{outcome.Report.NetProfit,11:F2}{median,9:F3}{p95,10:F3}{percentile,9:P1}");
        }
    }

    // Decile discrimination. The threshold grid above can only show that a filter trades less;
    // it cannot show whether the score ORDERS trades by quality. If the classifier carries real
    // information, win rate and mean R should rise monotonically from D1 to D10. A flat profile
    // means the probability is a coin flip wearing a decimal point, and every refinement built on
    // top of it - calibration, conviction sizing, ensembling - is refining noise.
    var ranked = scored
        .Select(item => (
            P: item.Trade.Side == TradeLabel.Buy
                ? item.Prediction.BuyProbability
                : item.Prediction.SellProbability,
            R: item.Trade.R))
        .OrderBy(item => item.P)
        .ToArray();

    Console.WriteLine("\nDecile discrimination (agreeing probability, ascending)");
    Console.WriteLine($"{"bucket",-8}{"n",5}{"p range",18}{"win%",8}{"mean R",10}{"sum R",10}");
    for (int decile = 0; decile < 10; decile++)
    {
        int start = decile * ranked.Length / 10;
        int end = (decile + 1) * ranked.Length / 10;
        if (end <= start)
            continue;

        var bucket = ranked[start..end];
        int wins = bucket.Count(item => item.R > 0m);
        Console.WriteLine($"{"D" + (decile + 1),-8}{bucket.Length,5}" +
            $"{$"{bucket[0].P:F3}-{bucket[^1].P:F3}",18}" +
            $"{(double)wins / bucket.Length,8:P1}" +
            $"{bucket.Average(item => item.R),10:F3}{bucket.Sum(item => item.R),10:F2}");
    }

    // Spearman rank correlation between score and realised R over every scored trade. One number
    // for "does a higher probability mean a better trade", with ties averaged so a model that
    // emits few distinct scores is not flattered.
    double spearman = RankCorrelation(
        ranked.Select(item => item.P).ToArray(),
        ranked.Select(item => (double)item.R).ToArray());
    Console.WriteLine($"\nSpearman(score, R) = {spearman:F4} over {ranked.Length} trades");

    // Whether the filter is doing something directional ("stop selling") or something
    // conditional ("skip these particular setups") changes what it is worth entirely.
    FilterOutcome _ = MetaFilter.Apply(scored, FilterMode.Agreement, 0.50);
    Console.WriteLine("\nBy side at Agreement >= 0.50 (is it just refusing one direction?)");
    Console.WriteLine($"{"side",-8}{"all",6}{"kept",6}{"keep%",8}{"net all",12}{"net kept",12}");
    foreach (TradeLabel side in (TradeLabel[])[TradeLabel.Buy, TradeLabel.Sell])
    {
        var ofSide = scored.Where(item => item.Trade.Side == side).ToArray();
        if (ofSide.Length == 0)
            continue;
        var keptOfSide = ofSide.Where(item =>
            (side == TradeLabel.Buy ? item.Prediction.BuyProbability : item.Prediction.SellProbability) >= 0.50)
            .ToArray();
        Console.WriteLine($"{side,-8}{ofSide.Length,6}{keptOfSide.Length,6}" +
            $"{(double)keptOfSide.Length / ofSide.Length,8:P0}" +
            $"{ofSide.Sum(i => i.Trade.Net),12:F2}{keptOfSide.Sum(i => i.Trade.Net),12:F2}");
    }

    Console.WriteLine("\nrandPF/rand p95 = median and 95th percentile profit factor of 2,000 random");
    Console.WriteLine("subsets of the same size. pctile = share of random subsets the filter beat.");
    Console.WriteLine("A filter at or below the 95th percentile has not demonstrably selected anything.");
    return 0;
}

// V2 Phase 0b gate. Unlike `filter-signals`, which prints a whole threshold grid scored on the test
// period, this picks the feature set, mode and threshold on each fold's VALIDATION candidates,
// freezes them, and applies them once to the untouched test candidates. That is the difference
// between a hypothesis-generating sweep and a falsification gate.
int FilterNested()
{
    string? tradesPath = flags.GetValueOrDefault("trades");
    string? resultPath = flags.GetValueOrDefault("simulation-result");
    if (tradesPath is null && resultPath is null)
    {
        Console.Error.WriteLine("--trades <csv> or --simulation-result <json> is required.");
        return 1;
    }

    IReadOnlyList<PrimaryTrade> trades = resultPath is not null
        ? MetaFilter.LoadFromSimulationResult(resultPath, flags.GetValueOrDefault("strategy-id"))
        : MetaFilter.LoadTrades(tradesPath!, TimeSpan.FromMinutes(Int("signal-lead-minutes", 1)));
    Console.WriteLine($"Primary trades: {trades.Count:N0}, {trades[0].Opened:yyyy-MM-dd} -> {trades[^1].Opened:yyyy-MM-dd}");

    // V2 section 3.1 item 4: the source has no last-processed-trigger guard, so one trigger snapshot
    // can emit several candidates. Deduplicate by source event before anything is trained or scored.
    int triggerMinutes = Int("dedup-trigger-minutes", 5);
    var (deduped, removed) = MetaFilter.DeduplicateBySourceEvent(trades, triggerMinutes);
    if (removed > 0)
    {
        Console.WriteLine($"Deduplicated by {triggerMinutes}m source event: {trades.Count:N0} -> " +
            $"{deduped.Count:N0} candidates ({removed:N0} removed, {(double)removed / trades.Count:P1})");
    }
    trades = deduped;

    // The predeclared candidate feature sets. Section 3.4 of the V2 document requires these be
    // registered before the run; --feature-sets overrides only for exploratory work.
    string spec = flags.GetValueOrDefault("feature-sets") ?? "ohlc:Experiment1,rung3:Experiment3";
    List<(string Name, ClassifierDataset Dataset)> featureSets = [];
    foreach (string entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = entry.Split(':', 2);
        string name = parts[0];
        FeatureGroups groups = Enum.Parse<FeatureGroups>(parts.Length > 1 ? parts[1] : parts[0]);
        ClassifierOptions scoped = options with { EnabledGroups = groups };
        ClassifierDataset dataset = new DatasetBuilder(scoped).Build(candles);
        Console.WriteLine($"  feature set '{name}' ({groups}): {dataset.Rows.Count:N0} rows x {dataset.Schema.Count} features");
        featureSets.Add((name, dataset));
    }

    NestedFilterResult result = MetaFilter.SelectNested(
        trades, featureSets, options, NewTrainer, trainSpan, validationSpan, testSpan,
        minimumValidationKept: Int("minimum-validation-kept", 5));

    Console.WriteLine($"\nOut-of-sample coverage: {result.Baseline.Count} of {trades.Count} trades " +
        $"({result.Uncovered} fell outside every test window and are excluded from both columns).\n");

    Console.WriteLine($"{"fold",5}{"test window",26}{"rule selected on validation",-42}" +
        $"{"val kept",9}{"cand",6}{"kept",6}{"net kept",11}{"net all",11}");
    foreach (FoldSelection fold in result.Folds)
    {
        string rule = fold.Rule?.ToString() ?? "(none cleared the guard)";
        Console.WriteLine($"{fold.Fold,5}  {fold.TestStart:yyyy-MM-dd}->{fold.TestEnd:yyyy-MM-dd}  {rule,-42}" +
            $"{fold.ValidationKept,9}{fold.TestCandidates,6}{fold.TestKept,6}{fold.TestNetR,11:F2}{fold.TestBaselineNetR,11:F2}");
    }

    TradingReport baseline = result.BaselineReport;
    TradingReport accepted = result.AcceptedReport;
    TradingReport baselineCash = result.BaselineReportCurrency;
    TradingReport acceptedCash = result.AcceptedReportCurrency;

    // PRIMARY: R. The source is sized by FixedFractionalRisk on a decaying account, so a currency
    // headline would rank candidates by where they fell on the equity curve.
    Console.WriteLine($"\nPRIMARY - R multiples (sizing-independent)");
    Console.WriteLine($"{"column",-28}{"trades",8}{"win%",8}{"PF_R",9}{"netR",12}{"maxDD_R",12}");
    Console.WriteLine($"{"BASELINE (no filter)",-28}{baseline.Trades,8}{baseline.WinRate,8:P1}" +
        $"{baseline.ProfitFactor,9:F3}{baseline.NetProfit,12:F2}{baseline.MaximumDrawdown,12:F2}");
    Console.WriteLine($"{"NESTED (frozen per fold)",-28}{accepted.Trades,8}{accepted.WinRate,8:P1}" +
        $"{accepted.ProfitFactor,9:F3}{accepted.NetProfit,12:F2}{accepted.MaximumDrawdown,12:F2}");

    Console.WriteLine($"\nSECONDARY - account currency (reported, never optimised)");
    Console.WriteLine($"{"column",-28}{"trades",8}{"win%",8}{"PF$",9}{"net$",12}{"maxDD$",12}");
    Console.WriteLine($"{"BASELINE (no filter)",-28}{baselineCash.Trades,8}{baselineCash.WinRate,8:P1}" +
        $"{baselineCash.ProfitFactor,9:F3}{baselineCash.NetProfit,12:F2}{baselineCash.MaximumDrawdown,12:F2}");
    Console.WriteLine($"{"NESTED (frozen per fold)",-28}{acceptedCash.Trades,8}{acceptedCash.WinRate,8:P1}" +
        $"{acceptedCash.ProfitFactor,9:F3}{acceptedCash.NetProfit,12:F2}{acceptedCash.MaximumDrawdown,12:F2}");

    // The control that decides whether any of this means anything: the frozen rule must beat
    // random subsets of the same size, or it has merely traded less.
    if (accepted.Trades > 0)
    {
        var (median, percentile, p95) = MetaFilter.RandomControl(
            result.Baseline, accepted.Trades, accepted.ProfitFactor, useRMultiple: true);
        Console.WriteLine($"\nRandom control in R ({accepted.Trades} of {baseline.Trades}): " +
            $"median PF_R {median:F3}, p95 {p95:F3}, this result at the {percentile:P1} percentile.");

        var (medianCash, percentileCash, p95Cash) = MetaFilter.RandomControl(
            result.Baseline, acceptedCash.Trades, acceptedCash.ProfitFactor, useRMultiple: false);
        Console.WriteLine($"Random control in currency (secondary): median PF$ {medianCash:F3}, " +
            $"p95 {p95Cash:F3}, this result at the {percentileCash:P1} percentile.");
    }

    int rulesChosen = result.Folds.Count(fold => fold.Rule is not null);
    Console.WriteLine($"\nFolds: {result.Folds.Count}, rule selected in {rulesChosen}, " +
        $"positive test folds (R) {result.Folds.Count(fold => fold.TestNetR > 0m)}.");
    foreach (var group in result.Folds.Where(fold => fold.Rule is not null)
        .GroupBy(fold => fold.Rule!.Value.FeatureSet).OrderByDescending(group => group.Count()))
    {
        Console.WriteLine($"  feature set '{group.Key}' chosen in {group.Count()} fold(s)");
    }

    return 0;
}

// V2 P1 + P2-slice: build the candidate-outcome dataset and regress after-cost realised R on it.
// The pre-registered statistic is out-of-sample rho (PROJECT_STATE section 3.18); bar = 0.12.
int CandidateR()
{
    string? resultPath = flags.GetValueOrDefault("simulation-result");
    if (resultPath is null)
    {
        Console.Error.WriteLine("--simulation-result <json> is required.");
        return 1;
    }

    var candidates = CandidateDatasetBuilder.LoadCandidates(
        resultPath, flags.GetValueOrDefault("strategy-id"));
    Console.WriteLine($"Candidates: {candidates.Count:N0}, " +
        $"{candidates[0].Candidate.DecisionAt:yyyy-MM-dd} -> {candidates[^1].Candidate.DecisionAt:yyyy-MM-dd}");

    // Section 3.1 item 4: deduplicate by source event before anything is trained.
    int triggerMinutes = Int("dedup-trigger-minutes", 5);
    HashSet<(DateTimeOffset, TradeLabel)> seen = [];
    List<(TradingCandidate Candidate, CandidateOutcome Outcome)> deduped = [];
    foreach (var item in candidates)
    {
        long ticks = TimeSpan.FromMinutes(triggerMinutes).Ticks;
        DateTimeOffset bucket = new(
            item.Candidate.DecisionAt.Ticks - (item.Candidate.DecisionAt.Ticks % ticks),
            item.Candidate.DecisionAt.Offset);
        if (seen.Add((bucket, item.Candidate.Side)))
            deduped.Add(item);
    }
    if (deduped.Count != candidates.Count)
        Console.WriteLine($"Deduplicated by {triggerMinutes}m source event: {candidates.Count:N0} -> {deduped.Count:N0}");

    ClassifierOptions localOptions = options with { EnabledGroups = FeatureGroups.Experiment1 };
    ClassifierDataset local = new DatasetBuilder(localOptions).Build(candles);
    Console.WriteLine($"Local causal features: {local.Rows.Count:N0} rows x {local.Schema.Count}");

    CandidateDataset dataset = CandidateDatasetBuilder.Build(deduped, local, costs);
    Console.WriteLine($"Candidate rows: {dataset.Rows.Count:N0} x {dataset.FeatureNames.Count} features " +
        $"({dataset.Unjoined:N0} could not be joined causally)");
    var constant = dataset.ConstantFeatures;
    Console.WriteLine($"Constant (zero-information) features: {(constant.Count == 0 ? "none" : string.Join(", ", constant))}");

    float[] targets = [.. dataset.Rows.Select(r => r.Target)];
    Console.WriteLine($"Target (realised R after costs): mean={targets.Average():+0.0000;-0.0000}  " +
        $"stdev={Math.Sqrt(targets.Select(t => Math.Pow(t - targets.Average(), 2)).Average()):F4}");

    int trainCount = Int("train-candidates", 600);
    int testCount = Int("test-candidates", 200);
    Console.WriteLine($"\nWalk-forward by event: {trainCount} train -> {testCount} test, rolling.\n");

    Console.WriteLine($"{"model",-18}{"folds",7}{"scored",8}{"rho",9}{"MAE",9}{"baseR",9}" +
        $"{"top20%R",10}{"top10%R",10}");
    foreach (bool lgbm in (bool[])[false, true])
    {
        RegressionReport report = CandidateRegression.Run(dataset, trainCount, testCount, lgbm);
        if (report.Predictions.Count == 0) { Console.WriteLine($"{report.Model,-18} no folds"); continue; }
        var (_, top20, _) = report.TopFraction(0.20);
        var (_, top10, _) = report.TopFraction(0.10);
        Console.WriteLine($"{report.Model,-18}{report.Folds,7}{report.Predictions.Count,8}" +
            $"{report.Rho,9:F4}{report.MeanAbsoluteError,9:F4}{report.BaselineMeanR,9:F4}" +
            $"{top20,10:F4}{top10,10:F4}");
    }

    Console.WriteLine("\nPre-registered bar (section 3.18): rho >= 0.12 proceed; 0.05-0.12 report and stop; " +
        "< 0.05 no usable information.");

    // ---- Phase 5: MFE / MAE ---------------------------------------------------------------------
    Console.WriteLine("\nEXCURSION TARGETS (Phase 5)\n");
    Console.WriteLine($"{"model",-28}{"folds",7}{"scored",8}{"rho",9}{"MAE",9}{"mean actual",13}");
    RegressionReport? mfe = null;
    foreach (CandidateTarget excursion in
        (CandidateTarget[])[CandidateTarget.MaximumFavourableR, CandidateTarget.MaximumAdverseR])
    {
        foreach (bool lgbm in (bool[])[false, true])
        {
            RegressionReport report = CandidateRegression.Run(
                dataset, trainCount, testCount, lgbm, target: excursion);
            if (report.Predictions.Count == 0) continue;
            double meanActual = report.Predictions.Average(item => (double)item.Realized);
            Console.WriteLine($"{report.Model,-28}{report.Folds,7}{report.Predictions.Count,8}" +
                $"{report.Rho,9:F4}{report.MeanAbsoluteError,9:F4}{meanActual,13:F4}");
            if (excursion == CandidateTarget.MaximumFavourableR && !lgbm)
                mfe = report;
        }
    }

    // Correlation alone does not justify a change. The question is whether a per-trade target beats
    // every fixed one — §3.19 measured the fixed optimum at ~0.75R, and every fixed multiple losing.
    if (mfe is not null)
    {
        Console.WriteLine("\nTARGET POLICY (stop fixed at 1R, pessimistic when barrier order is unknown)");
        Console.WriteLine($"{"policy",-24}{"trades",8}{"win%",8}{"expectancy R",14}{"total R",11}");
        List<TargetPolicyResult> policies =
        [
            .. new[] { 0.5, 0.75, 1.0, 1.5, 2.0 }
                .Select(multiple => TargetPolicyEvaluator.Fixed(mfe.Predictions, multiple)),
            .. new[] { 0.6, 0.8, 1.0 }
                .Select(scale => TargetPolicyEvaluator.Predicted(mfe.Predictions, scale))
        ];
        foreach (TargetPolicyResult policy in policies)
        {
            Console.WriteLine($"{policy.Policy,-24}{policy.Trades,8}{policy.WinRate,8:P1}" +
                $"{policy.ExpectancyR,14:F4}{policy.TotalR,11:F1}");
        }

        TargetPolicyResult bestFixed = policies.Where(item => item.Policy.StartsWith("fixed"))
            .OrderByDescending(item => item.ExpectancyR).First();
        TargetPolicyResult bestPredicted = policies.Where(item => item.Policy.StartsWith("predicted"))
            .OrderByDescending(item => item.ExpectancyR).First();
        Console.WriteLine($"\nBest fixed: {bestFixed.Policy} at {bestFixed.ExpectancyR:F4} R/trade");
        Console.WriteLine($"Best predicted: {bestPredicted.Policy} at {bestPredicted.ExpectancyR:F4} R/trade");
        // A raw ">" comparison is not a verdict. With R having stdev ~1 and a few thousand trades the
        // per-trade standard error is ~0.015R, so a 0.002R "win" is a tenth of a standard error and
        // means nothing. Require the difference to clear 2 standard errors before calling it real.
        double difference = bestPredicted.ExpectancyR - bestFixed.ExpectancyR;
        double[] realized = [.. mfe.Predictions.Select(item => (double)item.Outcome.RealizedRAfterCosts)];
        double mean = realized.Average();
        double deviation = Math.Sqrt(realized.Select(value => (value - mean) * (value - mean)).Average());
        double standardError = deviation / Math.Sqrt(Math.Max(realized.Length, 1));
        Console.WriteLine($"\nDifference {difference:F4} R/trade = {difference / standardError:F2} standard errors " +
            $"(SE {standardError:F4} at n={realized.Length}).");
        Console.WriteLine(difference > 2 * standardError
            ? "=> prediction beats the best fixed target by a margin larger than noise."
            : "=> NOT distinguishable from the best fixed target. MFE prediction has no operational value here.");
        if (bestPredicted.ExpectancyR < 0 && bestFixed.ExpectancyR < 0)
        {
            Console.WriteLine("   Note: both policies are negative, so this compares two losing " +
                "configurations — a better target cannot rescue a source with no edge.");
        }
    }

    return 0;
}

// Greedy forward selection over feature groups, deciding on VALIDATION only. This is the honest
// form of "reorder the ladder so harmful groups go last": the order is discovered, and the test
// period is scored once after the set is frozen.
int Select()
{
    GreedyGroupSelector selector = new(
        options, NewTrainer, costs,
        BarInterval.Minutes(resampleMinutes > 1 ? resampleMinutes : Int("interval-minutes", 15)),
        allowOverlap,
        Dbl("minimum-gain", 0.0))
    {
        // Flushed per candidate so a multi-hour search is observable rather than silent.
        Progress = line =>
        {
            Console.WriteLine(line);
            Console.Out.Flush();
        }
    };

    Console.WriteLine("Greedy forward selection (decisions on validation only)\n");
    SelectionResult result = selector.Run(candles, trainSpan, validationSpan, testSpan);

    foreach (SelectionStep step in result.Steps)
    {
        Console.WriteLine($"\n--- round {step.Round} kept {step.Added} " +
            $"({step.FeatureCount} features, val net {step.ValidationScore:F2}, gain {step.Gain:F2}) ---");
        foreach ((FeatureGroups group, double score) in step.Candidates)
            Console.WriteLine($"    {group,-22}{score,12:F2}{(group == step.Added ? "  <-- kept" : "")}");
    }

    if (result.Rejected.Count > 0)
    {
        Console.WriteLine("\nRejected (never improved validation):");
        foreach ((FeatureGroups group, double score) in result.Rejected.OrderByDescending(item => item.Score))
            Console.WriteLine($"  {group,-22} would have scored {score,10:F2}");
    }

    Console.WriteLine($"\nSelected set: {result.Selected}");
    Console.WriteLine($"Features: {result.TestFeatureCount}");
    Console.WriteLine("\nUNTOUCHED TEST PERIOD (scored once, after freezing):");
    Console.WriteLine(result.TestResult.ToText());
    return 0;
}

// Exercises the subsystems that have no other CLI path — drift detection, probability calibration,
// ensembling and the strategy selector — on a real dataset. A smoke test, not a performance test:
// the point is that each produces sensible non-degenerate output before a multi-year run.
int Diagnose()
{
    ClassifierDataset dataset = BuildDataset();
    DatasetSplit split = DatasetSplitter.Chronological(
        dataset.Rows, 0.6, 0.2, embargoRows: options.PredictionHorizon);
    Console.WriteLine($"train={split.Train.Count:N0} validation={split.Validation.Count:N0} test={split.Test.Count:N0}\n");

    // ---- drift: does the test period look like the training period? -----------------------------
    Console.WriteLine("DRIFT (train -> test, PSI; >0.25 = significant shift)");
    List<DriftReport> drift = [];
    for (int column = 0; column < dataset.Schema.Count; column++)
    {
        int index = column;
        drift.Add(ModelDriftDetector.Compare(
            dataset.Schema.Features[index].Name,
            [.. split.Train.Select(row => (double)row.Features.Values[index])],
            [.. split.Test.Select(row => (double)row.Features.Values[index])]));
    }
    foreach (DriftReport report in drift.OrderByDescending(item => item.PopulationStabilityIndex).Take(8))
        Console.WriteLine($"  {report.Name,-28}{report.PopulationStabilityIndex,8:F3}  {report.Verdict}");
    int shifted = drift.Count(item => item.RequiresAttention);
    Console.WriteLine($"  {shifted} of {drift.Count} features show a significant shift.\n");

    // ---- calibration: are the probabilities worth their face value? -----------------------------
    using TrainedModel model = NewTrainer().Train(
        SampleUniqueness.Stride(split.Train, options.TrainingStride), dataset.Schema);

    List<double> scores = [];
    List<bool> outcomes = [];
    foreach (LabeledFeatureRow row in split.Validation)
    {
        Prediction prediction = model.Model.Predict(row.Features);
        scores.Add(prediction.BuyProbability);
        outcomes.Add(row.Label == TradeLabel.Buy);
    }

    PlattCalibrator platt = PlattCalibrator.Fit(scores, outcomes);
    IsotonicCalibrator? isotonic = IsotonicCalibrator.Fit(scores, outcomes);
    Console.WriteLine("CALIBRATION (Buy probability, fitted on VALIDATION only)");
    Console.WriteLine($"  {"variant",-22}{"Brier",10}{"mean p",10}{"actual",10}");
    Console.WriteLine($"  {"raw",-22}{Brier(scores, outcomes, x => x),10:F4}" +
        $"{scores.Average(),10:F4}{outcomes.Count(o => o) / (double)outcomes.Count,10:F4}");
    Console.WriteLine($"  {"Platt",-22}{Brier(scores, outcomes, platt.Calibrate),10:F4}" +
        $"{scores.Select(platt.Calibrate).Average(),10:F4}");
    Console.WriteLine(isotonic is null
        ? "  isotonic               declined (validation sample below its minimum)"
        : $"  {"Isotonic",-22}{Brier(scores, outcomes, isotonic.Calibrate),10:F4}" +
          $"{scores.Select(isotonic.Calibrate).Average(),10:F4}");

    // ---- ensemble: does averaging two trainers behave? -------------------------------------------
    using TrainedModel second = new LogisticRegressionBaselineTrainer().Train(
        SampleUniqueness.Stride(split.Train, options.TrainingStride), dataset.Schema);
    EnsembleModel ensemble = new([model.Model, second.Model]);
    ModelEvaluator evaluator = new(options, costs, allowOverlappingPositions: allowOverlap);
    Console.WriteLine("\nENSEMBLE (test period)");
    foreach ((string name, ITradingModel member) in
        (( string, ITradingModel)[])[("LightGBM", model.Model), ("Logistic", second.Model), ("Ensemble", ensemble)])
    {
        TradingReport report = ClassifierBacktester.Run(
            split.Test,
            [.. split.Test.Select(row => member.Predict(row.Features))],
            options, costs, allowOverlap);
        Console.WriteLine($"  {name,-22}trades={report.Trades,5}  PF={report.ProfitFactor,7:F3}  net={report.NetProfit,10:F2}");
    }

    // ---- strategy selector: does it refuse what it should? ---------------------------------------
    StrategySelector selector = new();
    var choices = selector.Fit(
    [
        new RegimePerformance { StrategyId = "thin", Regime = ChartAnnotator.Regime.MarketRegime.Range, Trades = 5, TotalR = 12 },
        new RegimePerformance { StrategyId = "losing", Regime = ChartAnnotator.Regime.MarketRegime.TrendingUp, Trades = 120, TotalR = -18 },
        new RegimePerformance { StrategyId = "good", Regime = ChartAnnotator.Regime.MarketRegime.Compression, Trades = 150, TotalR = 30 }
    ]);
    Console.WriteLine("\nSTRATEGY SELECTOR");
    foreach (StrategyChoice choice in choices)
        Console.WriteLine($"  {choice.Regime,-18}{choice.StrategyId ?? "(none)",-10}{choice.Reason}");

    return 0;

    static double Brier(IReadOnlyList<double> values, IReadOnlyList<bool> actual, Func<double, double> map)
    {
        double total = 0;
        for (int index = 0; index < values.Count; index++)
        {
            double predicted = map(values[index]);
            double truth = actual[index] ? 1.0 : 0.0;
            total += (predicted - truth) * (predicted - truth);
        }
        return values.Count == 0 ? 0 : total / values.Count;
    }
}

// Fits and saves the adverse-excursion model used for ML stop placement. Separate command, and
// therefore a separate training window from whatever it is later scored on — fitting inside a
// backtest would train on the period being measured.
int TrainStop()
{
    string? resultPath = flags.GetValueOrDefault("simulation-result");
    string? savePath = flags.GetValueOrDefault("save");
    if (resultPath is null || savePath is null)
    {
        Console.Error.WriteLine("--simulation-result <json> and --save <path> are required.");
        return 1;
    }

    var candidates = CandidateDatasetBuilder.LoadCandidates(
        resultPath, flags.GetValueOrDefault("strategy-id"));
    // Same feature groups the AGENT will pass at decision time — the model must consume exactly
    // what the caller can produce, and the guard in MlStopPlacementModel enforces that.
    ClassifierDataset local = FeatureEngine.RequiresAnnotation(options.EnabledGroups)
        ? new AnnotationDatasetBuilder(options, BarInterval.Minutes(
            resampleMinutes > 1 ? resampleMinutes : Int("interval-minutes", 15))).Build(candles)
        : new DatasetBuilder(options).Build(candles);

    // includeGeometry: false — the geometry group contains stop_distance_atr, which is the very
    // quantity being predicted. Training on it would be circular.
    CandidateDataset dataset = CandidateDatasetBuilder.Build(
        candidates, local, costs, includeGeometry: false);

    // Only rows whose adverse excursion is known are usable.
    var rows = dataset.Rows
        .Where(row => row.Outcome.MaximumAdverseAtr > 0m)
        .Select(row => (row.Features, (float)row.Outcome.MaximumAdverseAtr))
        .ToArray();

    Console.WriteLine($"Candidates: {dataset.Rows.Count:N0}, usable for stop fitting: {rows.Length:N0}");
    if (rows.Length > 0)
    {
        float[] targets = [.. rows.Select(row => row.Item2)];
        Console.WriteLine($"Adverse excursion (ATR): mean={targets.Average():F3} " +
            $"min={targets.Min():F3} max={targets.Max():F3}");
    }

    bool useLightGbm = !string.Equals(
        flags.GetValueOrDefault("stop-trainer"), "sdca", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"Trainer: {(useLightGbm ? "LightGBM" : "Sdca (regularised linear)")}");
    MlStopPlacementModel? model = MlStopPlacementModel.Fit(
        rows, dataset.FeatureNames, useLightGbm: useLightGbm);
    if (model is null)
    {
        Console.Error.WriteLine(
            $"Refusing to fit: {rows.Length} rows is below the minimum. A model fitted here would " +
            "return a near-constant and make the A/B against the structural stop meaningless.");
        return 2;
    }

    MlStopPlacementModel.Save(
        savePath, MlStopPlacementModel.Fitted!, MlStopPlacementModel.FittedSchema!, dataset.FeatureNames);
    Console.WriteLine($"Saved stop model to {savePath} ({dataset.FeatureNames.Count} features)");
    return 0;
}

ClassifierDataset BuildDataset()
{
    ClassifierDataset dataset = FeatureEngine.RequiresAnnotation(options.EnabledGroups)
        ? new AnnotationDatasetBuilder(options, BarInterval.Minutes(resampleMinutes > 1 ? resampleMinutes : Int("interval-minutes", 15)))
            .Build(candles)
        : new DatasetBuilder(options).Build(candles);
    if (dataset.Rows.Count == 0)
        throw new InvalidOperationException("The dataset is empty: not enough candles to clear warm-up plus the label horizon.");

    Console.WriteLine($"Dataset: {dataset.Rows.Count:N0} rows x {dataset.Schema.Count} features");
    if (dataset.RowsDroppedForGaps > 0)
    {
        Console.WriteLine($"  {dataset.RowsDroppedForGaps:N0} rows dropped: a data gap stretched their " +
            $"label window beyond {dataset.InferredSpacing * options.PredictionHorizon}");
    }
    // Section 18 expects a NO_TRADE-heavy distribution; printing it makes the imbalance visible
    // before any metric is read.
    foreach ((TradeLabel label, int count) in dataset.ClassCounts.OrderBy(pair => pair.Key))
        Console.WriteLine($"  {label,-9}{count,8:N0}  {(double)count / dataset.Rows.Count,7:P1}");

    // A column that is constant across the whole dataset carries no information and is almost
    // always a wiring fault rather than a property of the market - a feature the engine declared
    // but never actually populated. Cheap to check, and it fails silently otherwise.
    if (flags.ContainsKey("feature-stats"))
    {
        Console.WriteLine($"\n{"feature",-38}{"nonzero%",10}{"distinct",10}{"min",12}{"max",12}");
        for (int column = 0; column < dataset.Schema.Count; column++)
        {
            int index = column;
            float[] all = dataset.Rows.Select(row => row.Features.Values[index]).ToArray();
            int distinct = all.Distinct().Count();
            Console.WriteLine($"{dataset.Schema.Features[index].Name,-38}" +
                $"{all.Count(v => v != 0f) / (double)all.Length,10:P1}{distinct,10}" +
                $"{all.Min(),12:G4}{all.Max(),12:G4}" +
                (distinct == 1 ? "   <-- CONSTANT" : string.Empty));
        }
        int constant = Enumerable.Range(0, dataset.Schema.Count)
            .Count(c => dataset.Rows.Select(r => r.Features.Values[c]).Distinct().Count() == 1);
        Console.WriteLine($"\nconstant columns: {constant} of {dataset.Schema.Count}");
    }

    return dataset;
}

IModelTrainer NewTrainer() => new LightGbmModelTrainer(new LightGbmTrainingOptions
{
    NumberOfLeaves = Int("leaves", 31),
    NumberOfIterations = Int("iterations", 200),
    LearningRate = Dbl("learning-rate", 0.1),
    MinimumExampleCountPerLeaf = Int("min-leaf", 40),
    UseClassWeights = !flags.ContainsKey("no-class-weights")
});

IReadOnlyList<ClassifierCandle> LoadCandles()
{
    if (flags.TryGetValue("csv", out string? csv))
        return CandleSources.FromCsv(csv);
    if (flags.TryGetValue("simulation-market", out string? market))
        return CandleSources.FromSimulationMarket(market);
    if (flags.TryGetValue("historical", out string? historical))
        return CandleSources.FromHistoricalCache(historical);
    if (flags.TryGetValue("random-walk", out string? count))
        return CandleSources.RandomWalk(int.Parse(count, CultureInfo.InvariantCulture));

    Console.Error.WriteLine("Provide --csv, --simulation-market, --historical or --random-walk.");
    return [];
}

int Int(string name, int fallback) =>
    flags.TryGetValue(name, out string? value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

double Dbl(string name, double fallback) =>
    flags.TryGetValue(name, out string? value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;

decimal Dec(string name, decimal fallback) =>
    flags.TryGetValue(name, out string? value) ? decimal.Parse(value, CultureInfo.InvariantCulture) : fallback;

TimeSpan Days(string name, int fallback) => TimeSpan.FromDays(Int(name, fallback));

// Accepts a section 35 rung ("experiment3"), the word "all", or a comma-separated group list
// ("PriceAction,Trend,Rsi") so a ladder finding can be re-run on its own for section 36 detail.
FeatureGroups Groups(string name, FeatureGroups fallback)
{
    if (!flags.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
        return fallback;

    FeatureGroups parsed = FeatureGroups.None;
    foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Enum.TryParse(part, ignoreCase: true, out FeatureGroups group))
            throw new ArgumentException($"Unknown feature group '{part}'.");
        parsed |= group;
    }
    return parsed;
}

static Dictionary<string, string> ParseFlags(IEnumerable<string> arguments)
{
    Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
    string? pending = null;

    foreach (string argument in arguments)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            // A bare switch (--importance) is recorded with an empty value the moment the next
            // flag arrives, so presence-only flags and value flags can share one parser.
            if (pending is not null)
                parsed[pending] = string.Empty;
            pending = argument[2..];
            if (pending.Contains('='))
            {
                string[] parts = pending.Split('=', 2);
                parsed[parts[0]] = parts[1];
                pending = null;
            }
        }
        else if (pending is not null)
        {
            parsed[pending] = argument;
            pending = null;
        }
    }

    if (pending is not null)
        parsed[pending] = string.Empty;
    return parsed;
}

static void PrintUsage() => Console.WriteLine("""
    TradingClassifierRunner - Trading Classification Model V1 (blueprint sections 26, 24, 35)

      train           Chronological split, train baseline + LightGBM, report, optionally save
      walk-forward    Section 16 rolling windows; exit code 2 if the section 36 bar is not met
      ladder          Section 35 experiment ladder (OHLC -> EMA -> RSI/CCI -> ATR -> MACD/BB)
      ablate          Section 24 ablation (all features, then minus each group)
      trend-stats     Blueprint section 55 Phase 2: segment trends and report the bull/bear
                      library. Use --resample 240 for the 4H default.
                      --trend-ema 20 --trend-atr 14 --trend-candidate-atr 0.75
                      --trend-confirm-atr 1.25 --trend-end-atr 1.25
      trend-walk-forward  Blueprint sections 48/60 (V7/V8): per-window threshold selection on a
                      validation slice, run unconditioned and regime-conditioned, after costs.
                      --test-days 365 --validation-days 365 --min-train-days 1460
                      --round-trip-cost-pct 0.03
      swing-entry     Blueprint sections 29/30: entry percentile sweep P05-P25 across the
                      price-only / time-only / price-and-time experiments. --min-trend-samples 20
                      --round-trip-cost-pct 0.03
      fetch           Download OANDA history using the encrypted database credential vault
                      --instrument METAL:XAU/USD --interval 15m --from --to --out <file> [--live]
      filter-signals  Meta-label another strategy's trade log: does the classifier improve it?
                      --trades <csv>              the primary strategy's trade log, or
                      --simulation-result <json>  a run's simulation-result.json (preferred:
                                                  carries the real signalCreatedAt)
                      --strategy-id breakout-detector

    Data source (one required)
      --csv <path>                  timestamp,open,high,low,close
      --simulation-market <dir>     a run's market/ directory of chunk-*.json.gz
      --historical <file>           .cache/historical/*.jsonl.gz
      --random-walk <count>         synthetic negative control
      --resample <minutes>          aggregate 1m input into N-minute candles (5, 15, 60, ...)
      --interval-minutes 15         candle interval when input is already at the target timeframe
                                    (only matters for the Analysis feature group)

    Target and decision layer (section 34)
      --horizon 10                  prediction horizon in candles
      --training-stride N           keep every N-th TRAINING row (default: horizon, giving
                                    non-overlapping labels); 1 restores the old behaviour
      --atr-multiplier 0.75         label threshold in ATR multiples
      --max-excursion               use the section 12 target instead of future close
      --buy-threshold 0.70          section 20 confidence filter
      --sell-threshold 0.70
      --no-tune                     skip validation threshold tuning

    Walk-forward spans
      --train-days 90  --validation-days 30  --test-days 30

    LightGBM
      --leaves 31  --iterations 200  --learning-rate 0.1  --min-leaf 40
      --no-class-weights            disable the section 18 inverse-frequency weighting

    Costs (section 22)
      --half-spread 0  --commission 0  --slippage 0

    Feature set (section 35 / 24)
      --groups experiment3          a ladder rung, "all", or a comma-separated group list
      --atr-levels 14,20            re-add the raw atr{n}_pct level columns (off by default:
                                    they do not survive a volatility regime shift)

    Other
      --train-fraction 0.6  --validation-fraction 0.2
      --train-from / --train-to     trim the candle series before building any dataset,
                                    so a model's training window can exclude the period it
                                    will later be scored on
      --allow-overlap               permit simultaneous holds. OFF by default: overlapping positions
                                    are not tradeable on one account and inflate trade counts.
                                    Applies to train, walk-forward, ladder and ablate alike
      --importance                  permutation feature importance (section 23)
      --feature-stats               per-column spread; flags constant (unpopulated) columns
      --save <path>                 write the fitted model
    """);

// Spearman rho with average ranks for ties. Returns 0 when either side is constant, which is the
// honest answer for a model that emits one score.
static double RankCorrelation(double[] xs, double[] ys)
{
    static double[] Ranks(double[] values)
    {
        int[] order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        double[] ranks = new double[values.Length];
        int index = 0;
        while (index < order.Length)
        {
            int last = index;
            while (last + 1 < order.Length && values[order[last + 1]] == values[order[index]])
                last++;
            double shared = (index + last) / 2.0 + 1.0;
            for (int k = index; k <= last; k++)
                ranks[order[k]] = shared;
            index = last + 1;
        }

        return ranks;
    }

    if (xs.Length < 2)
        return 0;

    double[] rx = Ranks(xs), ry = Ranks(ys);
    double mx = rx.Average(), my = ry.Average();
    double num = 0, dx = 0, dy = 0;
    for (int i = 0; i < rx.Length; i++)
    {
        double a = rx[i] - mx, b = ry[i] - my;
        num += a * b; dx += a * a; dy += b * b;
    }

    return dx <= 0 || dy <= 0 ? 0 : num / Math.Sqrt(dx * dy);
}
