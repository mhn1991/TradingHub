using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Trend;

/// <summary>Thresholds for the trend layer, all traceable to module 5.</summary>
public sealed record AlfonsoTrendOptions
{
    /// <summary>
    /// Opposing eliminations needed alongside a trendline. Module 5: "an uptrend requires at least
    /// one supply level eliminated and two new bullish impulses connecting a bullish trendline".
    /// </summary>
    public int EliminationsWithTrendline { get; init; } = 1;

    /// <summary>
    /// Opposing eliminations that establish a trend on their own. Module 5: "An uptrend is also
    /// created when two supply zones have been eliminated and without the possibility of drawing a
    /// trendline."
    /// </summary>
    public int EliminationsWithoutTrendline { get; init; } = 2;

    /// <summary>
    /// Whether the elimination-only route is restricted to occasions when no trendline of that
    /// direction can be drawn.
    /// <para>
    /// Module 5 phrases it as a fallback - "two supply zones have been eliminated AND WITHOUT the
    /// possibility of drawing a trendline" - not as a second path always on offer. Treating it as
    /// co-equal let a trend be declared on eliminations alone while a line existed and could have
    /// disagreed, which on 3.5 years of gold H4 was how HALF of all trending bars were established
    /// (706 of 1,415). Trendlines turn out to be available about 50% of the time, so this is not a
    /// rare edge case.
    /// </para>
    /// </summary>
    public bool FallbackRequiresNoTrendline { get; init; } = true;

    /// <summary>
    /// Consecutive continuation patterns that mark the timeframe over-extended. Module 5:
    /// "Over-extesion is defined as the creation of three or more consecutive CPs, and/or three or
    /// more large ERCs."
    /// </summary>
    public int OverExtensionContinuationPatterns { get; init; } = 3;

    /// <summary>Consecutive same-direction extended range candles that mark over-extension.</summary>
    public int OverExtensionExtendedRangeCandles { get; init; } = 3;

    /// <summary>Body-to-range ratio at which a candle counts as an ERC for over-extension.</summary>
    public decimal ExtendedRangeBodyRatio { get; init; } = 0.80m;

    /// <summary>
    /// Whether a trendline break needs a candle to CLOSE beyond the line, rather than the whole
    /// candle to sit beyond it. Defaults to the close, which is how the rule is taught.
    /// </summary>
    public bool TrendlineBreakRequiresClose { get; init; } = true;

    /// <summary>
    /// Whether only VALID opposing zones count toward a trend change or an out-of-alignment.
    /// <para>
    /// A valid zone is one that itself accomplished something - it eliminated a prior opposing zone,
    /// or its move broke a trendline or a peak / valley. This matters more than it looks: the zone
    /// engine deliberately tracks every structure, accomplished or not, because trendlines are drawn
    /// from valleys and peaks rather than from validated imbalances. On real H4 gold that is 327
    /// structures where only a fraction ever accomplished anything, so counting all of them would
    /// let the elimination of a meaningless structure flip the trend.
    /// </para>
    /// </summary>
    public bool RequireValidZoneForTrendChange { get; init; } = true;

    /// <summary>
    /// Whether a trend must also agree with market structure - ascending peaks AND troughs for an
    /// uptrend, descending for a downtrend.
    /// <para>
    /// Module 5 states this as part of the definition, not as a refinement: "This must happen in the
    /// context of new bullish impulses where each successive peak and trough is higher than the ones
    /// found earlier. An uptrend requires an accomplishment, not just successive higher highs and
    /// higher lows." Only the accomplishment half was implemented, and the omission inverts the
    /// signal: a demand elimination happens when price falls THROUGH a demand zone, which is the end
    /// of a down-move rather than the start of one, so the state flipped to Downtrend at local lows.
    /// Measured over 3.5 years of gold, price then ROSE over the following 60 bars 60% of the time
    /// the state read Downtrend, mean -1.01% against the call.
    /// </para>
    /// <para>
    /// Structure that cannot be read - fewer than two swings of either kind - does not veto. The
    /// rule is a context requirement, and absence of context is not disagreement.
    /// </para>
    /// </summary>
    public bool RequireStructuralAgreement { get; init; } = true;
}

