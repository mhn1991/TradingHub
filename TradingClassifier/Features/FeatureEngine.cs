using ChartAnnotator.Collections;
using TradingClassifier.Configuration;
using TradingClassifier.Indicators;

namespace TradingClassifier.Features;

/// <summary>Section 30's <c>IFeatureCalculator</c>.</summary>
public interface IFeatureCalculator
{
    FeatureSchema Schema { get; }

    /// <summary>
    /// Folds one candle in and returns its feature row, or null while any enabled indicator is
    /// still warming up (section 26, "remove warm-up rows").
    /// </summary>
    FeatureVector? Update(ClassifierCandle candle);

    /// <summary>
    /// As the single-argument overload, but supplying the annotation engine's snapshot
    /// for this candle. Required when <see cref="FeatureGroups.Analysis"/> is enabled; ignored
    /// otherwise.
    /// </summary>
    FeatureVector? Update(ClassifierCandle candle, ChartAnnotator.Models.IndicatorSnapshot? analysis);

    /// <summary>Full-snapshot overload: required by the Structure/SupportResistance/
    /// SupplyDemand/Liquidity/Regime groups, which read beyond IndicatorSnapshot.</summary>
    FeatureVector? Update(ClassifierCandle candle, ChartAnnotator.Models.AnalysisSnapshot? snapshot);
}

/// <summary>
/// Sections 4 to 8, as one incremental pass over candles.
/// <para>
/// This is the single shared implementation section 27 demands. The training pipeline folds it
/// over history and the live agent folds it over arriving candles; there is no second copy of the
/// indicator maths to drift.
/// </para>
/// <para>
/// Look-ahead safety (section 25) is structural rather than checked: <c>Update</c> only
/// ever sees candle <c>t</c> and state accumulated from <c>&lt;= t</c>. Future bars reach the
/// label generator alone, which is a separate type operating on a different input.
/// </para>
/// </summary>
public sealed class FeatureEngine : IFeatureCalculator
{
    private readonly ClassifierOptions _options;

    // Set only for the duration of the full-snapshot Update overload; the snapshot groups read it
    // during Write. Null on the indicator-only path, which is why RequiresFullSnapshot guards it.
    private ChartAnnotator.Models.AnalysisSnapshot? _snapshot;

    // Higher-timeframe state for the TrendState group, set only for the duration of the overload
    // that supplies it. The caller owns causality: it must come from an incremental replay over
    // CLOSED higher-timeframe candles, never a backfill.
    private IReadOnlyList<TrendStatistics.Detection.TrendState?>? _trendStates;
    private readonly RingBuffer<decimal> _closes;

    private readonly Dictionary<int, EmaState> _emas = [];
    private readonly Dictionary<int, RsiState> _rsis = [];
    private readonly Dictionary<int, CciState> _ccis = [];
    private readonly Dictionary<int, AtrState> _atrs = [];
    private readonly Dictionary<int, RollingExtremeState> _ranges = [];

    private readonly AtrState? _atrRegimeSlow;
    private readonly RollingPercentileState? _atrPercentile;
    private readonly Dictionary<int, LaggedValue> _atrLags = [];

    private readonly MacdState? _macd;
    private readonly BollingerBandState? _bollinger;
    private readonly AtrState _labelAtr;

    private readonly LaggedValue? _ema20Lag1;
    private readonly LaggedValue? _ema20Lag5;
    private readonly LaggedValue? _ema50Lag5;
    private readonly LaggedValue? _rsi14Lag1;
    private readonly LaggedValue? _rsi14Lag5;
    private readonly LaggedValue? _cci20Lag1;
    private readonly LaggedValue? _cci20Lag5;
    private readonly LaggedValue? _macdHistogramLag1;

    private DateTimeOffset? _previousTimestamp;

