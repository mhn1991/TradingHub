using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;

namespace ChartAnnotator.SupplyDemand;

/// <summary>
/// Detects price-derived supply/demand zones from completed-candle base+departure geometry and
/// tracks their append-only lifecycle. Purely causal: every candidate is evaluated only against
/// candles up to and including the one just closed, and a zone is only ever added to
/// <see cref="SupplyDemandAnalysisSnapshot.ActiveZones"/> starting on the candle whose close
/// confirms its departure - never earlier. One instance is stateful per <c>ChartKey</c>, mirroring
/// <c>ChartAnnotator.Structure.SwingDetector</c>/<c>ChartAnnotator.PriceAction.PriceActionAnalyzer</c>.
/// </summary>
public sealed class SupplyDemandAnalyzer
{
    private readonly SupplyDemandCalculationProfile _profile;
    private readonly string _profileHash;
    private readonly Dictionary<Guid, ZoneRuntimeState> _zones = [];
    private readonly List<SupplyDemandZoneEvent> _recentEvents = [];
    /// <summary>
    /// Every zone ID ever formed, retained for the lifetime of the analyzer so a deterministic
    /// ID can never be re-detected and resurrected after its <see cref="ZoneRuntimeState"/> is
    /// trimmed from <see cref="_zones"/> below. Cheap (a Guid per zone, no zone payload) compared
    /// to keeping the full runtime state around forever.
    /// </summary>
    private readonly HashSet<Guid> _formedZoneIds = [];
    /// <summary>
    /// Zone IDs in creation order (== AvailableAt order - <see cref="DetectNewZone"/> only ever
    /// stamps a new zone with the current candle's timestamp and adds at most one per call).
    /// Lets <see cref="TrimStaleTerminalZones"/> find "oldest first" without re-sorting the whole
    /// zone dictionary every candle. A <see cref="LinkedList{T}"/> rather than a <see cref="Queue{T}"/>
    /// deliberately - trimming must be able to remove a terminal entry from the *middle* of this
    /// order without one long-lived active zone sitting near the front permanently blocking every
    /// terminal entry behind it (a real bug this analyzer's first cut of this fix had: a plain
    /// FIFO queue gives up the instant it sees one still-active zone, so a single long-lived
    /// active zone lets everything behind it accumulate forever).
    /// </summary>
    private readonly LinkedList<Guid> _creationOrder = [];
    private long _snapshotVersion;

    public SupplyDemandAnalyzer(SupplyDemandCalculationProfile? profile = null)
    {
        _profile = profile ?? new SupplyDemandCalculationProfile();
        _profile.Validate();
        _profileHash = SupplyDemandCalculationProfileHasher.ComputeHash(_profile);
    }

    public SupplyDemandAnalysisSnapshot Update(
        Candle currentCandle,
        IReadOnlyList<Candle> candleHistory,
        IReadOnlyList<SwingPoint> confirmedSwings,
        decimal? atr,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(currentCandle);
        ArgumentNullException.ThrowIfNull(candleHistory);
        ArgumentNullException.ThrowIfNull(confirmedSwings);

        DateTimeOffset availableAt = currentCandle.CloseTime ?? currentCandle.OpenTime;
        _snapshotVersion++;

        if (!_profile.Enabled || atr is not > 0m)
        {
            return SupplyDemandAnalysisSnapshot.Disabled with
            {
                ProfileHash = _profileHash,
                SnapshotVersion = _snapshotVersion,
                AvailableAt = availableAt
            };
        }

        decimal atrValue = atr.Value;
        var newEvents = new List<SupplyDemandZoneEvent>();
        int suppressedCandidates = 0;

        UpdateExistingZones(currentCandle, availableAt, sequence, newEvents);
        DetectNewZone(currentCandle, candleHistory, confirmedSwings, atrValue, sequence, availableAt, newEvents, ref suppressedCandidates);
        MergeOverlappingZones(availableAt, sequence, newEvents);
        PruneExcessZones(availableAt, newEvents);

        _recentEvents.AddRange(newEvents);
        if (_recentEvents.Count > _profile.MaximumRetainedEvents)
        {
            _recentEvents.RemoveRange(0, _recentEvents.Count - _profile.MaximumRetainedEvents);
        }

        // Must run after _recentEvents has already received this candle's additions and been
        // trimmed - see TrimStaleTerminalZones's own doc comment for why (same defect as, and
        // fixed alongside, LiquidityAnalyzer.TrimStaleTerminalPools).
        TrimStaleTerminalZones();

        SupplyDemandZone[] active = _zones.Values
            .Where(state => !state.Terminal)
            .Select(state => state.Zone)
            .OrderBy(zone => zone.Type)
            .ThenBy(zone => zone.ConfirmedAt)
            .ThenBy(zone => zone.ZoneId)
            .ToArray();
        SupplyDemandZone[] zones = _zones.Values
            .Select(state => state.Zone)
            .OrderByDescending(zone => zone.AvailableAt)
            .ThenBy(zone => zone.ZoneId)
            .Take(_profile.MaximumActiveZones + _profile.MaximumRetainedEvents)
            .ToArray();

        return new SupplyDemandAnalysisSnapshot
        {
            IsEnabled = true,
            ProfileHash = _profileHash,
            SnapshotVersion = _snapshotVersion,
            AvailableAt = availableAt,
            Zones = zones,
            ActiveZones = active,
            RecentEvents = _recentEvents.ToArray(),
            Quality = new SupplyDemandAnalysisQuality
            {
                AtrReady = true,
                ActiveZoneCount = active.Length,
                SuppressedCandidateCount = suppressedCandidates,
                LastEvaluatedAt = availableAt
            }
        };
    }

