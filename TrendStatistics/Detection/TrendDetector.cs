using TrendStatistics.Data;
using TrendStatistics.Segmentation;

namespace TrendStatistics.Detection;

/// <summary>
/// Streaming V0 trend detector. Every decision is made after a candle closes and uses
/// only that candle plus state accumulated from earlier calls.
/// </summary>
public sealed class TrendDetector : ITrendDetector
{
    private readonly TrendDetectorConfig _config;
    private readonly List<IndexedCandle> _recentCandles = [];
    private readonly List<decimal> _emaValues = [];
    private readonly AtrState _atr;
    private readonly decimal _emaAlpha;

    private ActiveTrend? _active;
    private string? _symbol;
    private DateTimeOffset? _lastOpenTime;
    private decimal? _ema;
    private int _barIndex = -1;
    private int _earliestStructuralStartIndex;

    public TrendDetector(TrendDetectorConfig? config = null)
    {
        _config = config ?? new TrendDetectorConfig();
        _config.Validate();
        _atr = new AtrState(_config.AtrPeriod);
        _emaAlpha = 2m / (_config.EmaPeriod + 1m);
    }

    public TrendState CurrentState { get; private set; } = TrendState.Neutral;

    public TrendDetectorUpdate Apply(Candle candle)
    {
        ValidateCandle(candle);
        _barIndex++;

        decimal? laggedAtr = _atr.Value;
        decimal? updatedAtr = _atr.Update(candle);
        decimal? atr = _config.UseLaggedAtrForDecisions
            ? laggedAtr ?? updatedAtr
            : updatedAtr;
        _ema = _ema is null
            ? candle.Close
            : ((candle.Close - _ema.Value) * _emaAlpha) + _ema.Value;
        _emaValues.Add(_ema.Value);
        TrimEmaValues();

        TrendRecord? completed = null;
        if (_active is null)
        {
            TryStartCandidate(candle, atr);
        }
        else if (_active.ConfirmationIndex is null)
        {
            UpdateCandidate(candle, atr);
        }
        else
        {
            completed = UpdateConfirmedTrend(candle, atr);
        }

        AddRecentCandle(candle);
        _lastOpenTime = candle.OpenTime;
        CurrentState = BuildCurrentState(candle);
        return new TrendDetectorUpdate(CurrentState, completed);
    }

    private void TryStartCandidate(Candle candle, decimal? atr)
    {
        if ((_barIndex + 1) < _config.WarmupBars || atr is null || atr <= 0m || !HasEmaSlope)
        {
            return;
        }

        IndexedCandle? recentLow = FindRecentExtreme(findLow: true);
        IndexedCandle? recentHigh = FindRecentExtreme(findLow: false);
        if (recentLow is null || recentHigh is null)
        {
            return;
        }

        decimal bullDisplacementAtr = (candle.Close - recentLow.Value.Candle.Low) / atr.Value;
        decimal bearDisplacementAtr = (recentHigh.Value.Candle.High - candle.Close) / atr.Value;
        bool bullCandidate = EmaSlope > 0m
            && candle.Close > _ema
            && bullDisplacementAtr >= _config.CandidateMinDisplacementAtr;
        bool bearCandidate = EmaSlope < 0m
            && candle.Close < _ema
            && bearDisplacementAtr >= _config.CandidateMinDisplacementAtr;

        if (!bullCandidate && !bearCandidate)
        {
            return;
        }

        if (bullCandidate && (!bearCandidate || bullDisplacementAtr >= bearDisplacementAtr))
        {
            _active = ActiveTrend.Start(
                TrendDirection.Bullish,
                recentLow.Value.Candle.OpenTime,
                recentLow.Value.Candle.Low,
                recentLow.Value.Index,
                _barIndex);
        }
        else
        {
            _active = ActiveTrend.Start(
                TrendDirection.Bearish,
                recentHigh.Value.Candle.OpenTime,
                recentHigh.Value.Candle.High,
                recentHigh.Value.Index,
                _barIndex);
        }
    }

