using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;

namespace ChartAnnotator.Liquidity;

/// <summary>
/// Causal price-inferred liquidity analysis. It consumes the engine's already-confirmed swings;
/// candidates and lifecycle events are published only on a completed candle.
/// </summary>
public sealed class LiquidityAnalyzer
{
    private readonly LiquidityCalculationProfile _profile;
    private readonly string _profileHash;
    private readonly TimeZoneInfo _referenceTimeZone;
    private readonly Dictionary<Guid, PoolRuntimeState> _pools = [];
    private readonly List<LiquidityEvent> _recentEvents = [];
    private readonly List<LiquiditySweepEvent> _recentSweeps = [];
    private long _snapshotVersion;

    public LiquidityAnalyzer(LiquidityCalculationProfile? profile = null)
    {
        _profile = profile ?? new LiquidityCalculationProfile();
        _profile.Validate();
        _profileHash = LiquidityCalculationProfileHasher.ComputeHash(_profile);
        _referenceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(_profile.ReferenceTimeZoneId);
    }

    public LiquidityAnalysisSnapshot Update(
        Candle currentCandle,
        IReadOnlyList<Candle> candleHistory,
        IReadOnlyList<SwingPoint> confirmedSwings,
        decimal? atr,
        MarketStructureSnapshot marketStructure,
        PriceActionSnapshot priceAction,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(currentCandle);
        ArgumentNullException.ThrowIfNull(candleHistory);
        ArgumentNullException.ThrowIfNull(confirmedSwings);
        ArgumentNullException.ThrowIfNull(marketStructure);
        ArgumentNullException.ThrowIfNull(priceAction);

        DateTimeOffset availableAt = currentCandle.CloseTime ?? currentCandle.OpenTime;
        _snapshotVersion++;
        if (!_profile.Enabled || atr is not > 0m)
        {
            return LiquidityAnalysisSnapshot.Disabled with
            {
                ProfileHash = _profileHash,
                SnapshotVersion = _snapshotVersion,
                AvailableAt = availableAt
            };
        }

        decimal atrValue = atr.Value;
        var events = new List<LiquidityEvent>();
        var sweeps = new List<LiquiditySweepEvent>();
        int suppressed = 0;

        UpdateExistingPools(currentCandle, atrValue, marketStructure, priceAction, sequence, availableAt, events, sweeps);
        DetectEqualLevels(currentCandle, confirmedSwings, atrValue, sequence, availableAt, ref suppressed);
        DetectIsolatedSwings(currentCandle, candleHistory, confirmedSwings, atrValue, sequence, availableAt, ref suppressed);
        DetectRangeLevels(currentCandle, candleHistory, atrValue, sequence, availableAt, ref suppressed);
        DetectCompletedPeriodLevels(currentCandle, candleHistory, atrValue, sequence, availableAt, ref suppressed);
        DetectRoundNumbers(currentCandle, atrValue, sequence, availableAt, ref suppressed);
        MergeOverlappingPools(availableAt, events);
        PruneExcessPools(availableAt, events);

        _recentEvents.AddRange(events);
        TrimTo(_recentEvents, _profile.MaximumRetainedEvents);
        _recentSweeps.AddRange(sweeps);
        TrimTo(_recentSweeps, _profile.MaximumRetainedSweeps);

        LiquidityPool[] active = _pools.Values
            .Where(state => !state.Terminal)
            .Select(state => state.Pool)
            .OrderBy(pool => pool.Side)
            .ThenBy(pool => pool.ReferencePrice)
            .ThenBy(pool => pool.PoolId)
            .ToArray();
        LiquidityPool[] pools = _pools.Values
            .Select(state => state.Pool)
            .OrderByDescending(pool => pool.AvailableAt)
            .ThenBy(pool => pool.PoolId)
            .Take(_profile.MaximumActivePools + _profile.MaximumRetainedSweeps)
            .ToArray();

        return new LiquidityAnalysisSnapshot
        {
            IsEnabled = true,
            ProfileHash = _profileHash,
            SnapshotVersion = _snapshotVersion,
            AvailableAt = availableAt,
            Pools = pools,
            ActivePools = active,
            RecentEvents = _recentEvents.ToArray(),
            RecentSweeps = _recentSweeps.ToArray(),
            Quality = new LiquidityAnalysisQuality
            {
                AtrReady = true,
                ActivePoolCount = active.Length,
                SuppressedCandidateCount = suppressed,
                LastEvaluatedAt = availableAt
            }
        };
    }