/// <summary>
/// The Up / Down / Out-of-alignment state machine for one timeframe (module 5), driven by the zone
/// events from <see cref="ImbalanceDetector"/> and the trendlines built from their swings.
/// <para>
/// The load-bearing rule is module 5's warning that "A trendline that connects two impulses does not
/// necessarily mean there is a trend ... An uptrend requires an accomplishment, not just successive
/// higher highs and higher lows." A trendline alone never sets a trend here; an elimination is
/// always required.
/// </para>
/// </summary>
public sealed class AlfonsoTrendDetector
{
    private readonly AlfonsoTrendOptions _options;
    private readonly List<decimal> _highs = [];
    private readonly List<decimal> _lows = [];
    private readonly List<DateTimeOffset> _times = [];
    private readonly List<SwingPoint> _valleys = [];
    private readonly List<SwingPoint> _peaks = [];
    private readonly HashSet<Trendline> _brokenLines = [];
    private readonly Queue<Trendline> _brokenLineOrder = [];

    private int _supplyEliminated;
    private int _demandEliminated;
    private int _consecutiveContinuations;
    private int _consecutiveExtendedRange;
    private bool _lastExtendedRangeWasBullish;

    private AlfonsoTrend _trend = AlfonsoTrend.Unknown;
    private bool _undermined;

    /// <summary>Eliminations that established the current trend, kept for reporting only.</summary>
    private int _establishedWith;
    private string _reason = "No history yet.";

    public AlfonsoTrendDetector(AlfonsoTrendOptions? options = null) =>
        _options = options ?? new AlfonsoTrendOptions();

    /// <summary>Trendline breaks seen recently, so the zone layer can claim them as accomplishments.</summary>
    public IReadOnlyList<(DateTimeOffset At, TrendlineDirection Direction)> RecentBreaks => _breaks;

    private readonly List<(DateTimeOffset At, TrendlineDirection Direction)> _breaks = [];

    public AlfonsoTrendSnapshot Snapshot { get; private set; } = new()
    {
        Trend = AlfonsoTrend.Unknown,
        OpposingEliminations = 0,
        IsOverExtended = false,
        Reason = "No history yet."
    };

    /// <summary>
    /// Applies one closed candle plus whatever the zone layer reported for it, and returns the
    /// resulting state.
    /// </summary>
    public AlfonsoTrendSnapshot Apply(AlfonsoBar bar, ImbalanceDetectorUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        _highs.Add(bar.High);
        _lows.Add(bar.Low);
        _times.Add(bar.OpenTime);
        int index = _highs.Count - 1;

        TrackOverExtension(bar, update);
        RecordSwings(update);
        ApplyEliminations(update);

        Trendline? bullish = TrendlineBuilder.Bullish(_valleys, _highs, _lows, _times, index);
        Trendline? bearish = TrendlineBuilder.Bearish(_peaks, _highs, _lows, _times, index);

        BreakTrendlines(bar, index, bullish, bearish);
        if (bullish is not null && _brokenLines.Contains(bullish))
            bullish = null;
        if (bearish is not null && _brokenLines.Contains(bearish))
            bearish = null;
        Resolve(bullish, bearish);

        Snapshot = new AlfonsoTrendSnapshot
        {
            Trend = _trend,
            Line = _trend switch
            {
                AlfonsoTrend.Uptrend => bullish,
                AlfonsoTrend.Downtrend => bearish,
                _ => null
            },
            OpposingEliminations = _trend is AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend
                ? _establishedWith
                : Math.Max(_supplyEliminated, _demandEliminated),
            IsOverExtended = IsOverExtended,
            Reason = _reason
        };

        return Snapshot;
    }