    private void UpdateCandidate(Candle candle, decimal? atr)
    {
        ActiveTrend active = _active!;
        bool invalidated = active.Direction == TrendDirection.Bullish
            ? candle.Low < active.StructuralStartPrice
            : candle.High > active.StructuralStartPrice;
        bool timedOut = (_barIndex - active.CandidateDetectedIndex) >= _config.CandidateTimeoutBars;
        bool indicatorsReversed = active.Direction == TrendDirection.Bullish
            ? EmaSlope <= 0m && candle.Close <= _ema
            : EmaSlope >= 0m && candle.Close >= _ema;

        if (invalidated || timedOut || indicatorsReversed)
        {
            _active = null;
            return;
        }

        if (atr is null || atr <= 0m || !HasEmaSlope)
        {
            return;
        }

        decimal displacementAtr = active.Direction == TrendDirection.Bullish
            ? (candle.Close - active.StructuralStartPrice) / atr.Value
            : (active.StructuralStartPrice - candle.Close) / atr.Value;
        bool slopeSupportsTrend = active.Direction == TrendDirection.Bullish
            ? EmaSlope > 0m
            : EmaSlope < 0m;
        bool structureBreak = BreaksPriorStructure(candle, active.Direction);

        if (displacementAtr < _config.ConfirmationMinDisplacementAtr
            || !slopeSupportsTrend
            || !structureBreak)
        {
            return;
        }

        active.ConfirmationIndex = _barIndex;
        active.ConfirmationTime = CandleCloseTime(candle);
        active.ConfirmationPrice = candle.Close;
        active.AtrAtConfirmation = atr.Value;
        active.FavorableExtremePrice = active.Direction == TrendDirection.Bullish
            ? candle.High
            : candle.Low;
        active.FavorableExtremeTime = CandleCloseTime(candle);
        active.FavorableExtremeIndex = _barIndex;
    }

    private TrendRecord? UpdateConfirmedTrend(Candle candle, decimal? atr)
    {
        ActiveTrend active = _active!;
        UpdateFavorableExtreme(active, candle);
        UpdateMaximumRetracement(active, candle);
        UpdateMaximumAdverseExcursion(active, candle);

        decimal currentAtr = atr is > 0m ? atr.Value : active.AtrAtConfirmation!.Value;
        decimal retracementAtr = active.Direction == TrendDirection.Bullish
            ? (active.FavorableExtremePrice!.Value - candle.Close) / currentAtr
            : (candle.Close - active.FavorableExtremePrice!.Value) / currentAtr;
        bool adverseSlope = active.Direction == TrendDirection.Bullish
            ? EmaSlope < 0m
            : EmaSlope > 0m;
        bool structureInvalidated = BreaksPriorStructure(
            candle,
            active.Direction == TrendDirection.Bullish
                ? TrendDirection.Bearish
                : TrendDirection.Bullish);
        bool hardInvalidation = active.Direction == TrendDirection.Bullish
            ? candle.Close <= active.StructuralStartPrice
            : candle.Close >= active.StructuralStartPrice;
        bool ended = hardInvalidation
            || (adverseSlope
                && structureInvalidated
                && retracementAtr >= _config.EndRetracementAtr);

        if (ended)
        {
            TrendRecord completed = BuildRecord(active, candle);
            _active = null;
            // A reversal may originate at the preceding trend's favorable extreme. Keeping that
            // index avoids an artificial full-lookback dead zone after every completed trend.
            _earliestStructuralStartIndex = active.FavorableExtremeIndex ?? _barIndex;
            return completed;
        }

        // A one-bar EMA slope wobble is not exhaustion. Require an actual retracement plus
        // weakening slope/structure; otherwise the phase becomes permanently exhausted before a
        // statistically qualified entry can ever occur.
        if (active.HasReachedExhaustion
            || (retracementAtr >= _config.ExhaustionRetracementAtr
                && (adverseSlope || structureInvalidated)))
        {
            active.HasReachedExhaustion = true;
        }

        return null;
    }