    private void UpdateExistingPools(
        Candle candle,
        decimal atr,
        MarketStructureSnapshot structure,
        PriceActionSnapshot priceAction,
        long sequence,
        DateTimeOffset availableAt,
        List<LiquidityEvent> events,
        List<LiquiditySweepEvent> sweeps)
    {
        foreach (PoolRuntimeState state in _pools.Values.OrderBy(item => item.Pool.PoolId))
        {
            if (state.Terminal || state.Pool.AvailableAt >= availableAt)
                continue;

            LiquidityPool pool = state.Pool;
            int age = checked((int)Math.Max(0, sequence - state.ConfirmedSequence));
            if (_profile.MaximumPoolAgeBars is int maximumAge && age >= maximumAge)
            {
                Transition(state, LiquidityEventType.Failure, LiquidityPoolState.Expired,
                    candle.Prices.Close, availableAt, events);
                state.Terminal = true;
                continue;
            }

            bool buySide = pool.Side == LiquiditySide.BuySide;
            bool intersects = candle.Prices.High >= pool.LowerPrice && candle.Prices.Low <= pool.UpperPrice;
            decimal distance = buySide
                ? pool.LowerPrice - candle.Prices.High
                : candle.Prices.Low - pool.UpperPrice;
            if (!intersects && distance is >= 0m && distance <= atr * _profile.ApproachDistanceAtr &&
                pool.State == LiquidityPoolState.Active)
            {
                Transition(state, LiquidityEventType.Approach, LiquidityPoolState.Approached,
                    candle.Prices.Close, availableAt, events);
                pool = state.Pool;
            }

            bool penetrated = buySide
                ? candle.Prices.High > pool.UpperPrice
                : candle.Prices.Low < pool.LowerPrice;
            decimal penetration = buySide
                ? candle.Prices.High - pool.UpperPrice
                : pool.LowerPrice - candle.Prices.Low;
            decimal penetrationAtr = penetrated ? penetration / atr : 0m;
            bool closeReturned = buySide
                ? candle.Prices.Close <= pool.UpperPrice - atr * _profile.MinimumCloseBackAtr
                : candle.Prices.Close >= pool.LowerPrice + atr * _profile.MinimumCloseBackAtr;
            bool closeBeyondOriginSide = buySide
                ? candle.Prices.Close < pool.LowerPrice
                : candle.Prices.Close > pool.UpperPrice;

            if (penetrated && closeReturned &&
                penetrationAtr >= _profile.MinimumSweepPenetrationAtr &&
                penetrationAtr <= _profile.MaximumSweepPenetrationAtr)
            {
                bool displacement = HasDirectionalDisplacement(priceAction, bullish: !buySide);
                bool structureShift = HasDirectionalStructureShift(structure, priceAction, bullish: !buySide);
                if ((!_profile.RequireSweepDisplacementConfirmation || displacement) &&
                    (!_profile.RequireSweepStructureShiftConfirmation || structureShift))
                {
                    LiquidityPoolState before = state.Pool.State;
                    Transition(state, LiquidityEventType.Sweep, LiquidityPoolState.Swept,
                        candle.Prices.Close, availableAt, events);
                    decimal range = candle.Prices.High - candle.Prices.Low;
                    decimal rejection = range > 0m
                        ? buySide
                            ? (candle.Prices.High - candle.Prices.Close) / range
                            : (candle.Prices.Close - candle.Prices.Low) / range
                        : 0m;
                    Guid sweepId = DeterministicId.Create(
                        "liquidity-sweep", pool.PoolId, availableAt, candle.Prices.High,
                        candle.Prices.Low, _profileHash, _profile.RuleSetVersion);
                    sweeps.Add(new LiquiditySweepEvent
                    {
                        SweepId = sweepId,
                        PoolId = pool.PoolId,
                        SweepStartedAt = candle.OpenTime,
                        ConfirmedAt = availableAt,
                        AvailableAt = availableAt,
                        ExtremePrice = buySide ? candle.Prices.High : candle.Prices.Low,
                        PenetrationAtr = penetrationAtr,
                        ClosedBackInside = closeReturned,
                        ClosedBackBeyondOriginSide = closeBeyondOriginSide,
                        DisplacementConfirmed = displacement,
                        StructureShiftConfirmed = structureShift,
                        RejectionStrength = Math.Clamp(rejection, 0m, 1m),
                        QualityScore = Math.Clamp((state.Pool.QualityScore + rejection) / 2m, 0m, 1m),
                        SnapshotVersion = _snapshotVersion
                    });
                    state.Terminal = true;
                    state.Pool = state.Pool with { SnapshotVersion = _snapshotVersion };
                    continue;
                }
            }

            decimal closeBreakDistance = buySide
                ? candle.Prices.Close - pool.UpperPrice
                : pool.LowerPrice - candle.Prices.Close;
            bool closedBeyond = closeBreakDistance >= atr * _profile.MinimumAcceptedBreakCloseDistanceAtr;
            if (closedBeyond)
            {
                state.BreakHoldBars++;
                state.BreakStartedAt ??= candle.OpenTime;
                bool displacement = HasDirectionalDisplacement(priceAction, bullish: buySide);
                bool retest = state.BreakHoldBars > 1 && intersects;
                bool accepted = state.BreakHoldBars >= Math.Max(1, _profile.MinimumAcceptedBreakHoldBars) &&
                    (!_profile.RequireAcceptedBreakRetest || retest) &&
                    (!_profile.RequireAcceptedBreakDisplacement || displacement);
                if (accepted)
                {
                    Transition(state, LiquidityEventType.AcceptedBreak, LiquidityPoolState.AcceptedBreak,
                        candle.Prices.Close, availableAt, events);
                    state.Terminal = true;
                    continue;
                }

                AddEvent(state.Pool, LiquidityEventType.UnconfirmedPenetration,
                    state.Pool.State, state.Pool.State, candle.Prices.Close, availableAt, events);
                continue;
            }

            state.BreakHoldBars = 0;
            state.BreakStartedAt = null;
            if (penetrated)
            {
                AddEvent(state.Pool, LiquidityEventType.UnconfirmedPenetration,
                    state.Pool.State, state.Pool.State, candle.Prices.Close, availableAt, events);
            }
            else if (intersects)
            {
                LiquidityPoolState next = state.Pool.State is LiquidityPoolState.Active or LiquidityPoolState.Approached
                    ? LiquidityPoolState.Touched
                    : state.Pool.State;
                if (next != state.Pool.State)
                    Transition(state, LiquidityEventType.Touch, next, candle.Prices.Close, availableAt, events);
                else
                    AddEvent(state.Pool, LiquidityEventType.Touch, next, next, candle.Prices.Close, availableAt, events);
                state.Pool = state.Pool with { TouchCount = state.Pool.TouchCount + 1 };
            }

            decimal freshness = Math.Clamp(1m - age / 100m - state.Pool.TouchCount * 0.15m, 0m, 1m);
            state.Pool = state.Pool with
            {
                FreshnessScore = freshness,
                QualityScore = ComputeQuality(
                    state.Pool.EqualnessScore,
                    state.Pool.VisibilityScore,
                    state.Pool.CompressionScore,
                    state.Pool.ProminenceScore,
                    freshness),
                SnapshotVersion = _snapshotVersion
            };
        }
    }

