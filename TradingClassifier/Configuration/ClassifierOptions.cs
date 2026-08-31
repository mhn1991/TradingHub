namespace TradingClassifier.Configuration;

/// <summary>
/// Every value the blueprint's section 34 says must not be hard-coded. One options object drives
/// feature calculation, label generation and the decision layer, so a training run and the live
/// agent that consumes its model are configured from the same shape - see section 27, which
/// requires one shared implementation rather than parallel training/live logic.
/// </summary>
public sealed record ClassifierOptions
{
    /// <summary>Section 28: how many candles ahead the label looks.</summary>
    public int PredictionHorizon { get; init; } = 10;

    /// <summary>Section 11: label threshold expressed in ATR multiples, so it adapts to volatility.</summary>
    public decimal AtrTargetMultiplier { get; init; } = 0.75m;

    /// <summary>Section 12: false = future close (V1), true = maximum excursion over the window.</summary>
    public bool UseMaximumExcursionLabels { get; init; }

    /// <summary>
    /// Keep every N-th row when TRAINING, so overlapping labels do not present the same market
    /// episode many times over.
    /// <para>
    /// A row at <c>t</c> is labelled from <c>t+1..t+PredictionHorizon</c>, so with one row per
    /// candle consecutive labels share all but one bar. Setting this to
    /// <see cref="PredictionHorizon"/> makes the training labels fully non-overlapping. It costs
    /// sample count and buys independence; 1 preserves the original behaviour.
    /// </para>
    /// <para>
    /// Applies to training only. Validation and test are always scored on every row, because at
    /// inference the model genuinely is asked about every bar.
    /// </para>
    /// </summary>
    public int TrainingStride { get; init; } = 1;

    /// <summary>
    /// Normalise level-like features by ATR instead of by price.
    /// <para>
    /// Dividing by <c>close</c> removes the price <i>level</i> but not the volatility <i>regime</i>,
    /// which is why PROJECT_STATE §3.23 measured 22 of 44 features drifting significantly between
    /// train and test inside a single 41-day window — `range_pct` at PSI 6.13, `bb_width` at 5.24,
    /// with the MACD and EMA-difference columns close behind. All of them are price-normalised
    /// ratios whose scale still tracks volatility.
    /// </para>
    /// <para>
    /// ATR tracks the regime, so dividing by it removes both. Off by default so the change is a
    /// measurable A/B rather than a silent redefinition of every existing model's inputs.
    /// </para>
    /// </summary>
    public bool NormalizeByAtr { get; init; }

    /// <summary>
    /// Trend-state timeframes implied by the enabled flags, coarsest last. Derived rather than
    /// configured so the flags are the single source of truth — a separate list could disagree with
    /// them and silently emit columns for a timeframe nobody selected.
    /// </summary>
    public IReadOnlyList<int> TrendStateTimeframeMinutes =>
        [.. new (FeatureGroups Flag, int Minutes)[]
        {
            (FeatureGroups.TrendState30m, 30),
            (FeatureGroups.TrendState1h, 60),
            (FeatureGroups.TrendState2h, 120)
        }.Where(entry => EnabledGroups.HasFlag(entry.Flag)).Select(entry => entry.Minutes)];

    /// <summary>Section 20. Deliberately separate from the sell threshold so they can diverge.</summary>
    public double BuyProbabilityThreshold { get; init; } = 0.70;

    public double SellProbabilityThreshold { get; init; } = 0.70;

    public IReadOnlyList<int> EmaPeriods { get; init; } = [5, 10, 20, 50];
    public IReadOnlyList<int> RsiPeriods { get; init; } = [7, 14, 21];
    public IReadOnlyList<int> CciPeriods { get; init; } = [14, 20, 50];
    /// <summary>
    /// Raw <c>atr{n}_pct</c> = ATR(n)/Close level columns. **Empty by default, deliberately.**
    /// <para>
    /// The blueprint's section 8 lists these, but they are structurally non-transportable: ATR is
    /// Wilder-smoothed and therefore persistent, so a volatility regime shift moves it wholesale
    /// and it stays moved. Measured on real data (PROJECT_STATE.md section 3.12c), 48% of one test
    /// window sat above the *entire* training range of <c>atr14_pct</c>, against 4.6% for
    /// <c>bb_width</c> and 0% for <c>bb_position</c>. A gradient-boosted tree cannot extrapolate,
    /// so those bars all collapse into the topmost bin and the feature becomes a constant pinned at
    /// maximum - no information, and every prediction dragged toward the thin extreme tail of the
    /// training period. That group scored 0 of 4 positive walk-forward windows.
    /// </para>
    /// <para>
    /// <see cref="AtrPercentilePeriod"/>'s <c>atr_percentile_{n}</c> carries the same "how volatile
    /// is it" information in a bounded, transportable form and is the intended replacement. Set
    /// this back to <c>[14, 20]</c> only with a specific reason.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> AtrPeriods { get; init; } = [];