    /// <summary>
    /// Records breaks of lines that were already drawable before <paramref name="bar"/> closed.
    /// The timeframe analyzer calls this before zone detection so a break on the confirmation
    /// candle is visible to the new zone's accomplishment calculation.
    /// </summary>
    public void PreviewBreaks(AlfonsoBar bar)
    {
        if (_highs.Count == 0)
            return;

        int previousIndex = _highs.Count - 1;
        Trendline? bullish = TrendlineBuilder.Bullish(
            _valleys, _highs, _lows, _times, previousIndex);
        Trendline? bearish = TrendlineBuilder.Bearish(
            _peaks, _highs, _lows, _times, previousIndex);

        BreakTrendlines(bar, _highs.Count, bullish, bearish);
    }

    private bool IsOverExtended =>
        _consecutiveContinuations >= _options.OverExtensionContinuationPatterns ||
        _consecutiveExtendedRange >= _options.OverExtensionExtendedRangeCandles;

    /// <summary>
    /// Module 5's two over-extension routes. Both counters reset on the first sign of a correction,
    /// so the state clears itself rather than latching for the rest of the run.
    /// </summary>
    private void TrackOverExtension(AlfonsoBar bar, ImbalanceDetectorUpdate update)
    {
        bool extended = bar.BodyRatio >= _options.ExtendedRangeBodyRatio;
        if (extended)
        {
            bool bullish = bar.IsBullish;
            _consecutiveExtendedRange = _consecutiveExtendedRange > 0 && bullish == _lastExtendedRangeWasBullish
                ? _consecutiveExtendedRange + 1
                : 1;
            _lastExtendedRangeWasBullish = bullish;
        }
        else
        {
            _consecutiveExtendedRange = 0;
        }

        foreach (Imbalance zone in update.Created)
        {
            if (zone.IsContinuationPattern)
                _consecutiveContinuations++;
            else
                _consecutiveContinuations = 0;
        }

        // A correction ends the over-extension. Without this the continuation counter only ever
        // climbs - on real H4 gold it held the timeframe over-extended for 80% of all bars, which
        // would have silently forbidden almost every trade rather than describing the market.
        if (update.Eliminated.Count > 0 || update.Tested.Count > 0)
            _consecutiveContinuations = 0;
    }

    /// <summary>
    /// Turns newly confirmed non-continuation zones into swings. Module 3 bars continuation patterns
    /// from trendline construction, so they never become swings at all.
    /// </summary>
    private void RecordSwings(ImbalanceDetectorUpdate update)
    {
        foreach (Imbalance zone in update.Created)
        {
            if (zone.IsContinuationPattern)
                continue;

            int index = _times.FindLastIndex(time => time == zone.BaseEnd);
            if (index < 0)
                continue;

            SwingPoint swing = new()
            {
                Index = index,
                At = zone.BaseEnd,
                Price = zone.Distal,
                Kind = zone.Kind
            };

            if (zone.Kind == ImbalanceKind.Demand)
                _valleys.Add(swing);
            else
                _peaks.Add(swing);
        }

        Cap(_valleys);
        Cap(_peaks);
    }

    private static void Cap(List<SwingPoint> swings)
    {
        const int limit = 64;
        if (swings.Count > limit)
            swings.RemoveRange(0, swings.Count - limit);
    }

    private void ApplyEliminations(ImbalanceDetectorUpdate update)
    {
        foreach (Imbalance zone in update.Eliminated)
        {
            // Only the removal of a zone that had itself accomplished something says anything about
            // the trend. Structures that never achieved anything are tracked for their swings, not
            // for their significance.
            if (_options.RequireValidZoneForTrendChange && zone.Accomplished == Accomplishment.None)
                continue;

            if (zone.Kind == ImbalanceKind.Supply)
                _supplyEliminated++;
            else
                _demandEliminated++;

            // Module 5: an eliminated imbalance is one of the two routes into out of alignment. Only
            // the side the trend rests on counts - an uptrend eliminating supply is the uptrend
            // working, not the uptrend failing.
            bool undermines = (_trend == AlfonsoTrend.Uptrend && zone.Kind == ImbalanceKind.Demand) ||
                (_trend == AlfonsoTrend.Downtrend && zone.Kind == ImbalanceKind.Supply);

            if (undermines)
                Undermine($"{zone.Kind} eliminated while {_trend}.");
        }
    }