    public FeatureEngine(ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        Schema = FeatureSchema.Create(options);

        // The longest lookback any return needs; closes older than that are never read.
        int maxReturn = options.ReturnPeriods.Max();
        _closes = new RingBuffer<decimal>(maxReturn + 1);

        _labelAtr = new AtrState(options.LabelAtrPeriod);

        if (options.EnabledGroups.HasFlag(FeatureGroups.Trend))
        {
            foreach (int period in options.EmaPeriods)
                _emas[period] = new EmaState(period);
            _ema20Lag1 = new LaggedValue(1);
            _ema20Lag5 = new LaggedValue(5);
            _ema50Lag5 = new LaggedValue(5);
        }

        if (options.EnabledGroups.HasFlag(FeatureGroups.Rsi))
        {
            foreach (int period in options.RsiPeriods)
                _rsis[period] = new RsiState(period);
            _rsi14Lag1 = new LaggedValue(1);
            _rsi14Lag5 = new LaggedValue(5);
        }

        if (options.EnabledGroups.HasFlag(FeatureGroups.Cci))
        {
            foreach (int period in options.CciPeriods)
                _ccis[period] = new CciState(period);
            _cci20Lag1 = new LaggedValue(1);
            _cci20Lag5 = new LaggedValue(5);
        }

        if (options.EnabledGroups.HasFlag(FeatureGroups.Atr))
        {
            foreach (int period in options.AtrPeriods)
                _atrs[period] = new AtrState(period);
            // The base ATR drives regime/change/percentile and must exist even when AtrPeriods is
            // empty, i.e. when no raw level column is emitted at all.
            _atrs.TryAdd(options.AtrBasePeriod, new AtrState(options.AtrBasePeriod));
            // The slow ATR is the regime denominator only - it is never emitted as its own column.
            _atrRegimeSlow = new AtrState(options.AtrRegimeSlowPeriod);
            _atrPercentile = new RollingPercentileState(options.AtrPercentilePeriod);
            foreach (int lag in options.AtrChangeLags)
                _atrLags[lag] = new LaggedValue(lag);
        }

        if (options.EnabledGroups.HasFlag(FeatureGroups.RangePosition))
        {
            foreach (int period in options.RangePositionPeriods)
                _ranges[period] = new RollingExtremeState(period);
        }

        if (options.EnabledGroups.HasFlag(FeatureGroups.Macd))
        {
            _macd = new MacdState(options.MacdFastPeriod, options.MacdSlowPeriod, options.MacdSignalPeriod);
            _macdHistogramLag1 = new LaggedValue(1);
        }

        if (options.EnabledGroups.HasFlag(FeatureGroups.Bollinger))
            _bollinger = new BollingerBandState(options.BollingerPeriod, options.BollingerStandardDeviations);
    }

    public FeatureSchema Schema { get; }

    /// <summary>
    /// Groups sourced from the annotation snapshot rather than recomputed indicator maths. Any of
    /// them forces the AnnotationDatasetBuilder path; a null snapshot would otherwise silently emit
    /// zeros for every column and the ablation would price the group at exactly nothing.
    /// </summary>
    public static bool RequiresAnnotation(FeatureGroups groups) =>
        groups.HasFlag(FeatureGroups.Analysis) ||
        groups.HasFlag(FeatureGroups.Adx) ||
        groups.HasFlag(FeatureGroups.StochRsi) ||
        groups.HasFlag(FeatureGroups.Donchian) ||
        groups.HasFlag(FeatureGroups.Efficiency) ||
        groups.HasFlag(FeatureGroups.Volume) ||
        RequiresFullSnapshot(groups);

    /// <summary>Groups that need more than IndicatorSnapshot can supply.</summary>
    public static bool RequiresFullSnapshot(FeatureGroups groups) =>
        groups.HasFlag(FeatureGroups.Structure) ||
        groups.HasFlag(FeatureGroups.SupportResistance) ||
        groups.HasFlag(FeatureGroups.SupplyDemand) ||
        groups.HasFlag(FeatureGroups.Liquidity) ||
        groups.HasFlag(FeatureGroups.Regime);

    public FeatureVector? Update(ClassifierCandle candle) =>
        Update(candle, (ChartAnnotator.Models.IndicatorSnapshot?)null);