    private TrendRecord BuildRecord(ActiveTrend active, Candle endCandle)
    {
        decimal extreme = active.FavorableExtremePrice!.Value;
        decimal confirmationPrice = active.ConfirmationPrice!.Value;
        decimal totalMove = DirectionalPercent(
            active.Direction,
            active.StructuralStartPrice,
            extreme);
        decimal moveAfterConfirmation = DirectionalPercent(
            active.Direction,
            confirmationPrice,
            extreme);
        int durationBars = _barIndex - active.StructuralStartIndex + 1;

        return new TrendRecord
        {
            Symbol = _symbol!,
            Direction = active.Direction,
            StructuralStartTime = active.StructuralStartTime,
            StructuralStartPrice = active.StructuralStartPrice,
            ConfirmationTime = active.ConfirmationTime!.Value,
            ConfirmationPrice = confirmationPrice,
            EndTime = CandleCloseTime(endCandle),
            EndPrice = endCandle.Close,
            FavorableExtremeTime = active.FavorableExtremeTime!.Value,
            FavorableExtremePrice = extreme,
            DurationBars = durationBars,
            DurationHours = (decimal)(CandleCloseTime(endCandle) - active.StructuralStartTime).TotalHours,
            TotalMovePct = totalMove,
            MoveAfterConfirmationPct = moveAfterConfirmation,
            AtrNormalizedMove = Math.Abs(extreme - active.StructuralStartPrice)
                / active.AtrAtConfirmation!.Value,
            VolatilityPctAtConfirmation = active.AtrAtConfirmation.Value / confirmationPrice * 100m,
            MaximumAdverseExcursionPct = active.MaximumAdverseExcursionPct,
            MaximumRetracementPct = active.MaximumRetracementPct,
            ConfirmationDelayBars = active.ConfirmationIndex!.Value - active.StructuralStartIndex + 1,
            ConfirmationDelayPct = DirectionalPercent(
                active.Direction,
                active.StructuralStartPrice,
                confirmationPrice)
        };
    }

    private TrendState BuildCurrentState(Candle candle)
    {
        if (_active is null)
        {
            return TrendState.Neutral;
        }

        ActiveTrend active = _active;
        int durationBars = _barIndex - active.StructuralStartIndex + 1;
        TrendPhase phase;
        if (active.ConfirmationIndex is null)
        {
            phase = TrendPhase.Candidate;
        }
        else if (active.HasReachedExhaustion)
        {
            phase = TrendPhase.Exhaustion;
        }
        else if ((_barIndex - active.ConfirmationIndex.Value + 1) >= _config.MatureAfterBars)
        {
            phase = TrendPhase.Mature;
        }
        else
        {
            phase = TrendPhase.Confirmed;
        }

        return new TrendState
        {
            Phase = phase,
            Direction = active.Direction,
            Symbol = _symbol,
            StructuralStartTime = active.StructuralStartTime,
            StructuralStartPrice = active.StructuralStartPrice,
            ConfirmationTime = active.ConfirmationTime,
            ConfirmationPrice = active.ConfirmationPrice,
            VolatilityPctAtConfirmation = active.ConfirmationPrice is > 0m
                ? active.AtrAtConfirmation!.Value / active.ConfirmationPrice.Value * 100m
                : 0m,
            CurrentMovePct = Math.Max(
                0m,
                DirectionalPercent(
                    active.Direction,
                    active.StructuralStartPrice,
                    active.FavorableExtremePrice ?? candle.Close)),
            FavorableExtremePrice = active.FavorableExtremePrice,
            DurationBars = durationBars,
            DurationHours = (decimal)(CandleCloseTime(candle) - active.StructuralStartTime).TotalHours
        };
    }

