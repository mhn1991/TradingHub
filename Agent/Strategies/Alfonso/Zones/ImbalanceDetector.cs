namespace Agent.Strategies.Alfonso.Zones;

/// <summary>
/// Locates supply and demand imbalances on one timeframe, and maintains their lifecycle, following
/// the Set and Forget core rules (course modules 2, 4 and 7).
/// <para>
/// Strictly incremental and strictly causal: candles are applied one at a time in order, and a zone
/// is only ever confirmed on the candle that completes its consolidation away. Nothing is ever
/// backfilled onto an earlier bar, so a zone can never be known before the market could have known
/// it. Every timeframe owns its own detector - module 9: "Each timeframe will have its trend and
/// imbalances, completely independent from other timeframes."
/// </para>
/// </summary>
public sealed class ImbalanceDetector
{
    private readonly ImbalanceOptions _options;
    private readonly TimeSpan _interval;
    private readonly List<AlfonsoBar> _bars = [];
    private readonly List<Imbalance> _zones = [];
    private readonly List<Imbalance> _eliminationHistory = [];

    /// <summary>Base end indices already turned into a zone, so one base cannot emit twice.</summary>
    private readonly HashSet<int> _claimedBaseEnds = [];

    /// <summary>
    /// Highest high and lowest low seen up to AND INCLUDING each index, parallel to <c>_bars</c>.
    /// <para>
    /// A single running extreme cannot answer the all-time-high question. By the time a zone is
    /// confirmed, its own impulse bars have already been folded into any running extreme, so
    /// "did this impulse break the high" would compare the impulse against itself and always answer
    /// no - a silent no-op that would price the extreme-broken accomplishment at exactly zero.
    /// Keeping the watermark per index lets the impulse be compared with the market as it stood
    /// before the impulse began.
    /// </para>
    /// </summary>
    private readonly List<decimal> _highWater = [];
    private readonly List<decimal> _lowWater = [];

    /// <summary>
    /// True ranges, for the ATR that sets this timeframe's distance scale. True range rather than
    /// high-low so a gap between candles counts as the move it was.
    /// </summary>
    /// <summary>
    /// Recent peak and valley levels - the distal lines of non-continuation zones. Continuation
    /// patterns are excluded for the same reason module 3 bars them from trendlines: they are pauses
    /// inside a move, not the turns that define structure.
    /// </summary>
    private readonly List<(int Index, decimal Price)> _peaks = [];
    private readonly List<(int Index, decimal Price)> _valleys = [];

    private readonly Queue<decimal> _trueRanges = new();
    private decimal _trueRangeSum;
    private decimal? _previousClose;

    /// <summary>
    /// Bars dropped from the front of <c>_bars</c>. Indices held in <c>_claimedBaseEnds</c> are
    /// absolute, so trimming has to rebase rather than clear - clearing would let an already
    /// emitted base be detected a second time and duplicate its zone.
    /// </summary>
    private int _barOffset;

    /// <summary>
    /// Supplied by the trend layer to answer the third accomplishment route, which this detector
    /// cannot see on its own: was a trendline broken during the impulse, in the direction that
    /// creates this kind of zone?
    /// <para>
    /// Module 4: "the break of a trendline with at least a full OCHL candlestick creates a new
    /// imbalance at the origin of the move". Price breaking a BEARISH line upward creates demand;
    /// breaking a BULLISH line downward creates supply. Left unset, the detector simply cannot award
    /// this accomplishment - which is a real limitation, not a silent zero, and is why the trend
    /// layer wires it up.
    /// </para>
    /// </summary>
    public Func<DateTimeOffset, DateTimeOffset, ImbalanceKind, bool>? TrendlineBreakLookup { get; set; }

    public ImbalanceDetector(TimeSpan interval, ImbalanceOptions? options = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

        _options = options ?? new ImbalanceOptions();
        _options.Validate();
        _interval = interval;
    }

    /// <summary>Live zones, newest first. Eliminated zones are dropped.</summary>
    public IReadOnlyList<Imbalance> Zones => _zones;

