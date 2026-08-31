using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using Brokers.Models;
using TradingClassifier.ML.Evaluation;
using TradingClassifier.ML.Training;

namespace TradingClassifier.ML.Experiments;

/// <summary>One walk-forward window's out-of-sample result.</summary>
public sealed record WalkForwardWindowResult
{
    public required string Window { get; init; }
    public required DateTimeOffset TestStart { get; init; }
    public required DateTimeOffset TestEnd { get; init; }
    public required EvaluationResult Test { get; init; }
    public required double TunedBuyThreshold { get; init; }
    public required double TunedSellThreshold { get; init; }
}

/// <summary>Every window plus the pooled verdict section 36 actually cares about.</summary>
public sealed record WalkForwardResult
{
    public required string ModelName { get; init; }
    public required IReadOnlyList<WalkForwardWindowResult> Windows { get; init; }
    public required TradingReport Pooled { get; init; }

    /// <summary>Windows that could not be trained because their train slice held one class.</summary>
    public IReadOnlyList<string> SkippedWindows { get; init; } = [];

    /// <summary>Section 36: "no single period responsible for all profit".</summary>
    public bool IsConcentratedInOnePeriod
    {
        get
        {
            if (Windows.Count < 2 || Pooled.NetProfit <= 0m)
                return false;
            decimal best = Windows.Max(window => window.Test.Trading.NetProfit);
            return best >= Pooled.NetProfit;
        }
    }

    /// <summary>Section 36: profitable, stable, and not carried by a single window.</summary>
    public bool MeetsSuccessBar =>
        Pooled.MeetsSuccessBar
        && Windows.Count >= 2
        && !IsConcentratedInOnePeriod
        && Windows.Count(window => window.Test.Trading.NetProfit > 0m) * 2 >= Windows.Count;

    public string ToText()
    {
        System.Text.StringBuilder builder = new();
        builder.AppendLine($"Walk-forward: {ModelName}, {Windows.Count} window(s)");
        foreach (WalkForwardWindowResult window in Windows)
        {
            builder.AppendLine(
                $"  {window.Window,-10} {window.TestStart:yyyy-MM-dd}->{window.TestEnd:yyyy-MM-dd}  " +
                $"thr {window.TunedBuyThreshold:F2}/{window.TunedSellThreshold:F2}  {window.Test.Trading.ToText()}");
        }
        if (SkippedWindows.Count > 0)
            builder.AppendLine($"  skipped (single-class train slice): {string.Join(", ", SkippedWindows)}");
        builder.AppendLine($"  POOLED  {Pooled.ToText()}");
        builder.AppendLine($"  success bar (section 36): {(MeetsSuccessBar ? "MET" : "not met")}" +
            (IsConcentratedInOnePeriod ? " - all profit comes from one window" : string.Empty));
        return builder.ToString();
    }
}

/// <summary>
/// Section 16's walk-forward evaluation, which section 36 makes the deciding test.
/// <para>
/// Each window fits on its own train slice, tunes thresholds on its own validation slice
/// (section 20) and is scored on a test slice it has never touched. Windows are embargoed by the
/// prediction horizon so no label straddles a boundary.
/// </para>
/// </summary>
public sealed class WalkForwardRunner
{
    private readonly ClassifierOptions _options;
    private readonly IModelTrainer _trainer;
    private readonly ModelEvaluator _evaluator;
    private readonly TradingCostModel _costs;

    private readonly bool _allowOverlap;