    private void UpdateFavorableExtreme(ActiveTrend active, Candle candle)
    {
        decimal candidate = active.Direction == TrendDirection.Bullish
            ? candle.High
            : candle.Low;
        bool isMoreFavorable = active.Direction == TrendDirection.Bullish
            ? candidate > active.FavorableExtremePrice
            : candidate < active.FavorableExtremePrice;

        if (isMoreFavorable)
        {
            active.FavorableExtremePrice = candidate;
            active.FavorableExtremeTime = CandleCloseTime(candle);
            active.FavorableExtremeIndex = _barIndex;
        }
    }

    /// <summary>
    /// Tracks the worst excursion against the confirmation price. Only meaningful once the trend
    /// has confirmed, because before that there is no entry to be adverse to.
    /// </summary>
    private static void UpdateMaximumAdverseExcursion(ActiveTrend active, Candle candle)
    {
        if (active.ConfirmationPrice is not decimal entry || entry == 0m)
        {
            return;
        }

        // Bull: how far BELOW entry the bar traded. Bear: how far above.
        decimal adverse = active.Direction == TrendDirection.Bullish
            ? (entry - candle.Low) / entry * 100m
            : (candle.High - entry) / entry * 100m;
        active.MaximumAdverseExcursionPct = Math.Max(active.MaximumAdverseExcursionPct, Math.Max(0m, adverse));
    }

    private static void UpdateMaximumRetracement(ActiveTrend active, Candle candle)
    {
        decimal extreme = active.FavorableExtremePrice!.Value;
        decimal retracement = active.Direction == TrendDirection.Bullish
            ? Math.Max(0m, (extreme - candle.Close) / extreme * 100m)
            : Math.Max(0m, (candle.Close - extreme) / extreme * 100m);
        active.MaximumRetracementPct = Math.Max(active.MaximumRetracementPct, retracement);
    }

    private bool BreaksPriorStructure(Candle candle, TrendDirection direction)
    {
        IEnumerable<IndexedCandle> eligible = _recentCandles
            .Where(item => item.Index >= _earliestStructuralStartIndex)
            .TakeLast(_config.StructureLookbackBars);
        IndexedCandle[] prior = eligible.ToArray();
        if (prior.Length < _config.StructureLookbackBars)
        {
            return false;
        }

        return direction == TrendDirection.Bullish
            ? candle.Close > prior.Max(item => item.Candle.High)
            : candle.Close < prior.Min(item => item.Candle.Low);
    }

    private IndexedCandle? FindRecentExtreme(bool findLow)
    {
        IndexedCandle[] eligible = _recentCandles
            .Where(item => item.Index >= _earliestStructuralStartIndex)
            .TakeLast(_config.ExtremeLookbackBars)
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        // Latest wins ties so a flat extreme does not artificially lengthen a trend.
        return findLow
            ? eligible.OrderBy(item => item.Candle.Low).ThenByDescending(item => item.Index).First()
            : eligible.OrderByDescending(item => item.Candle.High).ThenByDescending(item => item.Index).First();
    }

    private decimal EmaSlope => _emaValues[^1] - _emaValues[0];

    private bool HasEmaSlope => _emaValues.Count > _config.EmaSlopeLookbackBars;

    private void TrimEmaValues()
    {
        int capacity = _config.EmaSlopeLookbackBars + 1;
        if (_emaValues.Count > capacity)
        {
            _emaValues.RemoveAt(0);
        }
    }

    private void AddRecentCandle(Candle candle)
    {
        _recentCandles.Add(new IndexedCandle(_barIndex, candle));
        int capacity = Math.Max(_config.ExtremeLookbackBars, _config.StructureLookbackBars);
        if (_recentCandles.Count > capacity)
        {
            _recentCandles.RemoveAt(0);
        }
    }

    private DateTimeOffset CandleCloseTime(Candle candle) => candle.OpenTime + _config.Timeframe;

    private void ValidateCandle(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);

        if (string.IsNullOrWhiteSpace(candle.Symbol))
        {
            throw new ArgumentException("A candle must specify a symbol.", nameof(candle));
        }