    private void DetectEqualLevels(
        Candle candle,
        IReadOnlyList<SwingPoint> swings,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt,
        ref int suppressed)
    {
        foreach (SwingType type in Enum.GetValues<SwingType>())
        {
            SwingPoint[] typed = swings.Where(swing => swing.Type == type && swing.ConfirmedAt <= availableAt)
                .OrderBy(swing => swing.PivotTime).ToArray();
            for (int index = 1; index < typed.Length; index++)
            {
                SwingPoint latest = typed[index];
                SwingPoint[] sources = typed.Take(index + 1)
                    .Where(swing => Math.Abs(swing.Price - latest.Price) <= atr * _profile.EqualLevelToleranceAtr)
                    .OrderByDescending(swing => swing.PivotTime)
                    .Take(_profile.MaximumSourcePoints)
                    .OrderBy(swing => swing.PivotTime)
                    .ToArray();
                if (sources.Length < _profile.MinimumSourcePoints)
                {
                    suppressed++;
                    continue;
                }

                decimal low = sources.Min(swing => swing.Price);
                decimal high = sources.Max(swing => swing.Price);
                if (high - low > atr * _profile.MaximumPoolWidthAtr)
                {
                    suppressed++;
                    continue;
                }

                decimal reference = sources.Average(swing => swing.Price);
                decimal minimumBand = atr * Math.Min(_profile.EqualLevelToleranceAtr, _profile.MaximumPoolWidthAtr) / 2m;
                DateTimeOffset[] sourceTimes = sources.Select(swing => swing.PivotTime).ToArray();
                LiquidityPoolType poolType = type == SwingType.High
                    ? LiquidityPoolType.EqualHighs
                    : LiquidityPoolType.EqualLows;
                LiquiditySide side = type == SwingType.High
                    ? LiquiditySide.BuySide
                    : LiquiditySide.SellSide;
                if (_pools.Values.Any(state =>
                        state.Pool.Type == poolType &&
                        state.Pool.Side == side &&
                        state.Pool.SourcePivotTimes.SequenceEqual(sourceTimes)))
                {
                    continue;
                }

                AddPool(
                    candle,
                    side,
                    poolType,
                    Math.Min(low, reference - minimumBand),
                    Math.Max(high, reference + minimumBand),
                    reference,
                    sources[0].PivotTime,
                    sources.Max(swing => swing.ConfirmedAt),
                    availableAt,
                    sources.Length,
                    Equalness(low, high, atr),
                    Visibility(sources.Length),
                    Compression(sources),
                    Prominence(sources),
                    sourceTimes,
                    sequence);
            }
        }
    }

