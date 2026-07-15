using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.PriceAction;

/// <summary>
/// Stateful, deterministic price-action analysis over completed candles and already
/// confirmed structural evidence. It never reads future candles or unconfirmed pivots.
/// </summary>
public sealed class PriceActionAnalyzer
{
    private readonly PriceActionOptions _options;
    private readonly RingBuffer<decimal> _bodyAtrHistory;
    private readonly RingBuffer<decimal> _rangeAtrHistory;
    private readonly RingBuffer<decimal> _wickBodyHistory;
    private ActiveRetest? _activeRetest;
    private PriceActionCalibrationSnapshot? _frozenCalibration;

    public PriceActionAnalyzer(PriceActionOptions? options = null)
    {
        _options = options ?? new PriceActionOptions();
        _options.Validate();
        _bodyAtrHistory = new RingBuffer<decimal>(_options.CalibrationLookback);
        _rangeAtrHistory = new RingBuffer<decimal>(_options.CalibrationLookback);
        _wickBodyHistory = new RingBuffer<decimal>(_options.CalibrationLookback);
    }

    public void FreezeCalibration(DateTimeOffset frozenAt)
    {
        PriceActionCalibrationSnapshot current = BuildCalibration();
        _frozenCalibration = current with
        {
            IsFrozen = true,
            FrozenAt = frozenAt
        };
    }