    /// <summary>
    /// The ATR the regime, change and percentile features are built from. Separate from
    /// <see cref="AtrPeriods"/>, which only controls which <c>atr{n}_pct</c> columns are emitted.
    /// </summary>
    public int AtrBasePeriod { get; init; } = 14;

    /// <summary>
    /// Denominator of <c>atr_regime</c> = ATR(base) / ATR(slow). A ratio above 1 says short-term
    /// volatility is running hotter than its own longer-run baseline; unlike a raw ATR this is
    /// scale-free and comparable across instruments.
    /// </summary>
    public int AtrRegimeSlowPeriod { get; init; } = 50;

    /// <summary>Lags for <c>atr_change_{n}</c> = ATR(base)[t] / ATR(base)[t-n] - 1.</summary>
    public IReadOnlyList<int> AtrChangeLags { get; init; } = [1, 5];

    /// <summary>Window for <c>atr_percentile_{n}</c>: where the current ATR sits in its own recent range.</summary>
    public int AtrPercentilePeriod { get; init; } = 100;
    public IReadOnlyList<int> ReturnPeriods { get; init; } = [1, 3, 5, 10, 20];
    public IReadOnlyList<int> RangePositionPeriods { get; init; } = [5, 10, 20, 50];

    public int MacdFastPeriod { get; init; } = 12;
    public int MacdSlowPeriod { get; init; } = 26;
    public int MacdSignalPeriod { get; init; } = 9;

    public int BollingerPeriod { get; init; } = 20;
    public decimal BollingerStandardDeviations { get; init; } = 2m;

    /// <summary>
    /// Which feature groups are calculated. Section 35 develops the model by switching these on
    /// one at a time, and section 24 ablates them one at a time; both use this single switch so an
    /// experiment cannot accidentally differ from an ablation in some other way.
    /// </summary>
    public FeatureGroups EnabledGroups { get; init; } = FeatureGroups.All;

    /// <summary>
    /// The label's ATR period. Kept separate from <see cref="AtrPeriods"/> because the label must
    /// not silently change when the feature set is ablated - section 24 removes ATR features while
    /// the target has to stay fixed, or the ablation compares two different problems.
    /// </summary>
    public int LabelAtrPeriod { get; init; } = 14;