    private void BreakTrendlines(AlfonsoBar bar, int index, Trendline? bullish, Trendline? bearish)
    {
        if (bullish is not null &&
            !_brokenLines.Contains(bullish) &&
            bullish.IsBrokenBy(index, bar.High, bar.Low, bar.Close, _options.TrendlineBreakRequiresClose))
        {
            Retire(bullish);
            _breaks.Add((bar.OpenTime, TrendlineDirection.Bullish));
            if (_trend == AlfonsoTrend.Uptrend)
                Undermine("Bullish trendline broken by a full candle.");
        }

        if (bearish is not null &&
            !_brokenLines.Contains(bearish) &&
            bearish.IsBrokenBy(index, bar.High, bar.Low, bar.Close, _options.TrendlineBreakRequiresClose))
        {
            Retire(bearish);
            _breaks.Add((bar.OpenTime, TrendlineDirection.Bearish));
            if (_trend == AlfonsoTrend.Downtrend)
                Undermine("Bearish trendline broken by a full candle.");
        }

        const int keep = 32;
        if (_breaks.Count > keep)
            _breaks.RemoveRange(0, _breaks.Count - keep);
    }

    private void Retire(Trendline line)
    {
        if (!_brokenLines.Add(line))
            return;

        _brokenLineOrder.Enqueue(line);
        const int keep = 256;
        while (_brokenLineOrder.Count > keep)
            _brokenLines.Remove(_brokenLineOrder.Dequeue());
    }

    /// <summary>
    /// Records that the current trend has been undermined - its trendline broken, or one of the zones
    /// it rests on eliminated - WITHOUT deciding the outcome yet.
    /// <para>
    /// The decision has to wait for <see cref="Resolve"/>, because the same event is often evidence
    /// for the opposite trend as well: supply being eliminated during a downtrend both breaks that
    /// downtrend and is exactly what builds the case for an uptrend. Settling it here would send the
    /// state to out of alignment and clear the counters, destroying the evidence for the flip - which
    /// is what made a daily trend latch permanently.
    /// </para>
    /// </summary>
    private void Undermine(string reason)
    {
        _undermined = true;
        _reason = reason;
    }

    /// <summary>
    /// Out of alignment spends the accomplishments that built the old trend while preserving the
    /// event that invalidated it. The latter is the first piece of evidence for a reversal.
    /// </summary>
    private void EnterOutOfAlignment(string reason)
    {
        AlfonsoTrend previous = _trend;
        _trend = AlfonsoTrend.OutOfAlignment;
        _reason = reason;
        _undermined = false;

        // Spend evidence supporting the old trend, but retain the event that undermined it. That
        // first opposing elimination is event one of a two-elimination reversal, not disposable
        // bookkeeping on the way into out of alignment.
        if (previous == AlfonsoTrend.Uptrend)
            _supplyEliminated = 0;
        else if (previous == AlfonsoTrend.Downtrend)
            _demandEliminated = 0;
    }

    /// <summary>
    /// Settles the trend from the evidence accumulated so far.
    /// <para>
    /// A trend may flip DIRECTLY to the opposite direction; it does not have to pass through out of
    /// alignment on the way. The rule is stated as a change of trend in its own right: the market has
    /// reversed, at least one opposing zone is gone and a new trendline can be drawn the other way -
    /// or no trendline can be drawn but two opposing zones have gone.
    /// </para>
    /// <para>
    /// Returning early whenever a trend was already set made that impossible, and on a timeframe with
    /// few bars it latched outright. Over 200 daily gold candles the state was Downtrend for 74.5% of
    /// bars and Unknown for the remainder - never Uptrend, never out of alignment - across a window in
    /// which price rallied 4,212 to 5,602 before falling back to 4,047. No daily trendline was ever
    /// drawable, so the only exit from Downtrend was a valid supply elimination, and at that bar count
    /// those never arrived. The D1/H4/H1 sequence consequently took zero trades.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether the last two peaks and the last two troughs both run the way the trend claims.
    /// <para>
    /// Both series must agree. Higher highs alone describe a market making new extremes on failing
    /// support, which is the shape of a top rather than of an uptrend, and module 5's wording is
    /// explicit that it is "each successive peak AND trough".
    /// </para>
    /// </summary>
    private bool StructureAgrees(AlfonsoTrend candidate)
    {
        if (!_options.RequireStructuralAgreement)
            return true;

        // Too little structure to read is not a disagreement.
        if (_peaks.Count < 2 || _valleys.Count < 2)
            return true;

        bool peaksRising = _peaks[^1].Price > _peaks[^2].Price;
        bool troughsRising = _valleys[^1].Price > _valleys[^2].Price;

        return candidate == AlfonsoTrend.Uptrend
            ? peaksRising && troughsRising
            : !peaksRising && !troughsRising;
    }