    // ----- Lifecycle -----------------------------------------------------

    private void UpdateExistingZones(
        Candle candle,
        DateTimeOffset availableAt,
        long sequence,
        List<SupplyDemandZoneEvent> events)
    {
        foreach (ZoneRuntimeState state in _zones.Values)
        {
            if (state.Terminal)
            {
                continue;
            }

            SupplyDemandZone zone = state.Zone;
            decimal lower = Math.Min(zone.ProximalPrice, zone.DistalPrice);
            decimal upper = Math.Max(zone.ProximalPrice, zone.DistalPrice);
            decimal height = upper - lower;

            bool touchedNow = candle.Prices.Low <= upper && candle.Prices.High >= lower;
            bool reentered = touchedNow && !state.WasTouching;
            state.WasTouching = touchedNow;
            if (touchedNow)
            {
                SupplyDemandZoneState before = zone.State;
                // Cooldown-based distinct touch count (industry-standard technique): only counts
                // toward DistinctTouchCount if at least MinimumDistinctTouchBars have passed since
                // the last counted touch, so one consolidation sitting on the zone across many
                // consecutive candles cannot be mistaken for repeated separate pullback tests.
                // Deliberately independent of TouchCount, which keeps its existing (unthrottled)
                // meaning for FreshnessScore/QualityScore elsewhere.
                TimeSpan cooldown = TimeSpan.FromSeconds(
                    BarIntervalParser.ApproximateSeconds(zone.Interval) * _profile.MinimumDistinctTouchBars);
                bool distinctTouch = reentered &&
                    (zone.LastDistinctTouchAt is not DateTimeOffset lastDistinct ||
                        availableAt - lastDistinct >= cooldown);
                zone = zone with
                {
                    TouchCount = zone.TouchCount + 1,
                    DistinctTouchCount = distinctTouch ? zone.DistinctTouchCount + 1 : zone.DistinctTouchCount,
                    LastDistinctTouchAt = distinctTouch ? availableAt : zone.LastDistinctTouchAt
                };
                if (distinctTouch)
                {
                    if (before is SupplyDemandZoneState.ConfirmedFresh or SupplyDemandZoneState.Approached)
                        zone = zone with { State = SupplyDemandZoneState.Tested };

                    events.Add(CreateEvent(zone.ZoneId, SupplyDemandZoneEventType.Touched, before, zone.State, candle.Prices.Close, availableAt));
                }

                decimal penetrationExtent = zone.Type == SupplyDemandZoneType.Demand
                    ? upper - candle.Prices.Low
                    : candle.Prices.High - lower;
                decimal penetrationRatio = height > 0m ? Math.Clamp(penetrationExtent / height, 0m, 1m) : 0m;
                if (penetrationRatio > state.MaxPenetrationRatio)
                {
                    state.MaxPenetrationRatio = penetrationRatio;
                }

                zone = zone with { PenetrationRatio = state.MaxPenetrationRatio };
            }
            else if (zone.State == SupplyDemandZoneState.ConfirmedFresh)
            {
                // Distance is expressed as a multiple of the zone's own height rather than a raw
                // ATR value (no ATR parameter is threaded into lifecycle updates) - the zone's
                // height is itself ATR-bounded at formation time, keeping this deterministic.
                decimal distanceFromProximal = zone.Type == SupplyDemandZoneType.Demand
                    ? candle.Prices.Close - upper
                    : lower - candle.Prices.Close;
                decimal distanceInHeights = height > 0m ? Math.Abs(distanceFromProximal) / height : decimal.MaxValue;
                if (distanceFromProximal >= 0m && distanceInHeights <= _profile.ApproachDistanceAtr)
                {
                    SupplyDemandZoneState before = zone.State;
                    zone = zone with { State = SupplyDemandZoneState.Approached };
                    events.Add(CreateEvent(zone.ZoneId, SupplyDemandZoneEventType.Approached, before, zone.State, candle.Prices.Close, availableAt));
                }
            }

            // QualityScore was previously computed once at formation and never revisited, so the
            // TouchPenalty/PenetrationPenalty/AgePenalty weights ComputeQualityScore already
            // accepts parameters for were dead in practice - every consumer (zone-quality gates,
            // TargetMapBuilder's tiering, StructureBasedTradeManager's stop-trail candidate
            // scoring) always saw a zone's day-one quality no matter how stale or heavily-tested
            // it had since become, even though TouchCount/DistinctTouchCount/PenetrationRatio/State
            // were all correctly tracked live. Mirrors LiquidityPool's own live-recompute pattern
            // (see LiquidityAnalyzer.UpdateExistingPools). DistinctTouchCount (cooldown-throttled),
            // not raw TouchCount, for the same reason the zone-touch gates already prefer it.
            int ageBars = checked((int)Math.Max(0, sequence - state.ConfirmedAtSequence));
            zone = zone with
            {
                QualityScore = ComputeQualityScore(
                    zone.DepartureAtr, zone.DepartureEfficiency, zone.BaseCompactness, zone.BaseCandleCount,
                    zone.BrokeStructure, zone.HasFairValueGap, zone.FreshnessScore,
                    touchCount: zone.DistinctTouchCount, penetrationRatio: zone.PenetrationRatio, ageBars)
            };
            state.Zone = zone;

            bool invalidated = IsInvalidated(zone, candle, lower, upper);
            if (invalidated)
            {
                Terminate(state, SupplyDemandZoneState.Invalidated, SupplyDemandZoneEventType.Invalidated, candle.Prices.Close, availableAt, events);
                continue;
            }

            if (state.MaxPenetrationRatio >= 1.0m && zone.State != SupplyDemandZoneState.Mitigated)
            {
                // Mitigated stays visible (dashed/faded per blueprint §15) - only Invalidated,
                // Expired, and Merged actually remove a zone from ActiveZones.
                Terminate(state, SupplyDemandZoneState.Mitigated, SupplyDemandZoneEventType.Mitigated, candle.Prices.Close, availableAt, events, terminal: false);
                continue;
            }

            if (state.MaxPenetrationRatio >= _profile.PartialMitigationPenetrationRatio &&
                zone.State is not (SupplyDemandZoneState.PartiallyMitigated or SupplyDemandZoneState.Mitigated))
            {
                SupplyDemandZoneState before = zone.State;
                state.Zone = zone with { State = SupplyDemandZoneState.PartiallyMitigated };
                events.Add(CreateEvent(zone.ZoneId, SupplyDemandZoneEventType.PartiallyMitigated, before, state.Zone.State, candle.Prices.Close, availableAt));
                continue;
            }

            if (_profile.MaximumZoneAgeBars is int maxAge && sequence - state.ConfirmedAtSequence >= maxAge)
            {
                Terminate(state, SupplyDemandZoneState.Expired, SupplyDemandZoneEventType.Expired, candle.Prices.Close, availableAt, events);
            }
        }
    }