    public WalkForwardRunner(
        ClassifierOptions options,
        IModelTrainer trainer,
        TradingCostModel? costs = null,
        bool allowOverlappingPositions = false)
    {
        _allowOverlap = allowOverlappingPositions;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trainer);
        options.Validate();
        _options = options;
        _trainer = trainer;
        _costs = costs ?? TradingCostModel.Free;
        _evaluator = new ModelEvaluator(options, _costs, 0, allowOverlappingPositions);
    }

    public WalkForwardResult Run(
        ClassifierDataset dataset,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan,
        bool tuneThresholds = true,
        bool scoreValidationInsteadOfTest = false)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        IReadOnlyList<DatasetSplit> splits = DatasetSplitter.WalkForward(
            dataset.Rows, trainSpan, validationSpan, testSpan, embargoRows: _options.PredictionHorizon);

        if (splits.Count == 0)
        {
            throw new InvalidOperationException(
                "The dataset is too short for even one walk-forward window with these spans.");
        }

        List<WalkForwardWindowResult> results = [];
        List<ClassifierTrade> pooledTrades = [];

        List<string> skipped = [];

        foreach (DatasetSplit split in splits)
        {
            // A window whose training slice holds a single class is a fact about that period, not
            // an error: skipping it keeps the remaining windows reportable, where letting the
            // trainer throw would lose the whole walk-forward run to one quiet fortnight.
            if (split.Train.Select(row => row.Label).Distinct().Count() < 2)
            {
                skipped.Add(split.Label);
                continue;
            }

            // Stride applies to TRAINING only: validation and test keep every row, because
            // inference is asked about every bar.
            IReadOnlyList<LabeledFeatureRow> trainRows =
                SampleUniqueness.Stride(split.Train, _options.TrainingStride);
            using TrainedModel model = _trainer.Train(trainRows, dataset.Schema);

            ClassifierOptions tuned = tuneThresholds
                ? _evaluator.TuneThresholds(model, split.Validation)
                : _options;

            // A fresh evaluator so the test scoring uses the tuned thresholds, not the defaults.
            ModelEvaluator scorer = new(tuned, _costs, 0, _allowOverlap);

            // Feature SELECTION must never see the test period, or the chosen set is fitted to the
            // answer — the mechanism that made §3.12's winner collapse in §3.12g. In that mode the
            // validation slice is scored instead and the test rows are not touched at all.
            IReadOnlyList<LabeledFeatureRow> scored =
                scoreValidationInsteadOfTest ? split.Validation : split.Test;
            if (scored.Count == 0)
                continue;

            EvaluationResult test = scorer.Evaluate(model, scored, split.Label);

            results.Add(new WalkForwardWindowResult
            {
                Window = split.Label,
                TestStart = scored[0].Features.Timestamp,
                TestEnd = scored[^1].Features.Timestamp,
                Test = test,
                TunedBuyThreshold = tuned.BuyProbabilityThreshold,
                TunedSellThreshold = tuned.SellProbabilityThreshold
            });

            pooledTrades.AddRange(test.Trading.TradeList);
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                $"Every one of the {splits.Count} walk-forward window(s) had a single-class training " +
                "slice, so none could be trained. The label threshold is almost certainly too wide " +
                "for this data: lower AtrTargetMultiplier or raise PredictionHorizon.");
        }

        return new WalkForwardResult
        {
            ModelName = _trainer.Name,
            Windows = results,
            Pooled = ClassifierBacktester.Summarise(pooledTrades),
            SkippedWindows = skipped
        };
    }
}

/// <summary>One rung of the section 35 ladder, or one row of the section 24 ablation table.</summary>
public sealed record ExperimentResult
{
    public required string Name { get; init; }
    public required FeatureGroups Groups { get; init; }
    public required int FeatureCount { get; init; }
    public required WalkForwardResult WalkForward { get; init; }
}

/// <summary>
/// Sections 24 and 35. Both change which feature groups are enabled, rebuild the dataset from
/// candles, retrain and compare - so they are the same routine with different group sets.
/// </summary>
public sealed class FeatureExperimentRunner
{
    private readonly ClassifierOptions _baseOptions;
    private readonly Func<IModelTrainer> _trainerFactory;
    private readonly TradingCostModel _costs;
    private readonly BarInterval _interval;

    // Every rung must be scored under the same execution policy as `walk-forward`, or the ladder
    // compares feature sets across two different tradeable strategies.
    private readonly bool _allowOverlap;