    /// <summary>
    /// Tradeable zones of one kind, nearest the given price first. This is the order the rules care
    /// about: the first level price will reach is the one that matters.
    /// </summary>
    /// <summary>Whether this zone is sitting out a test the engine has not yet resolved.</summary>
    public bool HasPendingTest(Imbalance zone) =>
        zone is not null && _pendingTest.Contains(zone.BaseEnd);

    public IReadOnlyList<Imbalance> TradeableZones(ImbalanceKind kind, decimal price) => _zones
        .Where(zone =>
            zone.Kind == kind &&
            zone.IsTradeable &&
            !_pendingTest.Contains(zone.BaseEnd))
        .OrderBy(zone => Math.Abs(zone.Proximal - price))
        .ToArray();

    /// <summary>
    /// Applies one CLOSED candle. Lifecycle is processed before detection so that a candle which
    /// both eliminates an old zone and confirms a new one is handled in the order the rules
    /// describe - the elimination is what the new zone accomplished.
    /// </summary>
    public ImbalanceDetectorUpdate Apply(AlfonsoBar bar)
    {
        _bars.Add(bar);
        int index = _bars.Count - 1;

        _highWater.Add(index == 0 ? bar.High : Math.Max(_highWater[index - 1], bar.High));
        _lowWater.Add(index == 0 ? bar.Low : Math.Min(_lowWater[index - 1], bar.Low));
        TrackTrueRange(bar);

        List<Imbalance> eliminated = UpdateLifecycle(bar, out List<Imbalance> tested, out List<Imbalance> touched);
        List<Imbalance> created = Detect(index);

        Trim();

        return created.Count == 0 && eliminated.Count == 0 && tested.Count == 0 && touched.Count == 0
            ? ImbalanceDetectorUpdate.Empty
            : new ImbalanceDetectorUpdate
            {
                Created = created,
                Eliminated = eliminated,
                Tested = tested,
                Touched = touched
            };
    }

    /// <summary>Average true range over the configured lookback, or null before any bar has landed.</summary>
    private decimal? Atr => _trueRanges.Count == 0 ? null : _trueRangeSum / _trueRanges.Count;