    private bool IsInvalidated(SupplyDemandZone zone, Candle candle, decimal lower, decimal upper)
    {
        return _profile.InvalidationMode switch
        {
            ZoneInvalidationMode.CloseBeyondDistal => zone.Type == SupplyDemandZoneType.Demand
                ? candle.Prices.Close < lower
                : candle.Prices.Close > upper,
            ZoneInvalidationMode.WickBeyondDistal => zone.Type == SupplyDemandZoneType.Demand
                ? candle.Prices.Low < lower
                : candle.Prices.High > upper,
            ZoneInvalidationMode.PenetrationThreshold =>
                _zones.TryGetValue(zone.ZoneId, out ZoneRuntimeState? state) &&
                state.MaxPenetrationRatio >= _profile.InvalidationPenetrationRatio,
            _ => false
        };
    }

    private void Terminate(
        ZoneRuntimeState state,
        SupplyDemandZoneState newState,
        SupplyDemandZoneEventType eventType,
        decimal price,
        DateTimeOffset availableAt,
        List<SupplyDemandZoneEvent> events,
        bool terminal = true)
    {
        SupplyDemandZoneState before = state.Zone.State;
        state.Zone = state.Zone with { State = newState };
        state.Terminal = terminal;
        events.Add(CreateEvent(state.Zone.ZoneId, eventType, before, newState, price, availableAt));
    }

    // ----- Detection -------------------------------------------------------