    private IReadOnlyList<ClassifierCandle>? _cachedCandles;
    private ClassifierDataset? _cachedSuperset;

    public FeatureExperimentRunner(
        ClassifierOptions baseOptions,
        Func<IModelTrainer> trainerFactory,
        TradingCostModel? costs = null,
        BarInterval interval = default,
        bool allowOverlappingPositions = false)
    {
        _allowOverlap = allowOverlappingPositions;
        ArgumentNullException.ThrowIfNull(baseOptions);
        ArgumentNullException.ThrowIfNull(trainerFactory);
        baseOptions.Validate();
        _baseOptions = baseOptions;
        _trainerFactory = trainerFactory;
        _costs = costs ?? TradingCostModel.Free;
        _interval = interval.IsValid ? interval : BarInterval.Minutes(15);
    }

    /// <summary>Section 35's ladder: OHLC only, then EMA, then RSI+CCI, then ATR, then MACD+BB.</summary>
    public IReadOnlyList<ExperimentResult> RunLadder(
        IReadOnlyList<ClassifierCandle> candles,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan)
    {
        // Order reflects measured marginal value, not the blueprint's original listing. Groups that
        // COST performance are demoted to the tail so they cannot contaminate the rungs above them:
        // §3.12 found ATR harmful, and on the 41-day window EMA took OHLC-only from +553.81 to
        // +161.50 while MACD/BB took rung 3 from +838.37 to -350.35. Bollinger stays early because
        // OHLC + BB + RSI/CCI is the combination §3.12 identified as strongest.
        //
        // NOTE: this hand-ordering is informed by test-period results and is therefore a DIAGNOSTIC,
        // not a selection procedure. `select` (GreedyGroupSelector) is the honest version — it makes
        // every add/drop decision on validation folds and scores the winner once on test.
        const FeatureGroups Ohlc = FeatureGroups.Experiment1;
        const FeatureGroups Bb = Ohlc | FeatureGroups.Bollinger;
        const FeatureGroups Core = Bb | FeatureGroups.Rsi | FeatureGroups.Cci;
        const FeatureGroups Rich = Core | FeatureGroups.Analysis;
        const FeatureGroups Indicators = Rich | FeatureGroups.Adx | FeatureGroups.StochRsi |
            FeatureGroups.Donchian | FeatureGroups.Efficiency | FeatureGroups.Volume;
        const FeatureGroups Geometry = Indicators | FeatureGroups.Structure |
            FeatureGroups.SupportResistance | FeatureGroups.SupplyDemand | FeatureGroups.Liquidity |
            FeatureGroups.Regime;

        (string Name, FeatureGroups Groups)[] ladder =
        [
            ("1 OHLC only", Ohlc),
            ("2 + BB", Bb),
            ("3 + RSI/CCI", Core),
            ("4 + Analysis", Rich),
            ("5 + ADX", Rich | FeatureGroups.Adx),
            ("6 + StochRSI", Rich | FeatureGroups.Adx | FeatureGroups.StochRsi),
            ("7 + Donchian", Rich | FeatureGroups.Adx | FeatureGroups.StochRsi | FeatureGroups.Donchian),
            ("8 + Efficiency", Rich | FeatureGroups.Adx | FeatureGroups.StochRsi |
                FeatureGroups.Donchian | FeatureGroups.Efficiency),
            ("9 + Volume", Indicators),
            ("10 + Structure", Indicators | FeatureGroups.Structure),
            ("11 + S/R", Indicators | FeatureGroups.Structure | FeatureGroups.SupportResistance),
            ("12 + SupplyDemand", Indicators | FeatureGroups.Structure |
                FeatureGroups.SupportResistance | FeatureGroups.SupplyDemand),
            ("13 + Liquidity", Indicators | FeatureGroups.Structure |
                FeatureGroups.SupportResistance | FeatureGroups.SupplyDemand | FeatureGroups.Liquidity),
            ("14 + Regime", Geometry),
            // Phase 4 cross-timeframe: the only genuinely untested ML idea in this repo. Every
            // prior negative result used a single-timeframe model.
            ("15 + TrendState 2h", Geometry | FeatureGroups.TrendState2h),
            ("15b + TrendState 1h", Geometry | FeatureGroups.TrendState2h |
                FeatureGroups.TrendState1h),
            ("15c + TrendState 30m", Geometry | FeatureGroups.TrendState),
            // Demoted tail: each of these measured NEGATIVE, so they are added last where their cost
            // is isolated instead of being paid by every rung above.
            ("16 + EMA (demoted)", Geometry | FeatureGroups.TrendState | FeatureGroups.Trend),
            ("17 + MACD (demoted)", Geometry | FeatureGroups.TrendState | FeatureGroups.Trend |
                FeatureGroups.Macd),
            ("18 + ATR (last)", FeatureGroups.Everything)
        ];

        return ladder
            .Select(rung => Run(rung.Name, rung.Groups, candles, trainSpan, validationSpan, testSpan))
            .ToArray();
    }