    public FeatureVector? Update(ClassifierCandle candle, ChartAnnotator.Models.AnalysisSnapshot? snapshot) =>
        Update(candle, snapshot, (IReadOnlyList<TrendStatistics.Detection.TrendState?>?)null);

    /// <summary>
    /// Full overload: annotation snapshot plus the higher-timeframe trend state for
    /// <see cref="FeatureGroups.TrendState"/>.
    /// </summary>
    public FeatureVector? Update(
        ClassifierCandle candle,
        ChartAnnotator.Models.AnalysisSnapshot? snapshot,
        TrendStatistics.Detection.TrendState? trendState) =>
        Update(candle, snapshot, trendState is null ? null : [trendState]);

    /// <summary>
    /// Multi-timeframe overload: one trend state per entry in
    /// <c>ClassifierOptions.TrendStateTimeframeMinutes</c>, in the same order. A short list is
    /// padded with neutral states rather than throwing, so a caller that can only supply some
    /// timeframes still produces a schema-correct vector.
    /// </summary>
    public FeatureVector? Update(
        ClassifierCandle candle,
        ChartAnnotator.Models.AnalysisSnapshot? snapshot,
        IReadOnlyList<TrendStatistics.Detection.TrendState?>? trendStates)
    {
        _snapshot = snapshot;
        _trendStates = trendStates;
        try
        {
            return Update(candle, snapshot?.Indicators);
        }
        finally
        {
            _snapshot = null;
            _trendStates = null;
        }
    }

    /// <summary>Groups needing higher-timeframe state the annotation snapshot cannot supply.</summary>
    public static bool RequiresTrendState(FeatureGroups groups) =>
        groups.HasFlag(FeatureGroups.TrendState30m) ||
        groups.HasFlag(FeatureGroups.TrendState1h) ||
        groups.HasFlag(FeatureGroups.TrendState2h);