    private void DetectNewZone(
        Candle currentCandle,
        IReadOnlyList<Candle> candleHistory,
        IReadOnlyList<SwingPoint> confirmedSwings,
        decimal atr,
        long sequence,
        DateTimeOffset availableAt,
        List<SupplyDemandZoneEvent> events,
        ref int suppressedCandidates)
    {
        Candle[] history = candleHistory as Candle[] ?? candleHistory.ToArray();
        int count = history.Length;
        if (count == 0 || history[^1].Prices != currentCandle.Prices)
        {
            // The caller contract (mirroring PriceActionAnalyzer.Update) always passes a history
            // snapshot that already ends with currentCandle - defensive no-op otherwise.
            return;
        }

        for (int departureLen = 1; departureLen <= _profile.MaximumDepartureCandles; departureLen++)
        {
            int departureStartIndex = count - departureLen;
            if (departureStartIndex < _profile.MinimumBaseCandles)
            {
                break;
            }

            var departureCandles = new ArraySegment<Candle>(history, departureStartIndex, departureLen);
            decimal netMove = departureCandles[^1].Prices.Close - departureCandles[0].Prices.Open;
            if (netMove == 0m)
            {
                suppressedCandidates++;
                continue;
            }

            decimal sumRange = 0m;
            decimal sumBody = 0m;
            foreach (Candle candle in departureCandles)
            {
                sumRange += candle.Prices.High - candle.Prices.Low;
                sumBody += Math.Abs(candle.Prices.Close - candle.Prices.Open);
            }

            if (sumRange <= 0m)
            {
                suppressedCandidates++;
                continue;
            }

            decimal departureEfficiency = Math.Abs(netMove) / sumRange;
            decimal departureAtr = Math.Abs(netMove) / atr;
            decimal directionalBodyRatio = sumBody / sumRange;

            bool bullish = netMove > 0m;

            if (departureAtr < _profile.MinimumDepartureAtr ||
                departureEfficiency < _profile.MinimumDepartureEfficiency ||
                directionalBodyRatio < _profile.MinimumDirectionalBodyRatio)
            {
                suppressedCandidates++;
                continue;
            }

            for (int baseLen = _profile.MinimumBaseCandles; baseLen <= _profile.MaximumBaseCandles; baseLen++)
            {
                int baseStartIndex = departureStartIndex - baseLen;
                if (baseStartIndex < 0)
                {
                    break;
                }

                var baseCandles = new ArraySegment<Candle>(history, baseStartIndex, baseLen);
                if (!TryEvaluateBase(baseCandles, atr, out decimal baseCompactness))
                {
                    suppressedCandidates++;
                    continue;
                }

                bool brokeStructure = DetectStructureBreak(bullish, departureCandles, confirmedSwings, baseCandles[0].OpenTime);
                bool hasFairValueGap = DetectFairValueGap(bullish, departureCandles);

                if (_profile.RequireStructureBreak && !brokeStructure)
                {
                    suppressedCandidates++;
                    continue;
                }

                if (_profile.RequireFairValueGap && !hasFairValueGap)
                {
                    suppressedCandidates++;
                    continue;
                }

                if (_profile.RequireOriginationMove && !TryEvaluateOrigination(history, baseStartIndex, bullish, atr))
                {
                    suppressedCandidates++;
                    continue;
                }

                DateTimeOffset baseStartedAt = baseCandles[0].OpenTime;
                DateTimeOffset baseEndedAt = baseCandles[^1].CloseTime ?? baseCandles[^1].OpenTime;
                DateTimeOffset departureStartedAt = departureCandles[0].OpenTime;

                if (OverlapsExistingActiveZone(bullish ? SupplyDemandZoneType.Demand : SupplyDemandZoneType.Supply, baseStartedAt, availableAt))
                {
                    suppressedCandidates++;
                    continue;
                }

                SupplyDemandPattern pattern = ClassifyPattern(bullish, confirmedSwings, baseStartedAt);
                SupplyDemandZoneType type = bullish ? SupplyDemandZoneType.Demand : SupplyDemandZoneType.Supply;

                (decimal proximal, decimal distal) = ComputeBoundaries(type, baseCandles);

                Guid zoneId = DeterministicId.Create(
                    "sdzone",
                    currentCandle.Instrument.ToString(),
                    currentCandle.Interval.ToString(),
                    type,
                    pattern,
                    baseStartedAt,
                    departureStartedAt,
                    proximal,
                    distal,
                    _profileHash);

                if (_formedZoneIds.Contains(zoneId))
                {
                    suppressedCandidates++;
                    continue;
                }

                decimal imbalanceRatio = directionalBodyRatio;
                decimal freshnessScore = 1.0m;
                decimal qualityScore = ComputeQualityScore(
                    departureAtr, departureEfficiency, baseCompactness, baseLen,
                    brokeStructure, hasFairValueGap, freshnessScore, touchCount: 0, penetrationRatio: 0m,
                    ageBars: 0);

                var zone = new SupplyDemandZone
                {
                    ZoneId = zoneId,
                    Instrument = currentCandle.Instrument,
                    Interval = currentCandle.Interval,
                    Type = type,
                    Pattern = pattern,
                    ProximalPrice = proximal,
                    DistalPrice = distal,
                    BaseStartedAt = baseStartedAt,
                    BaseEndedAt = baseEndedAt,
                    DepartureStartedAt = departureStartedAt,
                    ConfirmedAt = availableAt,
                    AvailableAt = availableAt,
                    State = SupplyDemandZoneState.ConfirmedFresh,
                    BaseCandleCount = baseLen,
                    TouchCount = 0,
                    DepartureAtr = departureAtr,
                    DepartureEfficiency = departureEfficiency,
                    BaseCompactness = baseCompactness,
                    ImbalanceRatio = imbalanceRatio,
                    PenetrationRatio = 0m,
                    FreshnessScore = freshnessScore,
                    QualityScore = qualityScore,
                    BrokeStructure = brokeStructure,
                    HasFairValueGap = hasFairValueGap,
                    BoundaryMode = _profile.BoundaryMode,
                    SourceZoneIds = [],
                    SnapshotVersion = _snapshotVersion,
                    ProfileHash = _profileHash
                };

                _zones[zoneId] = new ZoneRuntimeState
                {
                    Zone = zone,
                    ConfirmedAtSequence = sequence,
                    MaxPenetrationRatio = 0m,
                    // Formation can occur while the final departure candle still overlaps the
                    // base. Do not misclassify the next overlapping candle as a fresh revisit;
                    // a distinct touch requires price to leave the zone first.
                    WasTouching = currentCandle.Prices.Low <= Math.Max(proximal, distal) &&
                        currentCandle.Prices.High >= Math.Min(proximal, distal),
                    Terminal = false
                };
                _formedZoneIds.Add(zoneId);
                _creationOrder.AddLast(zoneId);
                events.Add(CreateEvent(zoneId, SupplyDemandZoneEventType.Formed, SupplyDemandZoneState.Forming, SupplyDemandZoneState.ConfirmedFresh, availableAt: availableAt, price: currentCandle.Prices.Close));
                events.Add(CreateEvent(zoneId, SupplyDemandZoneEventType.Confirmed, SupplyDemandZoneState.ConfirmedFresh, SupplyDemandZoneState.ConfirmedFresh, availableAt: availableAt, price: currentCandle.Prices.Close));
                return;
            }
        }
    }