    private void DetectIsolatedSwings(
        Candle candle,
        IReadOnlyList<Candle> history,
        IReadOnlyList<SwingPoint> swings,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt,
        ref int suppressed)
    {
        foreach (SwingPoint swing in swings.Where(swing => swing.ConfirmedAt <= availableAt))
        {
            LiquidityPoolType poolType = swing.Type == SwingType.High
                ? LiquidityPoolType.SwingHigh
                : LiquidityPoolType.SwingLow;
            if (_pools.Values.Any(state =>
                    state.Pool.Type == poolType &&
                    state.Pool.SourcePivotTimes.Contains(swing.PivotTime)))
            {
                continue;
            }

            int age = history.Count(item => item.OpenTime > swing.PivotTime);
            SwingPoint? opposite = swings.Where(item => item.Type != swing.Type && item.PivotTime < swing.PivotTime)
                .OrderByDescending(item => item.PivotTime).FirstOrDefault();
            decimal prominenceAtr = Math.Abs(swing.Price - (opposite?.Price ?? candle.Prices.Close)) / atr;
            if (age < _profile.MinimumSwingAgeBars || prominenceAtr < _profile.MinimumSwingProminenceAtr)
            {
                suppressed++;
                continue;
            }

            decimal halfWidth = atr * _profile.MaximumPoolWidthAtr / 2m;
            AddPool(
                candle,
                swing.Type == SwingType.High ? LiquiditySide.BuySide : LiquiditySide.SellSide,
                poolType,
                swing.Price - halfWidth,
                swing.Price + halfWidth,
                swing.Price,
                swing.PivotTime,
                swing.ConfirmedAt,
                availableAt,
                1,
                0m,
                0.35m,
                0m,
                Math.Clamp(prominenceAtr / 3m, 0m, 1m),
                [swing.PivotTime],
                sequence);
        }
    }