    public PriceActionSnapshot Update(
        Candle candle,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<SwingPoint> swings,
        IReadOnlyList<PriceZone> zones,
        MarketStructureSnapshot structure,
        MarketStructureSnapshot previousStructure,
        IndicatorSnapshot indicators,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(swings);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(previousStructure);
        ArgumentNullException.ThrowIfNull(indicators);

        decimal? atr = indicators.Atr;
        if (atr is > 0m && _frozenCalibration is null)
            UpdateCalibration(candle, atr.Value);

        PriceActionCalibrationSnapshot calibration = _frozenCalibration ?? BuildCalibration();
        Candle? previousCandle = candles.Count >= 2 ? candles[^2] : null;
        var events = new List<PriceActionEvent>();
        var diagnostics = new List<PriceActionDiagnostic>(8);

        PriceActionEvent? breakEvent = DetectBreak(
            candle,
            previousCandle,
            structure,
            previousStructure,
            atr,
            sequence,
            diagnostics);
        if (breakEvent is not null)
        {
            events.Add(breakEvent);
            _activeRetest = new ActiveRetest(
                breakEvent.EventId,
                breakEvent.Direction,
                breakEvent.BrokenLevel!.Value,
                breakEvent.ConfirmedAt,
                breakEvent.ConfirmedSequence,
                0,
                null,
                BreakRetestState.AwaitingRetest,
                null);
        }

        PriceActionEvent? rejection = DetectRejection(
            candle,
            swings,
            zones,
            atr,
            sequence,
            diagnostics);
        if (rejection is not null)
            events.Add(rejection);

        PriceActionEvent? displacement = DetectDisplacement(
            candle,
            atr,
            calibration,
            sequence,
            diagnostics);
        if (displacement is not null)
            events.Add(displacement);

        PriceActionEvent? sweep = DetectSweep(
            candle,
            swings,
            atr,
            sequence,
            diagnostics);
        if (sweep is not null)
            events.Add(sweep);

        PriceActionEvent? expansion = DetectCompressionExpansion(
            candle,
            indicators,
            atr,
            sequence,
            diagnostics);
        if (expansion is not null)
            events.Add(expansion);

        if (breakEvent is null)
        {
            PriceActionEvent? retest = UpdateRetest(
                candle,
                atr,
                sequence,
                rejection,
                displacement,
                diagnostics);
            if (retest is not null)
                events.Add(retest);
        }

        PriceLegMetrics? latestLeg = CalculateLatestLeg(candles, swings, atr);
        DateTimeOffset availableAt = candle.CloseTime ?? candle.OpenTime;
        bool newSwingConfirmed = swings.Count > 0 && swings.Max(item => item.ConfirmedAt) == availableAt;
        if (newSwingConfirmed && latestLeg is not null && latestLeg.DistanceAtr is >= 1m &&
            latestLeg.Direction is PriceActionDirection.Bullish or PriceActionDirection.Bearish)
        {
            PriceActionEventType legType = latestLeg.Direction == PriceActionDirection.Bullish
                ? PriceActionEventType.BullishImpulse
                : PriceActionEventType.BearishImpulse;
            events.Add(CreateEvent(
                candle,
                sequence,
                legType,
                latestLeg.Direction,
                referenceLevel: null,
                atr,
                Math.Clamp((latestLeg.DistanceAtr ?? 0m) * 20m, 0m, 100m),
                Math.Clamp(45m + latestLeg.EfficiencyRatio * 40m, 0m, 100m),
                "PriceLegMeasured",
                $"The newly confirmed leg moved {latestLeg.DistanceAtr:F2} ATR with " +
                $"efficiency {latestLeg.EfficiencyRatio:F2}."));
        }

        // MinimumTriggerConfidence was previously validated but had no effect. Keep
        // non-trigger context such as BOS and measured legs, while preventing weak
        // rejection/displacement/sweep/CHOCH events from arming entries or composites.
        PriceActionEvent[] weakTriggers = events
            .Where(item => IsEntryTrigger(item.Type) &&
                item.Confidence < _options.MinimumTriggerConfidence)
            .ToArray();
        foreach (PriceActionEvent weak in weakTriggers)
        {
            AddRejected(
                diagnostics,
                weak.Type.ToString(),
                "TriggerConfidenceBelowMinimum",
                $"Trigger confidence {weak.Confidence:F1} is below " +
                $"{_options.MinimumTriggerConfidence:F1}.");
        }
        if (weakTriggers.Length > 0)
        {
            var rejectedIds = weakTriggers.Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
            events.RemoveAll(item => rejectedIds.Contains(item.EventId));
        }

        decimal bullish = events.Where(item => item.Direction == PriceActionDirection.Bullish)
            .Sum(ScoreEvent);
        decimal bearish = events.Where(item => item.Direction == PriceActionDirection.Bearish)
            .Sum(ScoreEvent);
        PriceActionDirection bias = Math.Abs(bullish - bearish) < 5m
            ? PriceActionDirection.Neutral
            : bullish > bearish
                ? PriceActionDirection.Bullish
                : PriceActionDirection.Bearish;

        return new PriceActionSnapshot
        {
            Bias = bias,
            BullishScore = Math.Clamp(bullish, 0m, 100m),
            BearishScore = Math.Clamp(bearish, 0m, 100m),
            Events = events
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.Type)
                .ToArray(),
            Diagnostics = diagnostics.ToArray(),
            ActiveRetest = ToSnapshot(_activeRetest),
            LatestLeg = latestLeg,
            Calibration = calibration
        };
    }

    private PriceActionEvent? DetectBreak(
        Candle candle,
        Candle? previous,
        MarketStructureSnapshot structure,
        MarketStructureSnapshot previousStructure,
        decimal? atr,
        long sequence,
        ICollection<PriceActionDiagnostic> diagnostics)
    {
        if (previous is null || atr is not > 0m)
        {
            AddRejected(diagnostics, "BreakOfStructure", "InsufficientHistory",
                "A previous candle and ready ATR are required.");
            return null;
        }

        decimal threshold = atr.Value * _options.MinimumBreakCloseAtr;
        SwingPoint? high = structure.LastSwingHigh;
        bool structureBullishBreak = structure.Break == MarketStructureBreak.Bullish;
        bool structureBearishBreak = structure.Break == MarketStructureBreak.Bearish;

        // Close beyond the last swing, on a transition bar. MarketStructure.Break is
        // accepted as an alternate transition signal so PA and structure stay aligned.
        if (high is not null &&
            candle.Prices.Close > high.Price + threshold &&
            (previous.Prices.Close <= high.Price + threshold || structureBullishBreak))
        {
            bool changeOfCharacter =
                previousStructure.Direction == MarketStructureDirection.Falling ||
                structureBullishBreak;
            decimal confidence = changeOfCharacter ? 80m : 72m;
            if (structureBullishBreak)
                confidence = Math.Min(100m, confidence + 6m);

            AddAccepted(diagnostics, "BullishBreak", changeOfCharacter ? "BullishChangeOfCharacter" : "BullishBreakOfStructure");
            return CreateEvent(
                candle,
                sequence,
                changeOfCharacter
                    ? PriceActionEventType.BullishChangeOfCharacter
                    : PriceActionEventType.BullishBreakOfStructure,
                PriceActionDirection.Bullish,
                high.Price,
                atr,
                Math.Clamp((candle.Prices.Close - high.Price) / atr.Value * 100m, 0m, 100m),
                confidence,
                changeOfCharacter ? "BullishChangeOfCharacter" : "BullishBreakOfStructure",
                $"The completed candle closed above confirmed swing high {high.Price}."
            ) with { BrokenLevel = high.Price, SourceSwingKey = SwingKey(high) };
        }

        SwingPoint? low = structure.LastSwingLow;
        if (low is not null &&
            candle.Prices.Close < low.Price - threshold &&
            (previous.Prices.Close >= low.Price - threshold || structureBearishBreak))
        {
            bool changeOfCharacter =
                previousStructure.Direction == MarketStructureDirection.Rising ||
                structureBearishBreak;
            decimal confidence = changeOfCharacter ? 80m : 72m;
            if (structureBearishBreak)
                confidence = Math.Min(100m, confidence + 6m);

            AddAccepted(diagnostics, "BearishBreak", changeOfCharacter ? "BearishChangeOfCharacter" : "BearishBreakOfStructure");
            return CreateEvent(
                candle,
                sequence,
                changeOfCharacter
                    ? PriceActionEventType.BearishChangeOfCharacter
                    : PriceActionEventType.BearishBreakOfStructure,
                PriceActionDirection.Bearish,
                low.Price,
                atr,
                Math.Clamp((low.Price - candle.Prices.Close) / atr.Value * 100m, 0m, 100m),
                confidence,
                changeOfCharacter ? "BearishChangeOfCharacter" : "BearishBreakOfStructure",
                $"The completed candle closed below confirmed swing low {low.Price}."
            ) with { BrokenLevel = low.Price, SourceSwingKey = SwingKey(low) };
        }

        AddRejected(diagnostics, "BreakOfStructure", "BreakNotClosed",
            "The completed candle did not close beyond a confirmed swing by the configured ATR margin.");
        return null;
    }

    private PriceActionEvent? DetectRejection(
        Candle candle,
        IReadOnlyList<SwingPoint> swings,
        IReadOnlyList<PriceZone> zones,
        decimal? atr,
        long sequence,
        ICollection<PriceActionDiagnostic> diagnostics)
    {
        if (atr is not > 0m)
        {
            AddRejected(diagnostics, "StructuralRejection", "AtrNotReady", "ATR is required to normalise rejection geometry.");
            return null;
        }

        decimal range = candle.Prices.High - candle.Prices.Low;
        if (range <= 0m || range / atr.Value < _options.MinimumRejectionRangeAtr)
        {
            AddRejected(diagnostics, "StructuralRejection", "RejectionRangeTooSmall", "The candle range is too small relative to ATR.");
            return null;
        }

        decimal body = Math.Abs(candle.Prices.Close - candle.Prices.Open);
        decimal safeBody = Math.Max(body, range * 0.02m);
        decimal lowerWick = Math.Min(candle.Prices.Open, candle.Prices.Close) - candle.Prices.Low;
        decimal upperWick = candle.Prices.High - Math.Max(candle.Prices.Open, candle.Prices.Close);
        decimal closePosition = (candle.Prices.Close - candle.Prices.Low) / range;

        // Support must sit under/at the candle (not a level floating above the range).
        (decimal? Level, string? Key) support = FindNearestSupport(candle, swings, zones, atr.Value);
        if (support.Level is decimal supportLevel &&
            Math.Abs(candle.Prices.Low - supportLevel) / atr.Value <= _options.MaximumStructureDistanceAtr &&
            lowerWick / safeBody >= _options.MinimumRejectionWickToBody &&
            closePosition >= _options.MinimumRejectionClosePosition)
        {
            AddAccepted(diagnostics, "BullishRejection", "BullishStructuralRejection");
            return CreateEvent(
                candle,
                sequence,
                PriceActionEventType.BullishRejection,
                PriceActionDirection.Bullish,
                supportLevel,
                atr,
                Math.Clamp(lowerWick / safeBody * 25m, 0m, 100m),
                Math.Clamp(55m + closePosition * 35m, 0m, 100m),
                "BullishStructuralRejection",
                $"A lower-wick rejection closed in the upper {closePosition:P0} of its range near confirmed support.")
                with { SourceZoneKey = support.Key };
        }

        (decimal? Level, string? Key) resistance = FindNearestResistance(candle, swings, zones, atr.Value);
        decimal upperClosePosition = (candle.Prices.High - candle.Prices.Close) / range;
        if (resistance.Level is decimal resistanceLevel &&
            Math.Abs(candle.Prices.High - resistanceLevel) / atr.Value <= _options.MaximumStructureDistanceAtr &&
            upperWick / safeBody >= _options.MinimumRejectionWickToBody &&
            upperClosePosition >= _options.MinimumRejectionClosePosition)
        {
            AddAccepted(diagnostics, "BearishRejection", "BearishStructuralRejection");
            return CreateEvent(
                candle,
                sequence,
                PriceActionEventType.BearishRejection,
                PriceActionDirection.Bearish,
                resistanceLevel,
                atr,
                Math.Clamp(upperWick / safeBody * 25m, 0m, 100m),
                Math.Clamp(55m + upperClosePosition * 35m, 0m, 100m),
                "BearishStructuralRejection",
                $"An upper-wick rejection closed in the lower {upperClosePosition:P0} of its range near confirmed resistance.")
                with { SourceZoneKey = resistance.Key };
        }

        AddRejected(diagnostics, "StructuralRejection", "RejectionAwayFromStructure",
            "Candle geometry and structural proximity did not jointly satisfy the rejection rule.");
        return null;
    }

    private PriceActionEvent? DetectDisplacement(
        Candle candle,
        decimal? atr,
        PriceActionCalibrationSnapshot calibration,
        long sequence,
        ICollection<PriceActionDiagnostic> diagnostics)
    {
        if (atr is not > 0m)
        {
            AddRejected(diagnostics, "Displacement", "AtrNotReady", "ATR is required to normalise displacement geometry.");
            return null;
        }

        decimal range = candle.Prices.High - candle.Prices.Low;
        if (range <= 0m)
            return null;
        decimal body = Math.Abs(candle.Prices.Close - candle.Prices.Open);
        decimal bodyAtr = body / atr.Value;
        decimal rangeAtr = range / atr.Value;
        // Warm-up path uses a fixed ATR body floor so early bars are not silent.
        decimal requiredBodyAtr = calibration.IsReady
            ? Math.Max(
                calibration.MedianBodyAtr * _options.MinimumDisplacementBodyMedianMultiple,
                calibration.BodyAtr70)
            : _options.DisplacementBodyAtrFallback;
        decimal closePosition = (candle.Prices.Close - candle.Prices.Low) / range;
        decimal confidenceBase = calibration.IsReady ? 60m : 52m;

        if (candle.Prices.Close > candle.Prices.Open &&
            bodyAtr >= requiredBodyAtr &&
            rangeAtr >= _options.MinimumDisplacementRangeAtr &&
            closePosition >= _options.MinimumDisplacementClosePosition)
        {
            AddAccepted(diagnostics, "BullishDisplacement",
                calibration.IsReady ? "BullishDisplacementConfirmed" : "BullishDisplacementFallback");
            return CreateEvent(
                candle,
                sequence,
                PriceActionEventType.BullishDisplacement,
                PriceActionDirection.Bullish,
                candle.Prices.Close,
                atr,
                Math.Clamp(bodyAtr / Math.Max(requiredBodyAtr, 0.00000001m) * 60m, 0m, 100m),
                Math.Clamp(confidenceBase + closePosition * 30m, 0m, 100m),
                calibration.IsReady ? "BullishDisplacementConfirmed" : "BullishDisplacementFallback",
                $"Bullish body measured {bodyAtr:F2} ATR versus a {(calibration.IsReady ? "calibrated" : "fallback")} {requiredBodyAtr:F2} ATR threshold.");
        }

        decimal bearishClosePosition = (candle.Prices.High - candle.Prices.Close) / range;
        if (candle.Prices.Close < candle.Prices.Open &&
            bodyAtr >= requiredBodyAtr &&
            rangeAtr >= _options.MinimumDisplacementRangeAtr &&
            bearishClosePosition >= _options.MinimumDisplacementClosePosition)
        {
            AddAccepted(diagnostics, "BearishDisplacement",
                calibration.IsReady ? "BearishDisplacementConfirmed" : "BearishDisplacementFallback");
            return CreateEvent(
                candle,
                sequence,
                PriceActionEventType.BearishDisplacement,
                PriceActionDirection.Bearish,
                candle.Prices.Close,
                atr,
                Math.Clamp(bodyAtr / Math.Max(requiredBodyAtr, 0.00000001m) * 60m, 0m, 100m),
                Math.Clamp(confidenceBase + bearishClosePosition * 30m, 0m, 100m),
                calibration.IsReady ? "BearishDisplacementConfirmed" : "BearishDisplacementFallback",
                $"Bearish body measured {bodyAtr:F2} ATR versus a {(calibration.IsReady ? "calibrated" : "fallback")} {requiredBodyAtr:F2} ATR threshold.");
        }

        AddRejected(diagnostics, "Displacement", "DisplacementBodyTooSmall",
            $"The candle did not satisfy body, range, and closing-position thresholds ({requiredBodyAtr:F2} body ATR required).");
        return null;
    }

    private PriceActionEvent? DetectSweep(
        Candle candle,
        IReadOnlyList<SwingPoint> swings,
        decimal? atr,
        long sequence,
        ICollection<PriceActionDiagnostic> diagnostics)
    {
        if (atr is not > 0m)
            return null;

        DateTimeOffset availableAt = candle.CloseTime ?? candle.OpenTime;
        // Prefer the most recent qualifying equal-high/low style pivot, not only the
        // single latest swing (which may be a micro pivot that never got swept).
        SwingPoint[] recentLows = swings
            .Where(item => item.Type == SwingType.Low && item.ConfirmedAt <= availableAt)
            .OrderByDescending(item => item.PivotTime)
            .Take(_options.MaxSweepCandidateSwings)
            .ToArray();
        foreach (SwingPoint low in recentLows)
        {
            decimal penetration = (low.Price - candle.Prices.Low) / atr.Value;
            decimal recovery = (candle.Prices.Close - low.Price) / atr.Value;
            if (penetration >= _options.MinimumSweepPenetrationAtr &&
                penetration <= _options.MaximumSweepPenetrationAtr &&
                recovery >= _options.MinimumSweepRecoveryAtr)
            {
                AddAccepted(diagnostics, "SellSideSweep", "SellSideLiquiditySweep");
                return CreateEvent(
                    candle,
                    sequence,
                    PriceActionEventType.SellSideLiquiditySweep,
                    PriceActionDirection.Bullish,
                    low.Price,
                    atr,
                    Math.Clamp((penetration + recovery) * 100m, 0m, 100m),
                    68m,
                    "SellSideLiquiditySweep",
                    "Price traded below a confirmed swing low and the completed candle recovered above it.")
                    with { SourceSwingKey = SwingKey(low) };
            }
        }

        SwingPoint[] recentHighs = swings
            .Where(item => item.Type == SwingType.High && item.ConfirmedAt <= availableAt)
            .OrderByDescending(item => item.PivotTime)
            .Take(_options.MaxSweepCandidateSwings)
            .ToArray();
        foreach (SwingPoint high in recentHighs)
        {
            decimal penetration = (candle.Prices.High - high.Price) / atr.Value;
            decimal recovery = (high.Price - candle.Prices.Close) / atr.Value;
            if (penetration >= _options.MinimumSweepPenetrationAtr &&
                penetration <= _options.MaximumSweepPenetrationAtr &&
                recovery >= _options.MinimumSweepRecoveryAtr)
            {
                AddAccepted(diagnostics, "BuySideSweep", "BuySideLiquiditySweep");
                return CreateEvent(
                    candle,
                    sequence,
                    PriceActionEventType.BuySideLiquiditySweep,
                    PriceActionDirection.Bearish,
                    high.Price,
                    atr,
                    Math.Clamp((penetration + recovery) * 100m, 0m, 100m),
                    68m,
                    "BuySideLiquiditySweep",
                    "Price traded above a confirmed swing high and the completed candle recovered below it.")
                    with { SourceSwingKey = SwingKey(high) };
            }
        }

        AddRejected(diagnostics, "LiquiditySweep", "NoConfirmedSweep",
            "No confirmed swing was penetrated and recovered within the configured ATR limits.");
        return null;
    }

    private PriceActionEvent? DetectCompressionExpansion(
        Candle candle,
        IndicatorSnapshot indicators,
        decimal? atr,
        long sequence,
        ICollection<PriceActionDiagnostic> diagnostics)
    {
        BollingerAnalysisSnapshot context = indicators.BollingerAnalysis;
        if (!context.SqueezeReleased && !context.IsExpansion)
        {
            AddRejected(diagnostics, "CompressionExpansion", "CompressionNotReleased",
                "Bollinger bandwidth has not released from compression or entered expansion.");
            return null;
        }

        decimal range = candle.Prices.High - candle.Prices.Low;
        decimal body = Math.Abs(candle.Prices.Close - candle.Prices.Open);
        decimal rangeAtr = atr is > 0m && range > 0m ? range / atr.Value : 0m;
        decimal bodyShare = range > 0m ? body / range : 0m;
        // Squeeze-release breakouts are the primary signal. Expansion-only closes
        // outside the bands must also show a decisive body to reduce spam.
        bool decisiveBody = context.SqueezeReleased ||
            rangeAtr >= _options.MinimumExpansionBreakoutRangeAtr && bodyShare >= 0.50m;

        if (!decisiveBody)
        {
            AddRejected(diagnostics, "CompressionExpansion", "ExpansionBodyNotDecisive",
                "Volatility expanded without a squeeze release or a decisive-range close outside the envelope.");
            return null;
        }

        if (indicators.BollingerUpper is decimal upper &&
            candle.Prices.Close > upper &&
            candle.Prices.Close >= candle.Prices.Open)
        {
            AddAccepted(diagnostics, "BullishCompressionBreakout", "BullishCompressionBreakout");
            return CreateEvent(
                candle,
                sequence,
                PriceActionEventType.BullishCompressionBreakout,
                PriceActionDirection.Bullish,
                upper,
                atr,
                75m,
                context.SqueezeReleased ? 82m : 65m,
                "BullishCompressionBreakout",
                "Bollinger bandwidth expanded and the completed candle closed above the upper volatility band.");
        }

        if (indicators.BollingerLower is decimal lower &&
            candle.Prices.Close < lower &&
            candle.Prices.Close <= candle.Prices.Open)
        {
            AddAccepted(diagnostics, "BearishCompressionBreakout", "BearishCompressionBreakout");
            return CreateEvent(
                candle,
                sequence,
                PriceActionEventType.BearishCompressionBreakout,
                PriceActionDirection.Bearish,
                lower,
                atr,
                75m,
                context.SqueezeReleased ? 82m : 65m,
                "BearishCompressionBreakout",
                "Bollinger bandwidth expanded and the completed candle closed below the lower volatility band.");
        }

        AddRejected(diagnostics, "CompressionExpansion", "ExpansionDidNotBreakEnvelope",
            "Volatility expanded but price did not close outside the Bollinger envelope with directional commitment.");
        return null;
    }

    private PriceActionEvent? UpdateRetest(
        Candle candle,
        decimal? atr,
        long sequence,
        PriceActionEvent? rejection,
        PriceActionEvent? displacement,
        ICollection<PriceActionDiagnostic> diagnostics)
    {
        if (_activeRetest is null || atr is not > 0m)
            return null;

        if (_activeRetest.State is BreakRetestState.RetestHeld or
            BreakRetestState.RetestFailed or
            BreakRetestState.Expired)
        {
            _activeRetest = null;
            return null;
        }

        ActiveRetest state = _activeRetest with { BarsSinceBreak = _activeRetest.BarsSinceBreak + 1 };
        if (state.BarsSinceBreak > _options.RetestExpiryBars)
        {
            _activeRetest = state with { State = BreakRetestState.Expired, InvalidReason = "RetestExpired" };
            AddRejected(diagnostics, "BreakRetest", "RetestExpired", "No valid retest occurred before the configured expiry.");
            return null;
        }

        decimal distance = state.Direction == PriceActionDirection.Bullish
            ? Math.Abs(candle.Prices.Low - state.BrokenLevel) / atr.Value
            : Math.Abs(candle.Prices.High - state.BrokenLevel) / atr.Value;
        decimal? closest = state.ClosestDistanceAtr is null
            ? distance
            : Math.Min(state.ClosestDistanceAtr.Value, distance);

        bool failed = state.Direction == PriceActionDirection.Bullish
            ? candle.Prices.Close < state.BrokenLevel - atr.Value * _options.MaximumRetestDepthAtr
            : candle.Prices.Close > state.BrokenLevel + atr.Value * _options.MaximumRetestDepthAtr;
        if (failed)
        {
            _activeRetest = state with
            {
                State = BreakRetestState.RetestFailed,
                ClosestDistanceAtr = closest,
                InvalidReason = "RetestTooDeep"
            };
            AddRejected(diagnostics, "BreakRetest", "RetestTooDeep", "The completed candle closed materially through the broken level.");
            return null;
        }

        bool touched = distance <= _options.RetestProximityAtr;
        // Hold = touch + close still on the correct side of the broken level.
        // A slightly opposing candle body is allowed (common on shallow retests);
        // rejection/displacement/aligned body only boosts confidence.
        bool heldOnCorrectSide = state.Direction == PriceActionDirection.Bullish
            ? candle.Prices.Close >= state.BrokenLevel
            : candle.Prices.Close <= state.BrokenLevel;
        bool held = touched && heldOnCorrectSide;

        if (!held)
        {
            _activeRetest = state with
            {
                State = touched ? BreakRetestState.RetestInProgress : BreakRetestState.AwaitingRetest,
                ClosestDistanceAtr = closest
            };
            AddRejected(diagnostics, "BreakRetest",
                touched ? "RetestAwaitingHoldClose" : "RetestNotReached",
                touched
                    ? "Price revisited the broken level but the completed close did not hold on the correct side."
                    : "Price has not revisited the broken level within the configured ATR distance.");
            return null;
        }

        bool alignedBody = state.Direction == PriceActionDirection.Bullish
            ? candle.Prices.Close >= candle.Prices.Open
            : candle.Prices.Close <= candle.Prices.Open;
        bool supportiveSignal =
            rejection?.Direction == state.Direction ||
            displacement?.Direction == state.Direction ||
            alignedBody;
        decimal confidence = supportiveSignal ? 90m : 82m;

        _activeRetest = state with
        {
            State = BreakRetestState.RetestHeld,
            ClosestDistanceAtr = closest
        };
        PriceActionEventType type = state.Direction == PriceActionDirection.Bullish
            ? PriceActionEventType.BullishRetestHeld
            : PriceActionEventType.BearishRetestHeld;
        AddAccepted(diagnostics, "BreakRetest", "RetestHeld");
        return CreateEvent(
            candle,
            sequence,
            type,
            state.Direction,
            state.BrokenLevel,
            atr,
            Math.Clamp(100m - distance * 100m, 0m, 100m),
            confidence,
            "RetestHeld",
            $"The broken level {state.BrokenLevel} was revisited within {distance:F2} ATR and held on a completed candle.")
            with { BrokenLevel = state.BrokenLevel, RetestLevel = state.BrokenLevel };
    }

    private static PriceLegMetrics? CalculateLatestLeg(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<SwingPoint> swings,
        decimal? atr)
    {
        SwingPoint[] ordered = swings
            .OrderBy(item => item.PivotTime)
            .ThenBy(item => item.ConfirmedAt)
            .TakeLast(3)
            .ToArray();
        if (ordered.Length < 2 || ordered[^2].Type == ordered[^1].Type)
            return null;

        SwingPoint from = ordered[^2];
        SwingPoint to = ordered[^1];
        PriceActionDirection direction = to.Price > from.Price
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        Candle[] legCandles = candles
            .Where(item => item.OpenTime >= from.PivotTime && item.OpenTime <= to.PivotTime)
            .OrderBy(item => item.OpenTime)
            .ToArray();
        decimal path = 0m;
        for (int index = 1; index < legCandles.Length; index++)
            path += Math.Abs(legCandles[index].Prices.Close - legCandles[index - 1].Prices.Close);
        decimal distance = Math.Abs(to.Price - from.Price);
        decimal efficiency = path <= 0m ? 1m : Math.Clamp(distance / path, 0m, 1m);
        decimal retracementPercent = 0m;
        if (ordered.Length == 3 &&
            ordered[0].Type != ordered[1].Type &&
            ordered[1].Type != ordered[2].Type)
        {
            decimal previousLegDistance = Math.Abs(ordered[1].Price - ordered[0].Price);
            PriceActionDirection previousDirection = ordered[1].Price > ordered[0].Price
                ? PriceActionDirection.Bullish
                : PriceActionDirection.Bearish;
            if (previousLegDistance > 0m && previousDirection != direction)
            {
                retracementPercent = Math.Clamp(
                    distance / previousLegDistance * 100m,
                    0m,
                    500m);
            }
        }

        return new PriceLegMetrics
        {
            Direction = direction,
            StartedAt = from.PivotTime,
            EndedAt = to.PivotTime,
            Distance = distance,
            DistanceAtr = atr is > 0m ? distance / atr.Value : null,
            BarCount = legCandles.Length,
            EfficiencyRatio = efficiency,
            RetracementPercent = retracementPercent
        };
    }

    private void UpdateCalibration(Candle candle, decimal atr)
    {
        decimal range = candle.Prices.High - candle.Prices.Low;
        if (range <= 0m || atr <= 0m)
            return;
        decimal body = Math.Abs(candle.Prices.Close - candle.Prices.Open);
        decimal lower = Math.Min(candle.Prices.Open, candle.Prices.Close) - candle.Prices.Low;
        decimal upper = candle.Prices.High - Math.Max(candle.Prices.Open, candle.Prices.Close);
        _bodyAtrHistory.Add(body / atr);
        _rangeAtrHistory.Add(range / atr);
        _wickBodyHistory.Add(Math.Max(lower, upper) / Math.Max(body, range * 0.02m));
    }

    private PriceActionCalibrationSnapshot BuildCalibration()
    {
        decimal[] bodies = _bodyAtrHistory.Snapshot();
        decimal[] ranges = _rangeAtrHistory.Snapshot();
        decimal[] wicks = _wickBodyHistory.Snapshot();
        int count = Math.Min(bodies.Length, Math.Min(ranges.Length, wicks.Length));
        if (count == 0)
            return PriceActionCalibrationSnapshot.Empty;

        return new PriceActionCalibrationSnapshot
        {
            SampleCount = count,
            IsReady = count >= _options.CalibrationMinimumSamples,
            IsFrozen = false,
            MedianBodyAtr = Percentile(bodies, 50m),
            MedianRangeAtr = Percentile(ranges, 50m),
            MedianWickToBodyRatio = Percentile(wicks, 50m),
            BodyAtr70 = Percentile(bodies, 70m),
            RangeAtr70 = Percentile(ranges, 70m),
            RangeAtr90 = Percentile(ranges, 90m)
        };
    }

    private static decimal Percentile(IEnumerable<decimal> values, decimal percentile)
    {
        decimal[] ordered = values.OrderBy(item => item).ToArray();
        if (ordered.Length == 0)
            return 0m;
        decimal position = percentile / 100m * (ordered.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return ordered[lower];
        decimal fraction = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }

    private (decimal? Level, string? Key) FindNearestSupport(
        Candle candle,
        IReadOnlyList<SwingPoint> swings,
        IReadOnlyList<PriceZone> zones,
        decimal atr)
    {
        // Support must not sit above the candle range; Mixed zones above price are
        // supply and must not be treated as demand for bullish rejections.
        decimal ceiling = candle.Prices.High + atr * 0.05m;
        decimal reference = candle.Prices.Low;
        var candidates = new List<(decimal Level, string Key)>();
        candidates.AddRange(swings
            .Where(item => item.Type == SwingType.Low && item.Price <= ceiling)
            .Select(item => (item.Price, SwingKey(item))));
        candidates.AddRange(zones
            .Where(item =>
                (item.Type is PriceZoneType.Support or PriceZoneType.Mixed) &&
                item.UpperPrice <= ceiling)
            .Select(item => (item.UpperPrice, ZoneKey(item))));
        return candidates
            .OrderBy(item => Math.Abs(item.Level - reference))
            .Select(item => ((decimal?)item.Level, (string?)item.Key))
            .FirstOrDefault();
    }

    private (decimal? Level, string? Key) FindNearestResistance(
        Candle candle,
        IReadOnlyList<SwingPoint> swings,
        IReadOnlyList<PriceZone> zones,
        decimal atr)
    {
        // Resistance must not sit below the candle range.
        decimal floor = candle.Prices.Low - atr * 0.05m;
        decimal reference = candle.Prices.High;
        var candidates = new List<(decimal Level, string Key)>();
        candidates.AddRange(swings
            .Where(item => item.Type == SwingType.High && item.Price >= floor)
            .Select(item => (item.Price, SwingKey(item))));
        candidates.AddRange(zones
            .Where(item =>
                (item.Type is PriceZoneType.Resistance or PriceZoneType.Mixed) &&
                item.LowerPrice >= floor)
            .Select(item => (item.LowerPrice, ZoneKey(item))));
        return candidates
            .OrderBy(item => Math.Abs(item.Level - reference))
            .Select(item => ((decimal?)item.Level, (string?)item.Key))
            .FirstOrDefault();
    }

    private static PriceActionEvent CreateEvent(
        Candle candle,
        long sequence,
        PriceActionEventType type,
        PriceActionDirection direction,
        decimal? referenceLevel,
        decimal? atr,
        decimal strength,
        decimal confidence,
        string reasonCode,
        string explanation) => new()
    {
        EventId = $"{candle.Instrument.Value}:{candle.Interval}:{sequence}:{type}",
        Type = type,
        Direction = direction,
        ConfirmedAt = candle.CloseTime ?? candle.OpenTime,
        ConfirmedSequence = sequence,
        ReferenceLevel = referenceLevel,
        Atr = atr,
        Strength = Math.Clamp(strength, 0m, 100m),
        Confidence = Math.Clamp(confidence, 0m, 100m),
        ReasonCode = reasonCode,
        Explanation = explanation
    };

    private static decimal ScoreEvent(PriceActionEvent item)
    {
        decimal weight = item.Type switch
        {
            PriceActionEventType.BullishRetestHeld or PriceActionEventType.BearishRetestHeld => 1.00m,
            PriceActionEventType.BullishChangeOfCharacter or PriceActionEventType.BearishChangeOfCharacter => 0.90m,
            PriceActionEventType.BullishBreakOfStructure or PriceActionEventType.BearishBreakOfStructure => 0.80m,
            PriceActionEventType.BullishCompressionBreakout or PriceActionEventType.BearishCompressionBreakout => 0.75m,
            PriceActionEventType.BullishRejection or PriceActionEventType.BearishRejection => 0.70m,
            PriceActionEventType.BullishDisplacement or PriceActionEventType.BearishDisplacement => 0.65m,
            PriceActionEventType.SellSideLiquiditySweep or PriceActionEventType.BuySideLiquiditySweep => 0.60m,
            _ => 0.25m
        };
        return item.Confidence * weight;
    }

    private static bool IsEntryTrigger(PriceActionEventType type) => type is
        PriceActionEventType.BullishRetestHeld or
        PriceActionEventType.BearishRetestHeld or
        PriceActionEventType.BullishRejection or
        PriceActionEventType.BearishRejection or
        PriceActionEventType.BullishDisplacement or
        PriceActionEventType.BearishDisplacement or
        PriceActionEventType.BullishCompressionBreakout or
        PriceActionEventType.BearishCompressionBreakout or
        PriceActionEventType.SellSideLiquiditySweep or
        PriceActionEventType.BuySideLiquiditySweep or
        PriceActionEventType.BullishChangeOfCharacter or
        PriceActionEventType.BearishChangeOfCharacter;

    private static BreakRetestSnapshot ToSnapshot(ActiveRetest? state) => state is null
        ? BreakRetestSnapshot.Empty
        : new BreakRetestSnapshot
        {
            SetupId = state.SetupId,
            Direction = state.Direction,
            State = state.State,
            BrokenLevel = state.BrokenLevel,
            BreakConfirmedAt = state.BreakConfirmedAt,
            BreakSequence = state.BreakSequence,
            BarsSinceBreak = state.BarsSinceBreak,
            ClosestRetestDistanceAtr = state.ClosestDistanceAtr,
            InvalidReason = state.InvalidReason
        };

    private static string SwingKey(SwingPoint item) =>
        $"{item.Type}:{item.PivotTime:O}:{item.Price}";

    private static string ZoneKey(PriceZone item) =>
        $"{item.Type}:{item.LowerPrice}:{item.UpperPrice}:{item.TouchCount}";

    private static void AddAccepted(ICollection<PriceActionDiagnostic> diagnostics, string candidate, string reason) =>
        diagnostics.Add(new PriceActionDiagnostic
        {
            Candidate = candidate,
            Accepted = true,
            ReasonCode = reason,
            Explanation = reason
        });

    private static void AddRejected(
        ICollection<PriceActionDiagnostic> diagnostics,
        string candidate,
        string reason,
        string explanation) => diagnostics.Add(new PriceActionDiagnostic
        {
            Candidate = candidate,
            Accepted = false,
            ReasonCode = reason,
            Explanation = explanation
        });

    private sealed record ActiveRetest(
        string SetupId,
        PriceActionDirection Direction,
        decimal BrokenLevel,
        DateTimeOffset BreakConfirmedAt,
        long BreakSequence,
        int BarsSinceBreak,
        decimal? ClosestDistanceAtr,
        BreakRetestState State,
        string? InvalidReason);
}