    /// <summary>
    /// Section 24's ablation: the full set, then the full set minus each group in turn.
    /// </summary>
    public IReadOnlyList<ExperimentResult> RunAblation(
        IReadOnlyList<ClassifierCandle> candles,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan)
    {
        // The base set comes from the runner's own options (i.e. --groups), not a hardcoded
        // FeatureGroups.All. All stops at Experiment5, so ablating a group outside it silently
        // removed nothing and reported a row identical to the baseline — indistinguishable in the
        // results table from "this group has no effect".
        FeatureGroups baseGroups = _baseOptions.EnabledGroups;
        List<ExperimentResult> results =
        [
            Run("all features", baseGroups, candles, trainSpan, validationSpan, testSpan)
        ];

        FeatureGroups[] removable =
        [
            FeatureGroups.Rsi, FeatureGroups.Cci, FeatureGroups.Macd,
            FeatureGroups.Trend, FeatureGroups.Atr, FeatureGroups.Bollinger,
            FeatureGroups.RangePosition, FeatureGroups.PriceAction,
            FeatureGroups.Adx, FeatureGroups.StochRsi, FeatureGroups.Donchian,
            FeatureGroups.Efficiency, FeatureGroups.Volume, FeatureGroups.Structure,
            FeatureGroups.SupportResistance, FeatureGroups.SupplyDemand,
            FeatureGroups.Liquidity, FeatureGroups.Regime,
            FeatureGroups.TrendState30m, FeatureGroups.TrendState1h, FeatureGroups.TrendState2h
        ];

        foreach (FeatureGroups group in removable)
        {
            // Skip groups the base set never contained: an unchanged row is worse than no row,
            // because it reads as a measured null result.
            if (!baseGroups.HasFlag(group))
                continue;

            FeatureGroups reduced = baseGroups & ~group;
            if (reduced == FeatureGroups.None)
                continue;
            results.Add(Run($"minus {group}", reduced, candles, trainSpan, validationSpan, testSpan));
        }

        return results;
    }

    public ExperimentResult Run(
        string name,
        FeatureGroups groups,
        IReadOnlyList<ClassifierCandle> candles,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan)
    {
        ArgumentNullException.ThrowIfNull(candles);

        // The label must not move between rungs. LabelAtrPeriod is a separate option from the ATR
        // *features* precisely so that removing FeatureGroups.Atr still leaves the same target -
        // otherwise the ablation would be comparing two different prediction problems and the PnL
        // difference would mean nothing.
        ClassifierOptions options = _baseOptions with { EnabledGroups = groups };

        // The Analysis group needs the annotation engine's snapshots, which the light-weight
        // DatasetBuilder does not produce. Rungs without it keep the cheap path.
        ClassifierDataset dataset = BuildOrProject(options, groups, candles);

        WalkForwardRunner runner = new(options, _trainerFactory(), _costs, _allowOverlap);
        WalkForwardResult walkForward = runner.Run(dataset, trainSpan, validationSpan, testSpan);

        return new ExperimentResult
        {
            Name = name,
            Groups = groups,
            FeatureCount = dataset.Schema.Count,
            WalkForward = walkForward
        };
    }