    public void Validate()
    {
        if (PredictionHorizon <= 0)
            throw new ArgumentOutOfRangeException(nameof(PredictionHorizon), "Prediction horizon must be positive.");
        if (AtrTargetMultiplier <= 0m)
            throw new ArgumentOutOfRangeException(nameof(AtrTargetMultiplier), "ATR target multiplier must be positive.");
        if (BuyProbabilityThreshold is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(BuyProbabilityThreshold), "Buy threshold must be in (0, 1].");
        if (SellProbabilityThreshold is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(SellProbabilityThreshold), "Sell threshold must be in (0, 1].");
        if (TrainingStride <= 0)
            throw new ArgumentOutOfRangeException(nameof(TrainingStride), "Training stride must be at least 1.");
        if (LabelAtrPeriod <= 1)
            throw new ArgumentOutOfRangeException(nameof(LabelAtrPeriod), "Label ATR period must exceed 1.");
        if (MacdFastPeriod <= 0 || MacdSlowPeriod <= 0 || MacdSignalPeriod <= 0)
            throw new ArgumentException("MACD periods must be positive.");
        if (MacdFastPeriod >= MacdSlowPeriod)
            throw new ArgumentException("MACD fast period must be shorter than the slow period.");
        if (BollingerPeriod <= 1)
            throw new ArgumentOutOfRangeException(nameof(BollingerPeriod), "Bollinger period must exceed 1.");
        if (BollingerStandardDeviations <= 0m)
            throw new ArgumentOutOfRangeException(nameof(BollingerStandardDeviations));
        Positive(EmaPeriods, nameof(EmaPeriods));
        Positive(RsiPeriods, nameof(RsiPeriods));
        Positive(CciPeriods, nameof(CciPeriods));
        // AtrPeriods may be empty: that yields an ATR group carrying only regime/change/percentile
        // context, with no raw atr{n}_pct level column. Worth being able to express, because the
        // level is the part that behaved worst (PROJECT_STATE.md section 3.12a).
        PositiveAllowingEmpty(AtrPeriods, nameof(AtrPeriods));
        Positive(AtrChangeLags, nameof(AtrChangeLags));
        if (AtrBasePeriod <= 1)
            throw new ArgumentOutOfRangeException(nameof(AtrBasePeriod), "ATR base period must exceed 1.");
        if (AtrRegimeSlowPeriod <= AtrBasePeriod)
        {
            throw new ArgumentException(
                $"AtrRegimeSlowPeriod ({AtrRegimeSlowPeriod}) must exceed AtrBasePeriod " +
                $"({AtrBasePeriod}): atr_regime is short-run volatility over its own longer-run baseline.",
                nameof(AtrRegimeSlowPeriod));
        }
        if (AtrPercentilePeriod <= 1)
            throw new ArgumentOutOfRangeException(nameof(AtrPercentilePeriod), "ATR percentile window must exceed 1.");
        Positive(ReturnPeriods, nameof(ReturnPeriods));
        Positive(RangePositionPeriods, nameof(RangePositionPeriods));

        // The feature engine indexes EMA pairs by period, so the specific periods the blueprint's
        // section 8 pairs up (5v20, 10v20, 20v50) have to actually exist when trend is enabled.
        if (EnabledGroups.HasFlag(FeatureGroups.Trend))
        {
            foreach (int required in (int[])[5, 10, 20, 50])
            {
                if (!EmaPeriods.Contains(required))
                {
                    throw new ArgumentException(
                        $"EmaPeriods must contain {required} while the Trend group is enabled: the " +
                        "section 8 feature set pairs EMA 5/20, 10/20 and 20/50.",
                        nameof(EmaPeriods));
                }
            }
        }
        if (EnabledGroups.HasFlag(FeatureGroups.Rsi) && !RsiPeriods.Contains(14))
            throw new ArgumentException("RsiPeriods must contain 14: rsi14_change_1/5 are part of the section 8 set.", nameof(RsiPeriods));

        if (EnabledGroups.HasFlag(FeatureGroups.Cci) && !CciPeriods.Contains(20))
            throw new ArgumentException("CciPeriods must contain 20: cci20_change_1/5 are part of the section 8 set.", nameof(CciPeriods));
        if (EnabledGroups == FeatureGroups.None)
            throw new ArgumentException("At least one feature group must be enabled.", nameof(EnabledGroups));

        static void PositiveAllowingEmpty(IReadOnlyList<int> periods, string name)
        {
            if (periods.Any(period => period <= 0))
                throw new ArgumentException($"{name} must contain only positive periods.", name);
            if (periods.Distinct().Count() != periods.Count)
                throw new ArgumentException($"{name} must not contain duplicates.", name);
        }

        static void Positive(IReadOnlyList<int> periods, string name)
        {
            if (periods.Count == 0)
                throw new ArgumentException($"{name} must not be empty.", name);
            if (periods.Any(period => period <= 0))
                throw new ArgumentException($"{name} must contain only positive periods.", name);
            if (periods.Distinct().Count() != periods.Count)
                throw new ArgumentException($"{name} must not contain duplicates.", name);
        }
    }
}

/// <summary>
/// Section 35's experiment ladder and section 24's ablation both operate on these groups.
/// </summary>
[Flags]
public enum FeatureGroups
{
    None = 0,
    /// <summary>Section 4 returns plus section 5 candle structure - the experiment 1 baseline.</summary>
    PriceAction = 1 << 0,
    /// <summary>Section 6 highest-high/lowest-low range position.</summary>
    RangePosition = 1 << 1,
    Trend = 1 << 2,
    Rsi = 1 << 3,
    Cci = 1 << 4,
    Atr = 1 << 5,
    Macd = 1 << 6,
    Bollinger = 1 << 7,
    /// <summary>
    /// Derived analysis already computed by <c>ChartAnnotationEngine</c>: RSI/CCI zones, momentum
    /// and divergence relationships, and the Bollinger width regime. Requires the dataset to be
    /// built with annotation snapshots - see <c>AnnotationDatasetBuilder</c>.
    /// </summary>
    Analysis = 1 << 8,