    private void DetectRangeLevels(
        Candle candle,
        IReadOnlyList<Candle> history,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt,
        ref int suppressed)
    {
        if (history.Count < _profile.MinimumRangeDurationBars)
            return;
        Candle[] window = history.Skip(history.Count - _profile.MinimumRangeDurationBars).ToArray();
        decimal high = window.Max(item => item.Prices.High);
        decimal low = window.Min(item => item.Prices.Low);
        if (high - low > atr * _profile.MaximumRangeWidthAtr)
        {
            suppressed++;
            return;
        }

        decimal tolerance = atr * _profile.EqualLevelToleranceAtr;
        int highTouches = window.Count(item => high - item.Prices.High <= tolerance);
        int lowTouches = window.Count(item => item.Prices.Low - low <= tolerance);
        decimal halfWidth = Math.Min(tolerance / 2m, atr * _profile.MaximumPoolWidthAtr / 2m);
        if (highTouches >= _profile.MinimumRangeTouches)
        {
            AddPool(candle, LiquiditySide.BuySide, LiquidityPoolType.RangeHigh,
                high - halfWidth, high + halfWidth, high, window[0].OpenTime, availableAt,
                availableAt, highTouches, Equalness(high - tolerance, high, atr),
                Visibility(highTouches), 1m, 0.5m, [], sequence);
        }
        else
            suppressed++;

        if (lowTouches >= _profile.MinimumRangeTouches)
        {
            AddPool(candle, LiquiditySide.SellSide, LiquidityPoolType.RangeLow,
                low - halfWidth, low + halfWidth, low, window[0].OpenTime, availableAt,
                availableAt, lowTouches, Equalness(low, low + tolerance, atr),
                Visibility(lowTouches), 1m, 0.5m, [], sequence);
        }
        else
            suppressed++;
    }

    private void DetectCompletedPeriodLevels(
        Candle candle,
        IReadOnlyList<Candle> history,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt,
        ref int suppressed)
    {
        DateOnly currentDay = LocalDay(candle.OpenTime);
        DateOnly currentWeek = WeekStart(currentDay);
        Candle[] completedDays = history.Where(item => LocalDay(item.OpenTime) < currentDay).ToArray();
        if (completedDays.Length > 0)
        {
            DateOnly priorDay = completedDays.Max(item => LocalDay(item.OpenTime));
            Candle[] period = completedDays.Where(item => LocalDay(item.OpenTime) == priorDay).ToArray();
            if (_profile.EnablePreviousSessionLevels)
                AddPeriodPair(candle, period, LiquidityPoolType.PreviousSessionHigh,
                    LiquidityPoolType.PreviousSessionLow, atr, sequence, availableAt);
            if (_profile.EnablePreviousDayLevels)
                AddPeriodPair(candle, period, LiquidityPoolType.PreviousDayHigh,
                    LiquidityPoolType.PreviousDayLow, atr, sequence, availableAt);
        }

        Candle[] completedWeeks = history.Where(item => WeekStart(LocalDay(item.OpenTime)) < currentWeek).ToArray();
        if (_profile.EnablePreviousWeekLevels && completedWeeks.Length > 0)
        {
            DateOnly priorWeek = completedWeeks.Max(item => WeekStart(LocalDay(item.OpenTime)));
            Candle[] period = completedWeeks.Where(item => WeekStart(LocalDay(item.OpenTime)) == priorWeek).ToArray();
            AddPeriodPair(candle, period, LiquidityPoolType.PreviousWeekHigh,
                LiquidityPoolType.PreviousWeekLow, atr, sequence, availableAt);
        }
    }

    private void AddPeriodPair(
        Candle candle,
        IReadOnlyList<Candle> period,
        LiquidityPoolType highType,
        LiquidityPoolType lowType,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt)
    {
        decimal high = period.Max(item => item.Prices.High);
        decimal low = period.Min(item => item.Prices.Low);
        DateTimeOffset startedAt = period.Min(item => item.OpenTime);
        DateTimeOffset endedAt = period.Max(item => item.CloseTime ?? item.OpenTime);
        decimal halfWidth = atr * Math.Min(_profile.EqualLevelToleranceAtr, _profile.MaximumPoolWidthAtr) / 2m;
        if (!HasReferencePeriodPool(highType, startedAt, endedAt))
        {
            AddPool(candle, LiquiditySide.BuySide, highType, high - halfWidth, high + halfWidth,
                high, startedAt, endedAt, availableAt, 1, 0m, 0.65m, 0m, 0.5m, [], sequence,
                startedAt, endedAt);
        }
        if (!HasReferencePeriodPool(lowType, startedAt, endedAt))
        {
            AddPool(candle, LiquiditySide.SellSide, lowType, low - halfWidth, low + halfWidth,
                low, startedAt, endedAt, availableAt, 1, 0m, 0.65m, 0m, 0.5m, [], sequence,
                startedAt, endedAt);
        }
    }