    private void TrackTrueRange(AlfonsoBar bar)
    {
        decimal trueRange = _previousClose is decimal close
            ? Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - close), Math.Abs(bar.Low - close)))
            : bar.Range;

        _trueRanges.Enqueue(trueRange);
        _trueRangeSum += trueRange;
        if (_trueRanges.Count > _options.AtrLookbackCandles)
            _trueRangeSum -= _trueRanges.Dequeue();

        _previousClose = bar.Close;
    }

    /// <summary>
    /// How the departure is scored. A gap is module 7's strongest form outright; otherwise the leg
    /// has to carry price the required ATR distance within the speed window - the same test whether
    /// one candle covered it or two.
    /// </summary>
    private ImpulseStrength Score(decimal displacement, bool gapped) => gapped
        ? ImpulseStrength.Gap
        : Atr is decimal atr && atr > 0m && displacement >= _options.MinimumImpulseAtrMultiple * atr
            ? ImpulseStrength.Strong
            : ImpulseStrength.Weak;

    /// <summary>
    /// Elimination and testing. Module 4: "An imbalance is eliminated if the lowest low or the
    /// highest high of the basing structure has been penetrated through by as little as a tick or a
    /// pip." Module 7 defines a completed test as price reaching the proximal line and then
    /// consolidating away with a full candle.
    /// </summary>
    private List<Imbalance> UpdateLifecycle(
        AlfonsoBar bar, out List<Imbalance> tested, out List<Imbalance> touched)
    {
        List<Imbalance> eliminated = [];
        tested = [];
        touched = [];

        for (int index = _zones.Count - 1; index >= 0; index--)
        {
            Imbalance zone = _zones[index];

            // Module 4 treats any penetration beyond the distal as elimination. A close-only
            // interpretation remains available for explicit comparison runs.
            decimal beyond = _options.EliminationRequiresClose
                ? bar.Close
                : zone.Kind == ImbalanceKind.Demand ? bar.Low : bar.High;

            bool distalPenetrated = zone.Kind == ImbalanceKind.Demand
                ? beyond < zone.Distal
                : beyond > zone.Distal;

            if (distalPenetrated)
            {
                Imbalance dead = zone with
                {
                    State = ImbalanceState.Eliminated,
                    EliminatedAt = bar.OpenTime
                };
                eliminated.Add(dead);
                _eliminationHistory.Add(dead);
                _zones.RemoveAt(index);
                _pendingTest.Remove(zone.BaseEnd);
                continue;
            }

            bool touchedProximal = zone.Kind == ImbalanceKind.Demand
                ? bar.Low <= zone.Proximal
                : bar.High >= zone.Proximal;

            // While the leg out is still running - the zone untested and price still clear of it -
            // the departure keeps being re-scored. A zone is confirmed as soon as one full candle
            // stands clear of the base, which is usually long before the move has finished, so
            // freezing the impulse there would judge a multi-day rally on its first candle.
            if (!touchedProximal && zone.TestCount == 0)
            {
                decimal excursion = zone.Kind == ImbalanceKind.Demand
                    ? bar.High - zone.Proximal
                    : zone.Proximal - bar.Low;

                int bars = zone.ImpulseBarsTracked + 1;
                decimal ratio = Math.Max(
                    zone.ImpulseToBaseRatio, zone.Width <= 0m ? 0m : excursion / zone.Width);

                // Distance keeps accruing for as long as the leg runs, because the 2:1 in module 7
                // is measured against the finished move. Strength does not: it is a measure of the
                // DEPARTURE, so it stops being re-scored once the speed window has passed. Letting
                // it keep upgrading would score a slow grind as a strong impulse, which is exactly
                // the "pushing a car up a hill" the module calls weak.
                decimal displacement = Math.Max(zone.ImpulseDisplacement, excursion);
                ImpulseStrength strength = bars <= _options.ImpulseSpeedCandles
                    ? Score(displacement, zone.Strength == ImpulseStrength.Gap)
                    : zone.Strength;

                zone = zone with
                {
                    ImpulseToBaseRatio = ratio,
                    ImpulseDisplacement = displacement,
                    ImpulseBarsTracked = bars,
                    Strength = strength,
                    MeetsTradeabilityCriteria = Qualifies(ratio, strength, zone.Accomplished)
                };
                _zones[index] = zone;
            }

            if (touchedProximal)
            {
                touched.Add(zone);

                // Entering the zone only starts a test; module 7 requires a full candle back away
                // from the proximal line before the level counts as used. That completion is
                // detected on a later candle by the branch below.
                if (!_pendingTest.Contains(zone.BaseEnd))
                    _pendingTest.Add(zone.BaseEnd);
                continue;
            }

            if (!_pendingTest.Contains(zone.BaseEnd))
                continue;

            bool clearOfZone = zone.Kind == ImbalanceKind.Demand
                ? bar.Low > zone.Proximal
                : bar.High < zone.Proximal;

            if (!clearOfZone)
                continue;

            _pendingTest.Remove(zone.BaseEnd);

            // Capped: a level is retired once it reaches the maximum, and counting further
            // pullbacks past that point makes the field disagree with its own configured bound.
            int count = Math.Min(zone.TestCount + 1, _options.MaximumTests);
            Imbalance worn = zone with
            {
                TestCount = count,
                State = count >= _options.MaximumTests ? ImbalanceState.UsedUp : ImbalanceState.Tested
            };
            _zones[index] = worn;
            tested.Add(worn);
        }

        return eliminated;
    }

    /// <summary>Base end times whose zones are mid-test, awaiting a full candle away.</summary>
    private readonly HashSet<DateTimeOffset> _pendingTest = [];

    /// <summary>
    /// Looks for zones whose consolidation away completes exactly on <paramref name="index"/>.
    /// Scanning is bounded to the window in which a pattern could still be completing.
    /// </summary>
    private List<Imbalance> Detect(int index)
    {
        List<Imbalance> created = [];

        int window = _options.MaximumBaseCandles + _options.MaximumImpulseCandles +
            _options.ConsolidationAwayCandles + 2;
        int earliest = Math.Max(0, index - window);

        for (int baseEnd = index - 1; baseEnd >= earliest; baseEnd--)
        {
            if (_claimedBaseEnds.Contains(ToAbsoluteBarIndex(baseEnd, _barOffset)))
                continue;

            if (TryBuild(baseEnd, index) is not Imbalance zone)
                continue;

            _claimedBaseEnds.Add(ToAbsoluteBarIndex(baseEnd, _barOffset));
            _zones.Insert(0, zone);
            created.Add(zone);

            if (!zone.IsContinuationPattern)
            {
                List<(int Index, decimal Price)> swings =
                    zone.Kind == ImbalanceKind.Supply ? _peaks : _valleys;
                swings.Add((ToAbsoluteBarIndex(baseEnd, _barOffset), zone.Distal));
                if (swings.Count > _options.SwingMemory)
                    swings.RemoveRange(0, swings.Count - _options.SwingMemory);
            }
        }

        return created;
    }

    /// <summary>
    /// Attempts to assemble a zone whose base ends at <paramref name="baseEnd"/> and whose
    /// consolidation away finishes exactly at <paramref name="confirmIndex"/>. Returns null whenever
    /// any rule fails, which is the overwhelmingly common case.
    /// </summary>
    private Imbalance? TryBuild(int baseEnd, int confirmIndex)
    {
        if (!TryFindBase(baseEnd, out int baseStart))
            return null;

        int impulseStart = baseEnd + 1;
        if (impulseStart > confirmIndex)
            return null;

        AlfonsoBar first = _bars[impulseStart];
        bool bullish = first.Close > _bars[baseEnd].BodyTop || first.IsBullish;

        ImbalanceKind kind = bullish ? ImbalanceKind.Demand : ImbalanceKind.Supply;

        (decimal proximal, decimal distal) = Lines(baseStart, baseEnd, kind);
        decimal width = Math.Abs(proximal - distal);
        if (width <= 0m)
            return null;

        if (!TryConfirmConsolidation(impulseStart, confirmIndex, kind, proximal, distal))
            return null;

        MeasureImpulse(impulseStart, confirmIndex, kind, proximal, width,
            out decimal ratio, out decimal displacement, out bool gapped);

        int impulseEnd = confirmIndex;
        Accomplishment accomplished = Achievements(kind, impulseStart, impulseEnd);

        ImpulseStrength strength = Score(displacement, gapped);

        return new Imbalance
        {
            Interval = _interval,
            Kind = kind,
            Proximal = proximal,
            Distal = distal,
            BaseStart = _bars[baseStart].OpenTime,
            BaseEnd = _bars[baseEnd].OpenTime,
            ConfirmedAt = _bars[confirmIndex].OpenTime,
            BaseCandleCount = baseEnd - baseStart + 1,
            Strength = strength,
            Accomplished = accomplished,
            ImpulseToBaseRatio = ratio,
            ImpulseDisplacement = displacement,
            ImpulseBarsTracked = confirmIndex - impulseStart + 1,
            IsContinuationPattern = IsContinuation(baseStart, kind, distal),
            MeetsTradeabilityCriteria = Qualifies(ratio, strength, accomplished)
        };
    }

    /// <summary>
    /// Walks back from <paramref name="baseEnd"/> over candles that are pauses. Module 7: "Tight
    /// candle bases with bodies &lt;= 50% of the candle range."
    /// <para>
    /// Falls back to the single-candle base module 2 allows, where there is no pause at all and the
    /// turn is made of two opposing extended-range candles: "the basing structure of a valley may be
    /// formed by non 50% candlesticks and be made of only a bearish ERC and a bullish ERC".
    /// </para>
    /// </summary>
    private bool TryFindBase(int baseEnd, out int baseStart)
    {
        baseStart = baseEnd;

        if (_bars[baseEnd].BodyRatio <= _options.MaximumBasingBodyRatio)
        {
            // The base has to end where the pause ends. Without this, a run of three basing candles
            // yields a base for each of its truncations - three zones sharing one proximal, one
            // distal and one impulse - which triples the zone count and hands the trend layer three
            // copies of the same swing.
            if (baseEnd + 1 < _bars.Count &&
                _bars[baseEnd + 1].BodyRatio <= _options.MaximumBasingBodyRatio)
            {
                return false;
            }

            while (baseStart - 1 >= 0 &&
                baseEnd - (baseStart - 1) + 1 <= _options.MaximumBaseCandles &&
                _bars[baseStart - 1].BodyRatio <= _options.MaximumBasingBodyRatio)
            {
                baseStart--;
            }

            return baseEnd - baseStart + 1 >= _options.MinimumBaseCandles;
        }

        // Drop-rally / rally-drop: the "base" is the single turning candle, and it is an ERC.
        return baseEnd > 0 &&
            _bars[baseEnd].BodyRatio >= _options.ExtendedRangeBodyRatio &&
            _bars[baseEnd - 1].BodyRatio >= _options.ExtendedRangeBodyRatio &&
            _bars[baseEnd].IsBullish != _bars[baseEnd - 1].IsBullish;
    }

    /// <summary>
    /// Proximal and distal lines. Module 4: the distal "must always include the lowest low in the
    /// basing structure when drawing a demand level and the highest high when drawing a supply
    /// level"; the proximal sits at the body edge nearest price, or over the wicks when
    /// <see cref="ImbalanceOptions.ProximalCoversWicks"/> is set.
    /// </summary>
    private (decimal Proximal, decimal Distal) Lines(int baseStart, int baseEnd, ImbalanceKind kind)
    {
        decimal proximal = kind == ImbalanceKind.Demand ? decimal.MinValue : decimal.MaxValue;
        decimal distal = kind == ImbalanceKind.Demand ? decimal.MaxValue : decimal.MinValue;

        for (int index = baseStart; index <= baseEnd; index++)
        {
            AlfonsoBar bar = _bars[index];
            if (kind == ImbalanceKind.Demand)
            {
                proximal = Math.Max(proximal, _options.ProximalCoversWicks ? bar.High : bar.BodyTop);
                distal = Math.Min(distal, bar.Low);
            }
            else
            {
                proximal = Math.Min(proximal, _options.ProximalCoversWicks ? bar.Low : bar.BodyBottom);
                distal = Math.Max(distal, bar.High);
            }
        }

        return (proximal, distal);
    }

    /// <summary>
    /// Whether consolidation away completes exactly at <paramref name="confirmIndex"/>.
    /// <para>
    /// Module 7 states this as the impulse being "strong enough to stay away from the potential base
    /// for at least one or more full OCHL candles. These candles should, by no means, have tested
    /// the potential imbalance". The away-candles are therefore part of the move, not a pause after
    /// it - an earlier reading here treated them as a separate phase and defined the leg out as a
    /// run of consecutive extended-range candles, which on real candles almost never extends past
    /// one bar. The result was an impulse measured over a single candle: every zone scored Weak,
    /// none reached 2:1, and over eight months of H4 data not one Strong impulse was found.
    /// </para>
    /// </summary>
    private bool TryConfirmConsolidation(
        int impulseStart, int confirmIndex, ImbalanceKind kind, decimal proximal, decimal distal)
    {
        int clear = 0;
        int last = Math.Min(confirmIndex, impulseStart + _options.MaximumImpulseCandles - 1);

        for (int index = impulseStart; index <= last; index++)
        {
            AlfonsoBar bar = _bars[index];

            // A zone whose distal is taken out during its own leg never existed.
            if (kind == ImbalanceKind.Demand ? bar.Low < distal : bar.High > distal)
                return false;

            bool away = kind == ImbalanceKind.Demand ? bar.Low > proximal : bar.High < proximal;
            if (!away)
            {
                // The first candle out is still leaving the base, so it is allowed to touch it.
                // Every candle after that must stand clear: module 4 says "An imbalance will not be
                // confirmed if price returns to the origin of the move in the very next
                // candlestick", which invalidates the zone rather than postponing it.
                if (index > impulseStart)
                    return false;

                continue;
            }

            clear++;
            if (clear >= _options.ConsolidationAwayCandles)
                return index == confirmIndex;
        }

        return false;
    }

    /// <summary>
    /// Measures the leg out over everything from the base to confirmation: how far it travelled
    /// relative to the zone width, how many extended range candles carried it, and whether it
    /// gapped away. Module 7: "The impulse created after the basing structure has to be twice as
    /// wide as the basing structure ... It also has to be made of at least two ERCs."
    /// </summary>
    private void MeasureImpulse(
        int impulseStart,
        int confirmIndex,
        ImbalanceKind kind,
        decimal proximal,
        decimal width,
        out decimal ratio,
        out decimal displacement,
        out bool gapped)
    {
        decimal extreme = kind == ImbalanceKind.Demand ? decimal.MinValue : decimal.MaxValue;

        for (int index = impulseStart; index <= confirmIndex; index++)
        {
            AlfonsoBar bar = _bars[index];
            extreme = kind == ImbalanceKind.Demand
                ? Math.Max(extreme, bar.High)
                : Math.Min(extreme, bar.Low);
        }

        displacement = Math.Abs(extreme - proximal);

        AlfonsoBar opening = _bars[impulseStart];
        decimal clearance = kind == ImbalanceKind.Demand
            ? opening.Low - proximal
            : proximal - opening.High;
        gapped = clearance >= _options.MinimumGapToWidthRatio * width;

        ratio = Math.Abs(extreme - proximal) / width;
    }

    /// <summary>
    /// Whether the candle is an extended range candle running the same way as the leg out.
    /// </summary>
    private bool IsAlignedExtendedRange(int index, ImbalanceKind kind) =>
        IsAlignedExtendedRange(_bars[index], kind);

    private bool IsAlignedExtendedRange(AlfonsoBar bar, ImbalanceKind kind)
    {
        bool aligned = kind == ImbalanceKind.Demand ? bar.IsBullish : bar.IsBearish;
        return aligned && bar.BodyRatio >= _options.ExtendedRangeBodyRatio;
    }

    /// <summary>
    /// The tradeability bar, kept in one place so creation and re-scoring cannot drift: an
    /// accomplishment (module 4), a 2:1 impulse and a departure that is not weak (module 7).
    /// </summary>
    private bool Qualifies(decimal ratio, ImpulseStrength strength, Accomplishment accomplished) =>
        (!_options.RequireAccomplishment || accomplished != Accomplishment.None) &&
        ratio >= _options.MinimumImpulseToBaseRatio &&
        strength != ImpulseStrength.Weak;

    /// <summary>
    /// What the impulse accomplished, as far as this timeframe can tell on its own. Trendline breaks
    /// are the third route and are supplied by the trend layer, which owns trendlines.
    /// </summary>
    private Accomplishment Achievements(
        ImbalanceKind kind,
        int impulseStart,
        int impulseEnd)
    {
        Accomplishment result = Accomplishment.None;

        DateTimeOffset from = _bars[impulseStart].OpenTime;
        DateTimeOffset to = _bars[impulseEnd].OpenTime;
        ImbalanceKind opposing = kind == ImbalanceKind.Demand ? ImbalanceKind.Supply : ImbalanceKind.Demand;
        if (_eliminationHistory.Any(zone =>
                zone.Kind == opposing &&
                zone.EliminatedAt is DateTimeOffset eliminatedAt &&
                eliminatedAt >= from && eliminatedAt <= to))
            result |= Accomplishment.OpposingImbalanceEliminated;

        if (TrendlineBreakLookup?.Invoke(from, to, kind) == true)
            result |= Accomplishment.TrendlineBreak;

        if (impulseStart > 0)
        {
            decimal priorHigh = _highWater[impulseStart - 1];
            decimal priorLow = _lowWater[impulseStart - 1];

            for (int index = impulseStart; index <= impulseEnd; index++)
            {
                AlfonsoBar bar = _bars[index];
                bool broke = kind == ImbalanceKind.Demand
                    ? bar.High > priorHigh
                    : bar.Low < priorLow;

                if (broke)
                {
                    result |= Accomplishment.ExtremeBroken;
                    break;
                }
            }
        }

        // A bullish impulse takes out a PEAK; a bearish one takes out a VALLEY. Only swings that
        // existed before the impulse began can be broken by it.
        if (_options.SwingBreakIsAnAccomplishment && BrokeASwing(kind, impulseStart, impulseEnd))
            result |= Accomplishment.SwingBroken;

        return result;
    }

    private bool BrokeASwing(ImbalanceKind kind, int impulseStart, int impulseEnd)
    {
        List<(int Index, decimal Price)> swings = kind == ImbalanceKind.Demand ? _peaks : _valleys;
        int absoluteImpulseStart = ToAbsoluteBarIndex(impulseStart, _barOffset);

        for (int index = impulseStart; index <= impulseEnd; index++)
        {
            AlfonsoBar bar = _bars[index];
            foreach ((int at, decimal price) in swings)
            {
                if (at >= absoluteImpulseStart)
                    continue;

                if (kind == ImbalanceKind.Demand ? bar.High > price : bar.Low < price)
                    return true;
            }
        }

        return false;
    }

    internal static int ToAbsoluteBarIndex(int relativeIndex, int barOffset) =>
        checked(relativeIndex + barOffset);

    /// <summary>
    /// Whether the base is a pause inside a move rather than the turn at its origin.
    /// <para>
    /// Module 2 defines a valley as "a V shape formation ... at the origin of a bullish impulsive
    /// move", so the test is whether the base actually turned price: its low has to be the lowest
    /// of the approach. If price had already traded below this level on the way in, the base is not
    /// the bottom of anything - it is a pause, which module 2 tells us to treat as a continuation
    /// pattern when in doubt.
    /// </para>
    /// <para>
    /// The earlier test compared net close-to-close direction over the approach. In a trending
    /// market that reads almost every pullback as a continuation - 24 of 31 zones on real H4 gold -
    /// and since module 3 forbids drawing trendlines from continuation patterns, the trend layer was
    /// left with too few swings to ever draw one. No trendline means no trendline break, and the
    /// break is the primary route by which imbalances are created at all.
    /// </para>
    /// </summary>
    private bool IsContinuation(int baseStart, ImbalanceKind kind, decimal distal)
    {
        int from = Math.Max(0, baseStart - _options.LegInLookbackCandles);

        // Module 2: "When you are in doubt, consider them as a CP." With no approach to read there
        // is no evidence this base turned anything, so it is a pause until shown otherwise. The
        // inverted default made every zone at the start of a series a swing, and swings are what
        // trendlines are built from.
        if (from >= baseStart)
            return _options.TreatAmbiguousBaseAsContinuation;

        for (int index = from; index < baseStart; index++)
        {
            bool beyond = kind == ImbalanceKind.Demand
                ? _bars[index].Low < distal
                : _bars[index].High > distal;

            if (beyond)
                return true;
        }

        return false;
    }

    /// <summary>Bounds retained history so a long run cannot grow without limit.</summary>
    private void Trim()
    {
        if (_zones.Count > _options.MaximumTrackedZones)
            _zones.RemoveRange(_options.MaximumTrackedZones, _zones.Count - _options.MaximumTrackedZones);

        int keep = (_options.MaximumBaseCandles + _options.MaximumImpulseCandles +
            _options.ConsolidationAwayCandles + 2) * 4;
        if (_bars.Count <= keep * 2)
            return;

        int drop = _bars.Count - keep;
        _bars.RemoveRange(0, drop);
        _highWater.RemoveRange(0, drop);
        _lowWater.RemoveRange(0, drop);
        _barOffset += drop;

        DateTimeOffset earliestRetained = _bars[0].OpenTime;
        _eliminationHistory.RemoveAll(zone => zone.EliminatedAt < earliestRetained);

        // Absolute ids below the retained window can never be revisited, so they are dropped rather
        // than kept forever.
        _claimedBaseEnds.RemoveWhere(id => id < _barOffset);
    }
}