    /// <summary>
    /// Analyses the annotation engine already computes but the classifier never consumed. Each is
    /// a separate flag so the §3.12-style ladder and ablation can price it individually - which
    /// matters, because §3.12 and §3.13 found opposite feature rankings for the standalone and
    /// filtering tasks. Which groups help is task-dependent and must be measured, not assumed.
    /// </summary>
    Adx = 1 << 9,
    StochRsi = 1 << 10,
    Donchian = 1 << 11,
    Efficiency = 1 << 12,

    /// <summary>
    /// Tick/traded volume behaviour. Present in simulation replay data but NOT in the fetched OANDA
    /// candle files, which carry only OHLC - see <c>SnapshotFeatures</c>. Enabling this on a source
    /// without volume yields constant columns rather than an error, so check the data first.
    /// </summary>
    Volume = 1 << 13,

    /// <summary>Groups requiring the FULL AnalysisSnapshot, not just its indicators.</summary>
    Structure = 1 << 14,
    SupportResistance = 1 << 15,
    SupplyDemand = 1 << 16,
    Liquidity = 1 << 17,
    Regime = 1 << 18,

    /// <summary>
    /// Higher-timeframe TrendStatistics state joined onto each row — the ML catalogue's Phase 4
    /// cross-timeframe item. Requires the builder to replay the detector causally; see
    /// <see cref="TradingClassifier.Features.TrendStateFeatures"/>.
    /// </summary>
    /// <summary>
    /// Higher-timeframe trend state, ONE FLAG PER TIMEFRAME so the ladder, ablation and greedy
    /// selection can price each independently. Bundling them under a single flag would make the
    /// cascade all-or-nothing and hide which timeframe (if any) carries the information — the whole
    /// reason for offering several is that §2.18 showed the hand-made choice is easy to get wrong.
    /// </summary>
    TrendState30m = 1 << 19,
    TrendState1h = 1 << 20,
    TrendState2h = 1 << 21,

    /// <summary>Experiment 1: OHLC-derived features only.</summary>
    Experiment1 = PriceAction | RangePosition,
    /// <summary>Experiment 2 adds EMA.</summary>
    Experiment2 = Experiment1 | Trend,
    /// <summary>Experiment 3 adds RSI and CCI.</summary>
    Experiment3 = Experiment2 | Rsi | Cci,
    /// <summary>Experiment 4 adds ATR.</summary>
    Experiment4 = Experiment3 | Atr,
    /// <summary>Experiment 5 adds MACD and Bollinger Bands - the full section 8 set.</summary>
    Experiment5 = Experiment4 | Macd | Bollinger,
    /// <summary>Everything the annotation engine exposes through indicators, for the extended ladder.</summary>
    ExtendedIndicators = Adx | StochRsi | Donchian | Efficiency | Volume,
    /// <summary>Groups sourced from the full analysis snapshot.</summary>
    SnapshotGroups = Structure | SupportResistance | SupplyDemand | Liquidity | Regime,
    /// <summary>
    /// Experiment 6 goes beyond the blueprint: the repo's own derived analysis. Still inside
    /// section 29's allowed set - these are computed from RSI, CCI and Bollinger, not from volume,
    /// order book, liquidity or news.
    /// </summary>
    Experiment6 = Experiment5 | Analysis,

    /// <summary>
    /// The complete blueprint section 8 feature set, and the default.
    /// <para>
    /// Deliberately excludes <see cref="Analysis"/>: those columns can only be produced by running
    /// the annotation engine, so including them here would make the cheap
    /// <c>DatasetBuilder</c> path throw on default options. Opt in with
    /// <see cref="Experiment6"/>.
    /// </para>
    /// </summary>
    All = Experiment5,

    /// <summary>
    /// Every group that exists, including the annotation-backed ones. NOT a valid default: it forces
    /// the <c>AnnotationDatasetBuilder</c> path. Its purpose is superset construction — build one
    /// dataset with everything, then project each experiment rung out of it with
    /// <c>FeatureSchema.Restrict</c>.
    /// </summary>
    /// <summary>All trend-state timeframes together — a convenience, not a selectable unit.</summary>
    TrendState = TrendState30m | TrendState1h | TrendState2h,

    Everything = Experiment5 | Analysis | ExtendedIndicators | SnapshotGroups | TrendState
}