    private void DetectRoundNumbers(
        Candle candle,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt,
        ref int suppressed)
    {
        if (!_profile.EnableRoundNumberLevels)
            return;
        foreach (decimal step in new[] { _profile.RoundNumberMajorStep, _profile.RoundNumberMinorStep }
                     .Where(value => value is > 0m).Select(value => value!.Value).Distinct())
        {
            decimal below = Math.Floor(candle.Prices.Close / step) * step;
            decimal above = below + step;
            decimal halfWidth = Math.Min(atr * _profile.MaximumPoolWidthAtr / 2m, step / 20m);
            if (!HasReferencePool(LiquiditySide.SellSide, LiquidityPoolType.RoundNumber, below))
            {
                AddPool(candle, LiquiditySide.SellSide, LiquidityPoolType.RoundNumber,
                    below - halfWidth, below + halfWidth, below, availableAt, availableAt,
                    availableAt, 1, 0m, 0.25m, 0m, 0.20m, [], sequence);
            }
            if (!HasReferencePool(LiquiditySide.BuySide, LiquidityPoolType.RoundNumber, above))
            {
                AddPool(candle, LiquiditySide.BuySide, LiquidityPoolType.RoundNumber,
                    above - halfWidth, above + halfWidth, above, availableAt, availableAt,
                    availableAt, 1, 0m, 0.25m, 0m, 0.20m, [], sequence);
            }
        }
    }

    private bool HasReferencePeriodPool(
        LiquidityPoolType type,
        DateTimeOffset periodStartedAt,
        DateTimeOffset periodEndedAt) => _pools.Values.Any(state =>
            state.Pool.Type == type &&
            state.Pool.ReferencePeriodStartedAt == periodStartedAt &&
            state.Pool.ReferencePeriodEndedAt == periodEndedAt);

    private bool HasReferencePool(LiquiditySide side, LiquidityPoolType type, decimal referencePrice) =>
        _pools.Values.Any(state =>
            state.Pool.Side == side &&
            state.Pool.Type == type &&
            state.Pool.ReferencePrice == referencePrice);

    private void AddPool(
        Candle candle,
        LiquiditySide side,
        LiquidityPoolType type,
        decimal lower,
        decimal upper,
        decimal reference,
        DateTimeOffset originatedAt,
        DateTimeOffset confirmedAt,
        DateTimeOffset availableAt,
        int sourceCount,
        decimal equalness,
        decimal visibility,
        decimal compression,
        decimal prominence,
        IReadOnlyList<DateTimeOffset> sourcePivotTimes,
        long sequence,
        DateTimeOffset? periodStartedAt = null,
        DateTimeOffset? periodEndedAt = null)
    {
        string sourceKey = string.Join(',', sourcePivotTimes.Order().Select(value => value.ToUniversalTime().ToString("O")));
        Guid id = DeterministicId.Create(
            "liquidity-pool", candle.Instrument.ToString(), candle.Interval.ToString(), _profileHash,
            type, side, originatedAt, confirmedAt, lower, upper, reference, sourceKey,
            _profile.RuleSetVersion);
        if (_pools.ContainsKey(id))
            return;

        decimal freshness = 1m;
        var pool = new LiquidityPool
        {
            PoolId = id,
            Instrument = candle.Instrument,
            Interval = candle.Interval,
            Side = side,
            Type = type,
            LowerPrice = Math.Min(lower, upper),
            UpperPrice = Math.Max(lower, upper),
            ReferencePrice = reference,
            OriginatedAt = originatedAt,
            ConfirmedAt = confirmedAt,
            AvailableAt = availableAt,
            State = LiquidityPoolState.Active,
            SourcePointCount = sourceCount,
            TouchCount = 0,
            EqualnessScore = Math.Clamp(equalness, 0m, 1m),
            VisibilityScore = Math.Clamp(visibility, 0m, 1m),
            CompressionScore = Math.Clamp(compression, 0m, 1m),
            ProminenceScore = Math.Clamp(prominence, 0m, 1m),
            FreshnessScore = freshness,
            QualityScore = ComputeQuality(equalness, visibility, compression, prominence, freshness),
            SourcePoolIds = [],
            SourcePivotTimes = sourcePivotTimes.ToArray(),
            ReferencePeriodStartedAt = periodStartedAt,
            ReferencePeriodEndedAt = periodEndedAt,
            SnapshotVersion = _snapshotVersion,
            ProfileHash = _profileHash
        };
        _pools[id] = new PoolRuntimeState { Pool = pool, ConfirmedSequence = sequence };
    }