    public FeatureVector? Update(ClassifierCandle candle, ChartAnnotator.Models.IndicatorSnapshot? analysis)
    {
        if (RequiresAnnotation(_options.EnabledGroups) && analysis is null)
        {
            throw new ArgumentNullException(
                nameof(analysis),
                "An annotation-backed feature group (Analysis/Adx/StochRsi/Donchian/Efficiency) " +
                "is enabled, so every candle needs its annotation snapshot. Build the dataset with " +
                "AnnotationDatasetBuilder, not DatasetBuilder.");
        }

        // Section 26 sorts chronologically before anything else. Enforce it here rather than
        // trusting callers: an out-of-order candle silently corrupts every incremental state, and
        // the resulting model would be quietly trained on nonsense.
        if (_previousTimestamp is DateTimeOffset previous && candle.Timestamp <= previous)
        {
            throw new ArgumentException(
                $"Candles must arrive in strictly increasing time order; got {candle.Timestamp:O} after {previous:O}.",
                nameof(candle));
        }
        _previousTimestamp = candle.Timestamp;

        if (candle.High < candle.Low)
            throw new ArgumentException($"Candle at {candle.Timestamp:O} has High below Low.", nameof(candle));
        if (candle.Close <= 0m)
            throw new ArgumentException($"Candle at {candle.Timestamp:O} has a non-positive close.", nameof(candle));

        _labelAtr.Update(candle.High, candle.Low, candle.Close);
        foreach (EmaState ema in _emas.Values) ema.Update(candle.Close);
        foreach (RsiState rsi in _rsis.Values) rsi.Update(candle.Close);
        foreach (CciState cci in _ccis.Values) cci.Update(candle.High, candle.Low, candle.Close);
        foreach (AtrState atr in _atrs.Values) atr.Update(candle.High, candle.Low, candle.Close);
        _atrRegimeSlow?.Update(candle.High, candle.Low, candle.Close);
        foreach (RollingExtremeState range in _ranges.Values) range.Update(candle.High, candle.Low);
        _macd?.Update(candle.Close);
        _bollinger?.Update(candle.Close);

        // The lagged trackers are updated after the current values are computed but before the row
        // is emitted, so `Previous` is genuinely the value from N bars ago, not this bar's.
        bool laggedReady = UpdateLags();

        _closes.Add(candle.Close, out _);

        if (!IsReady() || !laggedReady)
            return null;

        float[] values = new float[Schema.Count];
        int cursor = 0;
        Write(values, ref cursor, candle);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Analysis))
            AnalysisFeatures.Write(values, ref cursor, analysis!);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Adx))
            ExtendedIndicatorFeatures.WriteAdx(values, ref cursor, analysis);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.StochRsi))
            ExtendedIndicatorFeatures.WriteStochRsi(values, ref cursor, analysis);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Donchian))
            ExtendedIndicatorFeatures.WriteDonchian(values, ref cursor, analysis, candle.Close);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Efficiency))
            ExtendedIndicatorFeatures.WriteEfficiency(values, ref cursor, analysis);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Volume))
            SnapshotFeatures.WriteVolume(values, ref cursor, analysis);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Structure))
            SnapshotFeatures.WriteStructure(values, ref cursor, _snapshot, candle.Close, _labelAtr.Current);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.SupportResistance))
            SnapshotFeatures.WriteSupportResistance(values, ref cursor, _snapshot, candle.Close, _labelAtr.Current);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.SupplyDemand))
            SnapshotFeatures.WriteSupplyDemand(values, ref cursor, _snapshot);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Liquidity))
            SnapshotFeatures.WriteLiquidity(values, ref cursor, _snapshot);
        if (_options.EnabledGroups.HasFlag(FeatureGroups.Regime))
            SnapshotFeatures.WriteRegime(values, ref cursor, _snapshot);
        if (RequiresTrendState(_options.EnabledGroups))
        {
            for (int index = 0; index < _options.TrendStateTimeframeMinutes.Count; index++)
            {
                TrendStatistics.Detection.TrendState? state =
                    _trendStates is not null && index < _trendStates.Count ? _trendStates[index] : null;
                TrendStateFeatures.Write(values, ref cursor, state, candle.Close, _labelAtr.Current);
            }
        }

        if (cursor != Schema.Count)
        {
            throw new InvalidOperationException(
                $"Feature engine wrote {cursor} values but the schema declares {Schema.Count}. " +
                "FeatureSchema.Create and FeatureEngine.Write have diverged.");
        }

        return new FeatureVector
        {
            Timestamp = candle.Timestamp,
            Close = candle.Close,
            LabelAtr = _labelAtr.Current,
            Values = values
        };
    }

    private bool UpdateLags()
    {
        bool ready = true;

        if (_emas.Count > 0)
        {
            if (_emas[20].IsReady)
            {
                _ema20Lag1!.Update(_emas[20].Current);
                _ema20Lag5!.Update(_emas[20].Current);
            }
            if (_emas[50].IsReady)
                _ema50Lag5!.Update(_emas[50].Current);
            ready &= _ema20Lag1!.IsReady && _ema20Lag5!.IsReady && _ema50Lag5!.IsReady;
        }

        if (_rsis.Count > 0)
        {
            if (_rsis[14].IsReady)
            {
                _rsi14Lag1!.Update(_rsis[14].Current);
                _rsi14Lag5!.Update(_rsis[14].Current);
            }
            ready &= _rsi14Lag1!.IsReady && _rsi14Lag5!.IsReady;
        }

        if (_ccis.Count > 0)
        {
            if (_ccis[20].IsReady)
            {
                _cci20Lag1!.Update(_ccis[20].Current);
                _cci20Lag5!.Update(_ccis[20].Current);
            }
            ready &= _cci20Lag1!.IsReady && _cci20Lag5!.IsReady;
        }

        if (_atrLags.Count > 0)
        {
            AtrState baseAtr = _atrs[_options.AtrBasePeriod];
            if (baseAtr.IsReady)
            {
                foreach (LaggedValue lagged in _atrLags.Values)
                    lagged.Update(baseAtr.Current);
                // Percentile is fed the same base ATR, so it only starts once that ATR exists -
                // otherwise the window would be polluted with warm-up zeros and every early
                // reading would rank at the top.
                _atrPercentile!.Update(baseAtr.Current);
            }
            ready &= _atrLags.Values.All(lagged => lagged.IsReady) && _atrPercentile!.IsReady;
        }

        if (_macd is not null)
        {
            if (_macd.IsReady)
                _macdHistogramLag1!.Update(_macd.Histogram);
            ready &= _macdHistogramLag1!.IsReady;
        }

        return ready;
    }

    private bool IsReady()
    {
        if (!_labelAtr.IsReady) return false;
        if (_closes.Count < _options.ReturnPeriods.Max()) return false;
        if (_emas.Values.Any(ema => !ema.IsReady)) return false;
        if (_rsis.Values.Any(rsi => !rsi.IsReady)) return false;
        if (_ccis.Values.Any(cci => !cci.IsReady)) return false;
        if (_atrs.Values.Any(atr => !atr.IsReady)) return false;
        if (_atrRegimeSlow is not null && !_atrRegimeSlow.IsReady) return false;
        if (_ranges.Values.Any(range => !range.IsReady)) return false;
        if (_macd is not null && !_macd.IsReady) return false;
        if (_bollinger is not null && !_bollinger.IsReady) return false;
        return true;
    }

    private void Write(float[] values, ref int cursor, ClassifierCandle candle)
    {
        decimal close = candle.Close;
        FeatureGroups groups = _options.EnabledGroups;

        if (groups.HasFlag(FeatureGroups.PriceAction))
        {
            foreach (int period in _options.ReturnPeriods)
            {
                // _closes already holds this candle's close at the newest slot, so the close
                // `period` bars back is at offset `period` from the end.
                decimal past = _closes[^(period + 1)];
                // A return over price is still a volatility-scaled quantity: a 0.5% move means
                // something different in a quiet regime than a violent one, which is why the
                // return_N columns were among the worst remaining drifters (§3.23). Over ATR the
                // question becomes "how many normal candles of movement was that", which transports.
                values[cursor++] = Ratio(close - past, Scale(past));
            }

            decimal range = candle.Range;
            values[cursor++] = Ratio(candle.Body, range);
            values[cursor++] = Ratio(candle.UpperWick, range);
            values[cursor++] = Ratio(candle.LowerWick, range);
            // range_pct was the WORST drifter measured (PSI 6.13, §3.23). Range over price still
            // scales with volatility; range over ATR is the same quantity with the regime divided
            // out — literally "how big is this candle relative to what is normal right now".
            values[cursor++] = Ratio(range, Scale(close));                // range_pct
            values[cursor++] = candle.Close > candle.Open ? 1f : candle.Close < candle.Open ? -1f : 0f;
        }

        if (groups.HasFlag(FeatureGroups.RangePosition))
        {
            foreach (int period in _options.RangePositionPeriods)
            {
                RollingExtremeState range = _ranges[period];
                values[cursor++] = Ratio(close - range.LowestLow, range.HighestHigh - range.LowestLow);
            }
        }

        if (groups.HasFlag(FeatureGroups.Trend))
        {
            // Distances from / between EMAs are level-like: normalise by the regime, not the price.
            decimal trendScale = Scale(close);
            foreach (int period in _options.EmaPeriods)
                values[cursor++] = Ratio(close - _emas[period].Current, trendScale);

            values[cursor++] = Ratio(_emas[5].Current - _emas[20].Current, trendScale);
            values[cursor++] = Ratio(_emas[10].Current - _emas[20].Current, trendScale);
            values[cursor++] = Ratio(_emas[20].Current - _emas[50].Current, trendScale);
            // Slopes are displacements per bar — same class as returns, same fix.
            values[cursor++] = Ratio(_emas[20].Current - _ema20Lag1!.Previous, Scale(_ema20Lag1.Previous));
            values[cursor++] = Ratio(_emas[20].Current - _ema20Lag5!.Previous, Scale(_ema20Lag5.Previous));
            values[cursor++] = Ratio(_emas[50].Current - _ema50Lag5!.Previous, Scale(_ema50Lag5.Previous));
        }

        if (groups.HasFlag(FeatureGroups.Rsi))
        {
            foreach (int period in _options.RsiPeriods)
                values[cursor++] = (float)_rsis[period].Current;
            // Already bounded 0-100, so the change is reported in points rather than as a ratio.
            values[cursor++] = (float)(_rsis[14].Current - _rsi14Lag1!.Previous);
            values[cursor++] = (float)(_rsis[14].Current - _rsi14Lag5!.Previous);
        }

        if (groups.HasFlag(FeatureGroups.Cci))
        {
            foreach (int period in _options.CciPeriods)
                values[cursor++] = (float)_ccis[period].Current;
            values[cursor++] = (float)(_ccis[20].Current - _cci20Lag1!.Previous);
            values[cursor++] = (float)(_ccis[20].Current - _cci20Lag5!.Previous);
        }

        if (groups.HasFlag(FeatureGroups.Atr))
        {
            foreach (int period in _options.AtrPeriods)
                values[cursor++] = Ratio(_atrs[period].Current, close);   // atr_pct: volatility level

            decimal baseAtr = _atrs[_options.AtrBasePeriod].Current;

            // atr_regime: short-run volatility relative to its own longer-run baseline. Above 1 is
            // an expanding regime, below 1 a contracting one - the same number on any instrument.
            values[cursor++] = Ratio(baseAtr, _atrRegimeSlow!.Current);

            // atr_change_n: direction of travel. A given ATR means something different when
            // volatility is rising into it than when it is decaying away from it.
            foreach (int lag in _options.AtrChangeLags)
            {
                decimal previous = _atrLags[lag].Previous;
                values[cursor++] = previous == 0m ? 0f : Ratio(baseAtr - previous, previous);
            }

            // atr_percentile: where this sits in its own recent range, already in [0, 1].
            values[cursor++] = (float)_atrPercentile!.Current;
        }

        if (groups.HasFlag(FeatureGroups.Macd))
        {
            // Section 8 names these plainly, but a raw MACD is in price units and section 17 rules
            // that out ("EMA20 = 1.17434" is given as the undesirable case). Dividing by close keeps
            // the section 8 names while making the columns comparable across instruments and eras.
            // MACD is a price-scale oscillator: on gold it means something different at 1,800 than
            // at 4,400, and price-normalising only fixes half of that.
            decimal macdScale = Scale(close);
            values[cursor++] = Ratio(_macd!.Macd, macdScale);
            values[cursor++] = Ratio(_macd.Signal, macdScale);
            values[cursor++] = Ratio(_macd.Histogram, macdScale);
            values[cursor++] = Ratio(_macd.Histogram - _macdHistogramLag1!.Previous, macdScale);
        }

        if (groups.HasFlag(FeatureGroups.Bollinger))
        {
            decimal bandwidth = _bollinger!.Upper - _bollinger.Lower;
            values[cursor++] = Ratio(close - _bollinger.Lower, bandwidth);
            // bb_width was the second-worst drifter (PSI 5.24): band width over the middle band is a
            // volatility level wearing a ratio's clothing.
            values[cursor++] = Ratio(bandwidth, Scale(_bollinger.Middle));
        }
    }

    /// <summary>
    /// Guards the degenerate denominators that genuinely occur - a doji has zero range, a flat
    /// window has zero band width. Returning 0 keeps the row usable; propagating NaN or Infinity
    /// would poison training, and ML.NET does not reject them loudly.
    /// </summary>
    /// <summary>
    /// Denominator for level-like features: ATR when regime normalisation is on, otherwise price.
    /// Falls back to price whenever ATR is not yet warm, so warm-up rows do not divide by zero.
    /// </summary>
    private decimal Scale(decimal close) =>
        _options.NormalizeByAtr && _labelAtr.Current > 0m ? _labelAtr.Current : close;

    private static float Ratio(decimal numerator, decimal denominator)
    {
        if (denominator == 0m)
            return 0f;
        float result = (float)(numerator / denominator);
        return float.IsFinite(result) ? result : 0f;
    }
}