    /// <summary>
    /// Builds the superset dataset once per candle series and projects each rung out of it.
    /// <para>
    /// Without this the ladder reruns the annotation engine for every rung; on 84k 15m candles that
    /// was slow enough to time out before the first rung reported. The cache key is the candle
    /// series identity, so a caller sweeping rungs over one series pays the cost once.
    /// </para>
    /// </summary>
    private ClassifierDataset BuildOrProject(
        ClassifierOptions options, FeatureGroups groups, IReadOnlyList<ClassifierCandle> candles)
    {
        if (!FeatureEngine.RequiresAnnotation(groups))
            return new DatasetBuilder(options).Build(candles);

        if (!ReferenceEquals(_cachedCandles, candles) || _cachedSuperset is null)
        {
            // Superset must be FeatureGroups.Everything, not All: All deliberately stops at
            // Experiment5 so that default options keep the cheap DatasetBuilder path. Projecting a
            // rung out of All silently dropped every annotation-backed column instead of failing,
            // which made rungs 6-10 byte-identical to rung 5.
            ClassifierOptions supersetOptions = options with { EnabledGroups = FeatureGroups.Everything };
            _cachedSuperset = new AnnotationDatasetBuilder(supersetOptions, _interval).Build(candles);
            _cachedCandles = candles;
        }

        ClassifierDataset superset = _cachedSuperset;
        IReadOnlyList<int> keep = superset.Schema.IndicesFor(groups);
        FeatureSchema schema = superset.Schema.Restrict(groups);

        List<LabeledFeatureRow> rows = new(superset.Rows.Count);
        foreach (LabeledFeatureRow row in superset.Rows)
        {
            float[] values = new float[keep.Count];
            for (int index = 0; index < keep.Count; index++)
                values[index] = row.Features.Values[keep[index]];

            rows.Add(row with
            {
                Features = row.Features with { Values = values }
            });
        }

        return new ClassifierDataset { Schema = schema, Rows = rows };
    }

    /// <summary>
    /// Scores a group set on the VALIDATION slices only. This is what feature selection is allowed
    /// to look at; <see cref="Run"/> scores the untouched test period and must be called once, after
    /// the group set is frozen.
    /// </summary>
    public ExperimentResult RunValidationOnly(
        FeatureGroups groups,
        IReadOnlyList<ClassifierCandle> candles,
        TimeSpan trainSpan,
        TimeSpan validationSpan,
        TimeSpan testSpan)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ClassifierOptions options = _baseOptions with { EnabledGroups = groups };
        ClassifierDataset dataset = BuildOrProject(options, groups, candles);

        WalkForwardRunner runner = new(options, _trainerFactory(), _costs, _allowOverlap);
        WalkForwardResult walkForward = runner.Run(
            dataset, trainSpan, validationSpan, testSpan,
            tuneThresholds: true, scoreValidationInsteadOfTest: true);

        return new ExperimentResult
        {
            Name = $"validation:{groups}",
            Groups = groups,
            FeatureCount = dataset.Schema.Count,
            WalkForward = walkForward
        };
    }

    public static string ToTable(IReadOnlyList<ExperimentResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        System.Text.StringBuilder builder = new();
        builder.AppendLine($"{"experiment",-18}{"features",9}{"trades",8}{"win%",8}{"PF",9}{"net",12}{"maxDD",12}");
        foreach (ExperimentResult result in results)
        {
            TradingReport pooled = result.WalkForward.Pooled;
            builder.AppendLine(
                $"{result.Name,-18}{result.FeatureCount,9}{pooled.Trades,8}{pooled.WinRate,8:P1}" +
                $"{pooled.ProfitFactor,9:F3}{pooled.NetProfit,12:F2}{pooled.MaximumDrawdown,12:F2}");
        }
        return builder.ToString();
    }
}