    private bool TryEvaluateBase(ArraySegment<Candle> baseCandles, decimal atr, out decimal compactness)
    {
        compactness = 0m;
        decimal baseHigh = decimal.MinValue;
        decimal baseLow = decimal.MaxValue;
        decimal sumBody = 0m;
        decimal sumRange = 0m;
        foreach (Candle candle in baseCandles)
        {
            baseHigh = Math.Max(baseHigh, candle.Prices.High);
            baseLow = Math.Min(baseLow, candle.Prices.Low);
            sumBody += Math.Abs(candle.Prices.Close - candle.Prices.Open);
            sumRange += candle.Prices.High - candle.Prices.Low;
        }

        decimal baseRange = baseHigh - baseLow;
        if (baseRange <= 0m)
        {
            return false;
        }

        decimal baseRangeAtr = baseRange / atr;
        if (baseRangeAtr > _profile.MaximumBaseRangeAtr)
        {
            return false;
        }

        decimal avgBodyAtr = (sumBody / baseCandles.Count) / atr;
        if (avgBodyAtr > _profile.MaximumAverageBaseBodyAtr)
        {
            return false;
        }

        decimal baseNetMove = baseCandles[^1].Prices.Close - baseCandles[0].Prices.Open;
        decimal baseEfficiency = sumRange > 0m ? Math.Abs(baseNetMove) / sumRange : 0m;
        if (baseEfficiency > _profile.MaximumBaseEfficiencyRatio)
        {
            return false;
        }

        if (baseCandles.Count >= 2)
        {
            decimal overlapSum = 0m;
            int pairs = 0;
            for (int index = 0; index < baseCandles.Count - 1; index++)
            {
                Candle first = baseCandles[index];
                Candle second = baseCandles[index + 1];
                decimal overlap = Math.Max(0m, Math.Min(first.Prices.High, second.Prices.High) - Math.Max(first.Prices.Low, second.Prices.Low));
                decimal smallerRange = Math.Min(first.Prices.High - first.Prices.Low, second.Prices.High - second.Prices.Low);
                if (smallerRange > 0m)
                {
                    overlapSum += overlap / smallerRange;
                    pairs++;
                }
            }

            decimal overlapRatio = pairs > 0 ? overlapSum / pairs : 1m;
            if (overlapRatio < _profile.MinimumCandleOverlapRatio)
            {
                return false;
            }
        }

        compactness = Math.Clamp(1m - baseRangeAtr / _profile.MaximumBaseRangeAtr, 0m, 1m);
        return true;
    }