    private void MergeOverlappingPools(DateTimeOffset availableAt, List<LiquidityEvent> events)
    {
        PoolRuntimeState[] active = _pools.Values.Where(item => !item.Terminal)
            .OrderBy(item => item.Pool.ConfirmedAt).ThenBy(item => item.Pool.PoolId).ToArray();
        for (int i = 0; i < active.Length; i++)
        {
            PoolRuntimeState survivor = active[i];
            if (survivor.Terminal)
                continue;
            for (int j = i + 1; j < active.Length; j++)
            {
                PoolRuntimeState merged = active[j];
                if (merged.Terminal || survivor.Pool.Side != merged.Pool.Side || survivor.Pool.Type != merged.Pool.Type)
                    continue;
                decimal overlap = Math.Max(0m,
                    Math.Min(survivor.Pool.UpperPrice, merged.Pool.UpperPrice) -
                    Math.Max(survivor.Pool.LowerPrice, merged.Pool.LowerPrice));
                decimal narrowest = Math.Min(
                    survivor.Pool.UpperPrice - survivor.Pool.LowerPrice,
                    merged.Pool.UpperPrice - merged.Pool.LowerPrice);
                decimal ratio = narrowest <= 0m
                    ? survivor.Pool.ReferencePrice == merged.Pool.ReferencePrice ? 1m : 0m
                    : overlap / narrowest;
                if (ratio < _profile.MergeOverlapRatio)
                    continue;

                Guid[] lineage = survivor.Pool.SourcePoolIds.Append(merged.Pool.PoolId)
                    .Concat(merged.Pool.SourcePoolIds).Distinct().Order().ToArray();
                survivor.Pool = survivor.Pool with
                {
                    LowerPrice = Math.Min(survivor.Pool.LowerPrice, merged.Pool.LowerPrice),
                    UpperPrice = Math.Max(survivor.Pool.UpperPrice, merged.Pool.UpperPrice),
                    SourcePointCount = survivor.Pool.SourcePointCount + merged.Pool.SourcePointCount,
                    SourcePoolIds = lineage,
                    SourcePivotTimes = survivor.Pool.SourcePivotTimes.Concat(merged.Pool.SourcePivotTimes)
                        .Distinct().Order().ToArray(),
                    SnapshotVersion = _snapshotVersion
                };
                AddEvent(merged.Pool, LiquidityEventType.Consumption, merged.Pool.State,
                    LiquidityPoolState.Merged, survivor.Pool.ReferencePrice, availableAt, events);
                merged.Pool = merged.Pool with { State = LiquidityPoolState.Merged, SnapshotVersion = _snapshotVersion };
                merged.Terminal = true;
            }
        }
    }

    private void PruneExcessPools(DateTimeOffset availableAt, List<LiquidityEvent> events)
    {
        PoolRuntimeState[] excess = _pools.Values.Where(item => !item.Terminal)
            .OrderByDescending(item => item.Pool.QualityScore)
            .ThenByDescending(item => item.Pool.ConfirmedAt)
            .ThenBy(item => item.Pool.PoolId)
            .Skip(_profile.MaximumActivePools).ToArray();
        foreach (PoolRuntimeState state in excess)
        {
            Transition(state, LiquidityEventType.Failure, LiquidityPoolState.Expired,
                state.Pool.ReferencePrice, availableAt, events);
            state.Terminal = true;
        }
    }