    private void Resolve(Trendline? bullish, Trendline? bearish)
    {
        bool upByLine = bullish is not null && _supplyEliminated >= _options.EliminationsWithTrendline;
        bool upAlone = (!_options.FallbackRequiresNoTrendline || bullish is null) &&
            _supplyEliminated >= _options.EliminationsWithoutTrendline;
        bool downByLine = bearish is not null && _demandEliminated >= _options.EliminationsWithTrendline;
        bool downAlone = (!_options.FallbackRequiresNoTrendline || bearish is null) &&
            _demandEliminated >= _options.EliminationsWithoutTrendline;
        bool up = (upByLine || upAlone) && StructureAgrees(AlfonsoTrend.Uptrend);
        bool down = (downByLine || downAlone) && StructureAgrees(AlfonsoTrend.Downtrend);

        if (_trend is AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend)
        {
            // While a trend runs, only the OPPOSITE case can change it. Its own supporting
            // eliminations are the trend working, not evidence against it, so they are not weighed
            // here at all - weighing them made every reversal look like both sides eliminating at
            // once and sent the state to out of alignment instead of flipping it.
            bool opposite = _trend == AlfonsoTrend.Uptrend ? down : up;
            if (opposite)
            {
                Establish(_trend == AlfonsoTrend.Uptrend, downByLine, upByLine);
                return;
            }

            // Broken, with no opposite case made: there is nowhere to go but out of alignment.
            if (_undermined)
                EnterOutOfAlignment(_reason);

            return;
        }

        // From Unknown or out of alignment, both sides are open and a tie is genuinely directionless.
        if (up && down)
        {
            _reason = "Both sides eliminating; no clear direction.";
            _trend = AlfonsoTrend.OutOfAlignment;
            _undermined = false;
            return;
        }

        if (!up && !down)
        {
            if (_trend == AlfonsoTrend.Unknown)
                _reason = $"No accomplishment yet (supply {_supplyEliminated}, demand {_demandEliminated}).";
            return;
        }

        Establish(toDowntrend: down, downByLine, upByLine);
    }

    /// <summary>
    /// Sets the trend and spends the evidence that carried it.
    /// <para>
    /// Only the incoming side's count is cleared. The new trend then starts with a clean slate for
    /// the case against it, which is what lets it persist: clearing both counters left a trend with
    /// no buffer at all, and the first undermining event dropped it straight back out - on real
    /// candles that pushed the state to out of alignment for 83% to 99% of bars.
    /// </para>
    /// </summary>
    private void Establish(bool toDowntrend, bool downByLine, bool upByLine)
    {
        if (toDowntrend)
        {
            _trend = AlfonsoTrend.Downtrend;
            _establishedWith = _demandEliminated;
            _reason = downByLine
                ? $"{_demandEliminated} demand eliminated with a bearish trendline."
                : $"{_demandEliminated} demand eliminated without a drawable trendline.";
            _demandEliminated = 0;
        }
        else
        {
            _trend = AlfonsoTrend.Uptrend;
            _establishedWith = _supplyEliminated;
            _reason = upByLine
                ? $"{_supplyEliminated} supply eliminated with a bullish trendline."
                : $"{_supplyEliminated} supply eliminated without a drawable trendline.";
            _supplyEliminated = 0;
        }

        _undermined = false;
    }
}