        if (!candle.IsComplete)
        {
            throw new ArgumentException("Only completed candles can be processed.", nameof(candle));
        }

        if (candle.Open <= 0m || candle.High <= 0m || candle.Low <= 0m || candle.Close <= 0m)
        {
            throw new ArgumentException("Candle prices must be positive.", nameof(candle));
        }

        if (candle.High < Math.Max(candle.Open, candle.Close)
            || candle.Low > Math.Min(candle.Open, candle.Close)
            || candle.Low > candle.High)
        {
            throw new ArgumentException("Candle OHLC values are inconsistent.", nameof(candle));
        }

        _symbol ??= candle.Symbol;
        if (!string.Equals(_symbol, candle.Symbol, StringComparison.Ordinal))
        {
            throw new ArgumentException("A detector instance can process only one symbol.", nameof(candle));
        }

        if (_lastOpenTime is not null && candle.OpenTime <= _lastOpenTime)
        {
            throw new ArgumentException(
                "Candles must be processed once in strictly increasing time order.",
                nameof(candle));
        }
        if (_lastOpenTime is not null
            && (candle.OpenTime - _lastOpenTime.Value).Ticks % _config.Timeframe.Ticks != 0)
        {
            throw new ArgumentException(
                $"Candle spacing must be an integer multiple of {_config.Timeframe}.",
                nameof(candle));
        }
    }

    private static decimal DirectionalPercent(
        TrendDirection direction,
        decimal start,
        decimal end)
    {
        decimal move = direction == TrendDirection.Bullish
            ? end - start
            : start - end;
        return move / start * 100m;
    }

    private readonly record struct IndexedCandle(int Index, Candle Candle);

    private sealed class ActiveTrend
    {
        public required TrendDirection Direction { get; init; }

        public required DateTimeOffset StructuralStartTime { get; init; }

        public required decimal StructuralStartPrice { get; init; }

        public required int StructuralStartIndex { get; init; }

        public required int CandidateDetectedIndex { get; init; }

        public int? ConfirmationIndex { get; set; }

        public DateTimeOffset? ConfirmationTime { get; set; }

        public decimal? ConfirmationPrice { get; set; }

        public decimal? AtrAtConfirmation { get; set; }

        public DateTimeOffset? FavorableExtremeTime { get; set; }

        public decimal? FavorableExtremePrice { get; set; }

        public int? FavorableExtremeIndex { get; set; }

        public decimal MaximumRetracementPct { get; set; }

        /// <summary>Worst move against the trend since confirmation, in percent.</summary>
        public decimal MaximumAdverseExcursionPct { get; set; }

        public bool HasReachedExhaustion { get; set; }

        public static ActiveTrend Start(
            TrendDirection direction,
            DateTimeOffset structuralStartTime,
            decimal structuralStartPrice,
            int structuralStartIndex,
            int candidateDetectedIndex) => new()
            {
                Direction = direction,
                StructuralStartTime = structuralStartTime,
                StructuralStartPrice = structuralStartPrice,
                StructuralStartIndex = structuralStartIndex,
                CandidateDetectedIndex = candidateDetectedIndex
            };
    }

    private sealed class AtrState(int period)
    {
        private decimal? _previousClose;
        private decimal? _value;
        private decimal _initialTotal;
        private int _count;

        public decimal? Value => _value;

        public decimal? Update(Candle candle)
        {
            decimal trueRange = _previousClose is null
                ? candle.High - candle.Low
                : Math.Max(
                    candle.High - candle.Low,
                    Math.Max(
                        Math.Abs(candle.High - _previousClose.Value),
                        Math.Abs(candle.Low - _previousClose.Value)));
            _previousClose = candle.Close;

            if (_value is not null)
            {
                _value = ((_value.Value * (period - 1)) + trueRange) / period;
                return _value;
            }

            _initialTotal += trueRange;
            _count++;
            if (_count == period)
            {
                _value = _initialTotal / period;
            }

            return _value;
        }
    }
}