    private void Transition(
        PoolRuntimeState state,
        LiquidityEventType eventType,
        LiquidityPoolState next,
        decimal price,
        DateTimeOffset at,
        List<LiquidityEvent> events)
    {
        LiquidityPoolState before = state.Pool.State;
        state.Pool = state.Pool with { State = next, SnapshotVersion = _snapshotVersion };
        AddEvent(state.Pool, eventType, before, next, price, at, events);
    }

    private void AddEvent(
        LiquidityPool pool,
        LiquidityEventType eventType,
        LiquidityPoolState before,
        LiquidityPoolState after,
        decimal price,
        DateTimeOffset at,
        List<LiquidityEvent> events) => events.Add(new LiquidityEvent
    {
        EventId = DeterministicId.Create("liquidity-event", pool.PoolId, eventType, before, after, at, price),
        PoolId = pool.PoolId,
        EventType = eventType,
        StateBefore = before,
        StateAfter = after,
        Price = price,
        OccurredAt = at,
        AvailableAt = at,
        SnapshotVersion = _snapshotVersion
    });

    private decimal ComputeQuality(
        decimal equalness,
        decimal visibility,
        decimal compression,
        decimal prominence,
        decimal freshness)
    {
        LiquidityScoringWeights weights = _profile.ScoringWeights;
        decimal denominator = weights.Equalness + weights.Visibility + weights.Compression +
            weights.Prominence + weights.Freshness;
        if (denominator <= 0m)
            return 0m;
        return Math.Clamp((
            Math.Clamp(equalness, 0m, 1m) * weights.Equalness +
            Math.Clamp(visibility, 0m, 1m) * weights.Visibility +
            Math.Clamp(compression, 0m, 1m) * weights.Compression +
            Math.Clamp(prominence, 0m, 1m) * weights.Prominence +
            Math.Clamp(freshness, 0m, 1m) * weights.Freshness) / denominator, 0m, 1m);
    }

    private static decimal Equalness(decimal low, decimal high, decimal atr) =>
        atr <= 0m ? 0m : Math.Clamp(1m - (high - low) / atr, 0m, 1m);

    private static decimal Visibility(int count) => Math.Clamp(count / 4m, 0m, 1m);

    private static decimal Compression(IReadOnlyList<SwingPoint> sources)
    {
        if (sources.Count < 2)
            return 0m;
        double averageGap = sources.Zip(sources.Skip(1), (left, right) =>
            (right.PivotTime - left.PivotTime).TotalSeconds).Average();
        return (decimal)(1d / (1d + averageGap / 86_400d));
    }

    private static decimal Prominence(IReadOnlyList<SwingPoint> sources) =>
        sources.Count == 0
            ? 0m
            : Math.Clamp((decimal)sources.Average(item => item.Strength) / 10m, 0m, 1m);

    private DateOnly LocalDay(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, _referenceTimeZone).DateTime);

    private static DateOnly WeekStart(DateOnly date)
    {
        int days = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-days);
    }

    private static bool HasDirectionalDisplacement(PriceActionSnapshot snapshot, bool bullish) =>
        snapshot.Events.Any(item => item.Type == (bullish
            ? PriceActionEventType.BullishDisplacement
            : PriceActionEventType.BearishDisplacement));

    private static bool HasDirectionalStructureShift(
        MarketStructureSnapshot structure,
        PriceActionSnapshot priceAction,
        bool bullish) => bullish
        ? structure.Break == MarketStructureBreak.Bullish || priceAction.Events.Any(item =>
            item.Type is PriceActionEventType.BullishBreakOfStructure or PriceActionEventType.BullishChangeOfCharacter)
        : structure.Break == MarketStructureBreak.Bearish || priceAction.Events.Any(item =>
            item.Type is PriceActionEventType.BearishBreakOfStructure or PriceActionEventType.BearishChangeOfCharacter);

    private static void TrimTo<T>(List<T> items, int maximum)
    {
        if (items.Count > maximum)
            items.RemoveRange(0, items.Count - maximum);
    }

    private sealed class PoolRuntimeState
    {
        public required LiquidityPool Pool { get; set; }
        public required long ConfirmedSequence { get; init; }
        public bool Terminal { get; set; }
        public int BreakHoldBars { get; set; }
        public DateTimeOffset? BreakStartedAt { get; set; }
    }
}