    /// <summary>
    /// Opt-in check (RequireOriginationMove) for the "strong move in" leg of the standard
    /// Drop-Base-Rally/Rally-Base-Drop pattern: a qualifying move ending exactly where the base
    /// begins, in the OPPOSITE direction from the eventual departure (a demand zone's base should
    /// be approached by a prior bearish move, mirroring the departure move's own ATR/efficiency
    /// checks). Without this, a base reached by slow drift scores identically to one reached by a
    /// genuine prior move, even though only the latter is what the standard definition means by
    /// "imbalance origin".
    /// </summary>
    private bool TryEvaluateOrigination(Candle[] history, int baseStartIndex, bool bullishDeparture, decimal atr)
    {
        for (int originLen = _profile.MinimumOriginationCandles; originLen <= _profile.MaximumOriginationCandles; originLen++)
        {
            int originStartIndex = baseStartIndex - originLen;
            if (originStartIndex < 0)
            {
                break;
            }

            var originCandles = new ArraySegment<Candle>(history, originStartIndex, originLen);
            decimal netMove = originCandles[^1].Prices.Close - originCandles[0].Prices.Open;
            bool matchesExpectedDirection = bullishDeparture ? netMove < 0m : netMove > 0m;
            if (!matchesExpectedDirection)
            {
                continue;
            }

            decimal sumRange = 0m;
            foreach (Candle candle in originCandles)
            {
                sumRange += candle.Prices.High - candle.Prices.Low;
            }

            if (sumRange <= 0m)
            {
                continue;
            }

            decimal originationEfficiency = Math.Abs(netMove) / sumRange;
            decimal originationAtr = Math.Abs(netMove) / atr;
            if (originationAtr >= _profile.MinimumOriginationAtr &&
                originationEfficiency >= _profile.MinimumOriginationEfficiency)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DetectStructureBreak(
        bool bullish,
        ArraySegment<Candle> departureCandles,
        IReadOnlyList<SwingPoint> confirmedSwings,
        DateTimeOffset baseStartedAt)
    {
        SwingType targetType = bullish ? SwingType.High : SwingType.Low;
        SwingPoint? priorSwing = confirmedSwings
            .Where(swing => swing.Type == targetType && swing.PivotTime < baseStartedAt)
            .OrderByDescending(swing => swing.PivotTime)
            .FirstOrDefault();
        if (priorSwing is null)
        {
            return false;
        }

        foreach (Candle candle in departureCandles)
        {
            if (bullish && candle.Prices.Close > priorSwing.Price)
            {
                return true;
            }

            if (!bullish && candle.Prices.Close < priorSwing.Price)
            {
                return true;
            }
        }

        return false;
    }

    private static bool DetectFairValueGap(bool bullish, ArraySegment<Candle> departureCandles)
    {
        if (departureCandles.Count < 3)
        {
            return false;
        }

        for (int index = 1; index < departureCandles.Count - 1; index++)
        {
            Candle before = departureCandles[index - 1];
            Candle after = departureCandles[index + 1];
            if (bullish && before.Prices.High < after.Prices.Low)
            {
                return true;
            }

            if (!bullish && before.Prices.Low > after.Prices.High)
            {
                return true;
            }
        }

        return false;
    }

    private static SupplyDemandPattern ClassifyPattern(
        bool bullishDeparture,
        IReadOnlyList<SwingPoint> confirmedSwings,
        DateTimeOffset baseStartedAt)
    {
        SwingPoint? priorSwing = confirmedSwings
            .Where(swing => swing.PivotTime < baseStartedAt)
            .OrderByDescending(swing => swing.PivotTime)
            .FirstOrDefault();

        // No prior swing history yet: default to the continuation pattern (RBR/DBD), the most
        // common baseline case, rather than blocking zone formation early in a stream.
        bool enteredFromDrop = priorSwing?.Type == SwingType.High;
        bool enteredFromRally = priorSwing?.Type == SwingType.Low;

        return (bullishDeparture, enteredFromDrop, enteredFromRally) switch
        {
            (true, true, _) => SupplyDemandPattern.DropBaseRally,
            (true, _, _) => SupplyDemandPattern.RallyBaseRally,
            (false, _, true) => SupplyDemandPattern.RallyBaseDrop,
            (false, _, _) => SupplyDemandPattern.DropBaseDrop
        };
    }

    private (decimal proximal, decimal distal) ComputeBoundaries(SupplyDemandZoneType type, ArraySegment<Candle> baseCandles)
    {
        decimal baseHigh = baseCandles.Max(c => c.Prices.High);
        decimal baseLow = baseCandles.Min(c => c.Prices.Low);
        Candle originCandle = baseCandles[^1];
        decimal originBodyTop = Math.Max(originCandle.Prices.Open, originCandle.Prices.Close);
        decimal originBodyBottom = Math.Min(originCandle.Prices.Open, originCandle.Prices.Close);
        decimal bodyTopAcrossBase = baseCandles.Max(c => Math.Max(c.Prices.Open, c.Prices.Close));
        decimal bodyBottomAcrossBase = baseCandles.Min(c => Math.Min(c.Prices.Open, c.Prices.Close));

        return (type, _profile.BoundaryMode) switch
        {
            (SupplyDemandZoneType.Demand, ZoneBoundaryMode.FullWickRange) => (baseHigh, baseLow),
            (SupplyDemandZoneType.Supply, ZoneBoundaryMode.FullWickRange) => (baseLow, baseHigh),
            (SupplyDemandZoneType.Demand, ZoneBoundaryMode.BodyToExtreme) => (bodyTopAcrossBase, baseLow),
            (SupplyDemandZoneType.Supply, ZoneBoundaryMode.BodyToExtreme) => (bodyBottomAcrossBase, baseHigh),
            (SupplyDemandZoneType.Demand, ZoneBoundaryMode.DepartureOriginBody) => (originBodyTop, originBodyBottom),
            (SupplyDemandZoneType.Supply, ZoneBoundaryMode.DepartureOriginBody) => (originBodyBottom, originBodyTop),
            _ => (baseHigh, baseLow)
        };
    }

    private bool OverlapsExistingActiveZone(SupplyDemandZoneType type, DateTimeOffset baseStartedAt, DateTimeOffset candidateEnd)
    {
        foreach (ZoneRuntimeState state in _zones.Values)
        {
            if (state.Terminal || state.Zone.Type != type)
            {
                continue;
            }

            if (state.Zone.BaseStartedAt <= candidateEnd && state.Zone.ConfirmedAt >= baseStartedAt)
            {
                return true;
            }
        }

        return false;
    }

    // ----- Merge / bounding --------------------------------------------

    private void MergeOverlappingZones(DateTimeOffset availableAt, long sequence, List<SupplyDemandZoneEvent> events)
    {
        List<ZoneRuntimeState> candidates = _zones.Values
            .Where(state => !state.Terminal)
            .OrderBy(state => state.Zone.ConfirmedAt)
            .ThenBy(state => state.Zone.ZoneId)
            .ToList();

        for (int i = 0; i < candidates.Count; i++)
        {
            ZoneRuntimeState a = candidates[i];
            if (a.Terminal)
            {
                continue;
            }

            for (int j = i + 1; j < candidates.Count; j++)
            {
                ZoneRuntimeState b = candidates[j];
                if (b.Terminal || a.Zone.Type != b.Zone.Type)
                {
                    continue;
                }

                decimal aLower = Math.Min(a.Zone.ProximalPrice, a.Zone.DistalPrice);
                decimal aUpper = Math.Max(a.Zone.ProximalPrice, a.Zone.DistalPrice);
                decimal bLower = Math.Min(b.Zone.ProximalPrice, b.Zone.DistalPrice);
                decimal bUpper = Math.Max(b.Zone.ProximalPrice, b.Zone.DistalPrice);

                // Nesting requires strict, asymmetric containment (one zone genuinely smaller and
                // fully inside the other). Equal or near-equal bands are "overlapping", not
                // "nested" - falling through to the overlap-ratio check below, where a heavily
                // overlapping (in the extreme, identical) same-side pair correctly merges even
                // when AllowNestedSameSideZones is true.
                bool aStrictlyContainsB = aLower <= bLower && aUpper >= bUpper && (aLower < bLower || aUpper > bUpper);
                bool bStrictlyContainsA = bLower <= aLower && bUpper >= aUpper && (bLower < aLower || bUpper > aUpper);
                if (_profile.AllowNestedSameSideZones && (aStrictlyContainsB || bStrictlyContainsA))
                {
                    continue;
                }

                decimal overlap = Math.Max(0m, Math.Min(aUpper, bUpper) - Math.Max(aLower, bLower));
                decimal aHeight = aUpper - aLower;
                decimal bHeight = bUpper - bLower;
                decimal smaller = Math.Min(aHeight, bHeight);
                if (smaller <= 0m || overlap / smaller < _profile.MergeOverlapRatio)
                {
                    continue;
                }

                ZoneRuntimeState survivor = a.Zone.QualityScore >= b.Zone.QualityScore ? a : b;
                ZoneRuntimeState loser = ReferenceEquals(survivor, a) ? b : a;

                if (survivor.Zone.SourceZoneIds.Count < _profile.MaximumMergedSourceCount)
                {
                    survivor.Zone = survivor.Zone with
                    {
                        SourceZoneIds = [.. survivor.Zone.SourceZoneIds, loser.Zone.ZoneId]
                    };
                }

                SupplyDemandZoneState before = loser.Zone.State;
                loser.Zone = loser.Zone with { State = SupplyDemandZoneState.Merged };
                loser.Terminal = true;
                events.Add(CreateEvent(loser.Zone.ZoneId, SupplyDemandZoneEventType.Merged, before, SupplyDemandZoneState.Merged, loser.Zone.ProximalPrice, availableAt) with
                {
                    RelatedZoneId = survivor.Zone.ZoneId
                });
            }
        }
    }

    private void PruneExcessZones(DateTimeOffset availableAt, List<SupplyDemandZoneEvent> events)
    {
        List<ZoneRuntimeState> active = _zones.Values.Where(state => !state.Terminal).ToList();
        if (active.Count <= _profile.MaximumActiveZones)
        {
            return;
        }

        // Deterministic, profitability-unrelated pruning: lowest quality first, ties broken by
        // oldest-first (age), then ZoneId for total determinism.
        IEnumerable<ZoneRuntimeState> toPrune = active
            .OrderBy(state => state.Zone.QualityScore)
            .ThenBy(state => state.Zone.ConfirmedAt)
            .ThenBy(state => state.Zone.ZoneId)
            .Take(active.Count - _profile.MaximumActiveZones);

        foreach (ZoneRuntimeState state in toPrune)
        {
            SupplyDemandZoneState before = state.Zone.State;
            state.Zone = state.Zone with { State = SupplyDemandZoneState.Expired };
            state.Terminal = true;
            events.Add(CreateEvent(state.Zone.ZoneId, SupplyDemandZoneEventType.Expired, before, SupplyDemandZoneState.Expired, state.Zone.ProximalPrice, availableAt));
        }
    }

    /// <summary>
    /// Bounds <see cref="_zones"/> to roughly <see cref="SupplyDemandCalculationProfile.MaximumActiveZones"/>
    /// + <see cref="SupplyDemandCalculationProfile.MaximumRetainedEvents"/> entries so per-candle
    /// full-dictionary scans (<see cref="UpdateExistingZones"/>, <see cref="OverlapsExistingActiveZone"/>,
    /// <see cref="MergeOverlappingZones"/>, and the snapshot builder in <see cref="Update"/>) stay
    /// O(bounded constant) instead of O(every zone ever formed in the run) - without that, a long
    /// backtest degrades to roughly quadratic total work as zones accumulate. Only terminal zones
    /// older (by creation order, which matches AvailableAt order exactly) than the window the
    /// snapshot's <c>Zones</c> output ever needs are removed; <see cref="_formedZoneIds"/> keeps
    /// every ID forever so a trimmed zone's deterministic ID can never be re-detected and
    /// resurrected (the append-only lifecycle guarantee is unaffected).
    ///
    /// Must be called after <see cref="_recentEvents"/> has already received this candle's
    /// additions and been trimmed to its own retention window - and must never remove a zone that
    /// list still references. A zone that just went Terminal via an event produced THIS candle is
    /// often also the OLDEST entry in <see cref="_creationOrder"/> (it sat around for a long time
    /// before finally invalidating/mitigating), making it the first candidate this walk considers
    /// for removal - if removed here, the event just recorded for it would permanently reference a
    /// zone ID no longer present in the snapshot's <c>Zones</c> list. This is the same defect found
    /// and fixed in <c>LiquidityAnalyzer.TrimStaleTerminalPools</c> (there it silently broke the
    /// sweep-reversal playbook's sweep-to-pool cross-reference forever, once the pool backlog first
    /// exceeded the retention window); fixed here for the same data-integrity reason even though no
    /// zone consumer currently cross-references events back to zones the same way.
    /// </summary>
    private void TrimStaleTerminalZones()
    {
        int excess = _creationOrder.Count - (_profile.MaximumActiveZones + _profile.MaximumRetainedEvents);
        if (excess <= 0)
        {
            return;
        }

        var protectedIds = new HashSet<Guid>(_recentEvents.Select(item => item.ZoneId));

        // Walk oldest-to-newest, removing terminal entries wherever they are - not just at the
        // front - so one long-lived active zone can never block cleanup of everything behind it.
        // Active entries and zones still referenced by a retained event are skipped in place (never
        // removed here); the number skipped is bounded by MaximumActiveZones + MaximumRetainedEvents
        // regardless of total run length, so this stays a bounded walk.
        LinkedListNode<Guid>? node = _creationOrder.First;
        int removed = 0;
        while (node is not null && removed < excess)
        {
            LinkedListNode<Guid>? next = node.Next;
            if (protectedIds.Contains(node.Value))
            {
                node = next;
                continue;
            }
            if (_zones.TryGetValue(node.Value, out ZoneRuntimeState? state) && state.Terminal)
            {
                _zones.Remove(node.Value);
                _creationOrder.Remove(node);
                removed++;
            }

            node = next;
        }
    }

    private decimal ComputeQualityScore(
        decimal departureAtr,
        decimal departureEfficiency,
        decimal baseCompactness,
        int baseCandleCount,
        bool brokeStructure,
        bool hasFairValueGap,
        decimal freshnessScore,
        int touchCount,
        decimal penetrationRatio,
        int ageBars)
    {
        SupplyDemandScoringWeights weights = _profile.ScoringWeights;

        decimal departureStrengthComponent = Math.Clamp(departureAtr / Math.Max(_profile.MinimumDepartureAtr, 0.01m), 0m, 3m) / 3m;
        decimal baseDurationComponent = Math.Clamp(1m - (decimal)(baseCandleCount - _profile.MinimumBaseCandles) / Math.Max(_profile.MaximumBaseCandles - _profile.MinimumBaseCandles, 1), 0m, 1m);
        decimal touchPenaltyComponent = Math.Clamp(1m - touchCount * 0.2m, 0m, 1m);
        decimal penetrationPenaltyComponent = Math.Clamp(1m - penetrationRatio, 0m, 1m);
        decimal agePenaltyComponent = _profile.MaximumZoneAgeBars is int maxAge && maxAge > 0
            ? Math.Clamp(1m - (decimal)ageBars / maxAge, 0m, 1m)
            : 1m;

        // Higher-timeframe alignment, support/resistance confluence, liquidity confluence, and
        // room-to-opposing-zone all require cross-timeframe or cross-subsystem context this
        // single-timeframe detector does not have in Phase 3 - per the blueprint's "missing or
        // uncertain evidence is neutral" rule, their contribution stays neutral (0) rather than
        // guessed.
        const decimal neutral = 0m;

        decimal weightedSum =
            weights.DepartureStrength * departureStrengthComponent +
            weights.DepartureEfficiency * departureEfficiency +
            weights.BaseCompactness * baseCompactness +
            weights.BaseDuration * baseDurationComponent +
            weights.StructureBreak * (brokeStructure ? 1m : 0m) +
            weights.FairValueGap * (hasFairValueGap ? 1m : 0m) +
            weights.Freshness * freshnessScore +
            weights.TouchPenalty * touchPenaltyComponent +
            weights.PenetrationPenalty * penetrationPenaltyComponent +
            weights.AgePenalty * agePenaltyComponent +
            weights.HigherTimeframeAlignment * neutral +
            weights.SupportResistanceConfluence * neutral +
            weights.LiquidityConfluence * neutral +
            weights.RoomToOpposingZone * neutral;

        decimal totalWeight =
            weights.DepartureStrength + weights.DepartureEfficiency + weights.BaseCompactness +
            weights.BaseDuration + weights.StructureBreak + weights.FairValueGap + weights.Freshness +
            weights.TouchPenalty + weights.PenetrationPenalty + weights.AgePenalty +
            weights.HigherTimeframeAlignment + weights.SupportResistanceConfluence +
            weights.LiquidityConfluence + weights.RoomToOpposingZone;

        // Unit interval [0, 1] — matches FreshnessScore, liquidity pool quality, and agent
        // thresholds such as MinimumZoneQuality (0.55). Never publish 0–100 here; consumers
        // multiply by 100 only when they need a 0–100 display/confidence component.
        return totalWeight > 0m ? Math.Clamp(weightedSum / totalWeight, 0m, 1m) : 0m;
    }

    private SupplyDemandZoneEvent CreateEvent(
        Guid zoneId,
        SupplyDemandZoneEventType eventType,
        SupplyDemandZoneState before,
        SupplyDemandZoneState after,
        decimal price,
        DateTimeOffset availableAt) =>
        new()
        {
            EventId = DeterministicId.Create("sdevent", zoneId, eventType, before, after, availableAt, _snapshotVersion),
            ZoneId = zoneId,
            EventType = eventType,
            StateBefore = before,
            StateAfter = after,
            Price = price,
            OccurredAt = availableAt,
            AvailableAt = availableAt,
            SnapshotVersion = _snapshotVersion
        };

    private sealed class ZoneRuntimeState
    {
        public required SupplyDemandZone Zone { get; set; }
        public required long ConfirmedAtSequence { get; init; }
        public decimal MaxPenetrationRatio { get; set; }
        public bool WasTouching { get; set; }
        public bool Terminal { get; set; }
    }
}
