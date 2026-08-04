using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Agent.Strategies.DivergenceReversal;

/// <summary>
/// Formalized version of the user's original Bollinger + RSI + StochRSI extreme-reading strategy.
/// At an extreme reading (price at a Bollinger band, RSI and StochRSI-fast both at their extreme)
/// on a <see cref="DivergenceReversalStrategyOptions.MonitoredIntervals"/> trigger timeframe,
/// classifies what happens next using StochRSI-fast divergence against price at the confirmed
/// swing pivot:
/// <list type="bullet">
/// <item>Regular divergence (price extends to a new extreme, StochRSI-fast doesn't confirm it) -
/// momentum is exhausted - <b>reversal</b>: close any opposing position, open the new direction.</item>
/// <item>Hidden divergence or convergence (StochRSI-fast confirms or extends with price) -
/// momentum agrees with the extreme - <b>breakout/continuation</b>: hold an existing position in
/// that direction, or open one if flat. Never closes a position purely because a breakout fired -
/// only a later genuine reversal does that.</item>
/// </list>
/// If the trigger timeframe's reading is only <i>partial</i> (touches the condition without fully
/// qualifying), steps down through <see cref="DivergenceReversalStrategyOptions.ConfirmationIntervals"/>,
/// coarsest first, looking for a full reading in the same direction there - mirroring the original
/// Python's recursive lower-timeframe confirmation. This can fire independently on any monitored
/// timeframe - there is no fixed trend/setup/entry role hierarchy, matching the source
/// conversation's "any timeframe can trigger." The primary exit is always the next genuine
/// opposing reversal signal; a wide ATR protective stop is submitted as a backstop only
/// (<see cref="AgentExitManagementMode.ProtectiveStopAndStrategyExit"/>), bounding the one risk the
/// original proof-of-concept had no answer for: an extended move that never produces a clean
/// reversal signal.
/// </summary>
public sealed class DivergenceReversalAgent : ITradingAgent
{
    private readonly DivergenceReversalStrategyOptions _options;
    private readonly IReadOnlyList<BarInterval> _triggerIntervals;
    private readonly Dictionary<BarInterval, TimeframeState> _timeframeStates;
    private readonly Dictionary<InstrumentKey, OrderSide> _pendingEntry = [];

    public DivergenceReversalAgent(DivergenceReversalStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _triggerIntervals = options.MonitoredIntervals;

        IReadOnlyList<BarInterval> allIntervals = [.. options.MonitoredIntervals, .. options.ConfirmationIntervals];
        _timeframeStates = allIntervals.ToDictionary(
            interval => interval,
            _ => new TimeframeState(new StochRsiAnalysisState(
                minimumStochRsiDifference: options.MinimumStochRsiFastDifference,
                minimumPriceDifferenceAtr: options.MinimumPriceDifferenceAtr,
                signalLifetimeCandles: options.RelationshipSignalLifetimeCandles)));

        RequiredIntervals = new HashSet<BarInterval>(allIntervals);
        TriggerInterval = allIntervals
            .OrderBy(interval => interval, Comparer<BarInterval>.Create(BarIntervalParser.CompareDuration))
            .First();
    }

    public string Name => "Divergence reversal agent";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval { get; }
    public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        BrokerPosition? currentPosition = context.Positions
            .FirstOrDefault(position => position.Instrument == context.Instrument && position.Quantity > 0m);
        OrderSide? currentSide = currentPosition?.Side;

        // Second half of a close-then-flip: we closed the opposing position on a prior evaluation
        // and are now flat, so open the remembered direction without waiting to re-detect the
        // signal (which may no longer be true a candle later).
        if (currentSide is null &&
            _pendingEntry.TryGetValue(context.Instrument, out OrderSide pendingSide))
        {
            _pendingEntry.Remove(context.Instrument);
            return Task.FromResult(EnterTrade(context, pendingSide, TriggerInterval,
                "Opening the flip side after closing the prior opposing position."));
        }

        foreach (BarInterval triggerInterval in _triggerIntervals)
        {
            SignalMatch? signal = EvaluateTrigger(context, triggerInterval);
            if (signal is null)
                continue;

            SignalMatch match = signal.Value;
            OrderSide signalSide = match.Direction == ExtremeDirection.Bullish ? OrderSide.Buy : OrderSide.Sell;

            if (currentSide == signalSide)
            {
                // Already positioned correctly for this read; nothing to do.
                continue;
            }

            if (currentSide is not null)
            {
                // Currently holding the opposite side. A breakout classification only ever opens or
                // holds a position - it never triggers a close. Only a genuine reversal does.
                if (!match.IsReversal)
                    continue;

                _pendingEntry[context.Instrument] = signalSide;
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Close,
                    Instrument = context.Instrument,
                    SuggestedQuantity = currentPosition!.Quantity,
                    QuantityUnit = QuantityUnit.Units,
                    Confidence = match.Relationship.Strength,
                    CreatedAt = context.Timestamp,
                    Reason = match.Reason
                });
            }

            return Task.FromResult(EnterTrade(context, signalSide, match.SignalInterval, match.Reason, match.Relationship.Strength));
        }

        return Task.FromResult(new AgentDecision
        {
            Action = AgentAction.Observe,
            Instrument = context.Instrument,
            Confidence = 0m,
            CreatedAt = context.Timestamp,
            Reason = "No extreme reading with a confirmed StochRSI-fast relationship on any monitored or confirmation timeframe."
        });
    }

    /// <summary>
    /// Evaluates one trigger timeframe. A full reading is classified directly from that timeframe's
    /// own relationship; a partial reading steps down through the confirmation intervals (coarsest
    /// first, as configured) looking for a full reading in the same direction, using whichever
    /// confirmation timeframe first supplies one.
    /// </summary>
    private SignalMatch? EvaluateTrigger(AgentMarketContext context, BarInterval triggerInterval)
    {
        (ExtremeCertainty Certainty, ExtremeDirection Direction)? trigger =
            UpdateAndClassify(context, triggerInterval, expectedDirection: null);
        if (trigger is not { Certainty: ExtremeCertainty.Partial or ExtremeCertainty.Full } triggerReading)
            return null;

        if (triggerReading.Certainty == ExtremeCertainty.Full)
            return TryBuildSignal(triggerInterval, triggerReading.Direction);

        // Partial: step down through the confirmation intervals, coarsest first (list order),
        // looking for the first one whose own reading fully confirms the same direction.
        foreach (BarInterval confirmationInterval in _options.ConfirmationIntervals)
        {
            (ExtremeCertainty Certainty, ExtremeDirection Direction)? confirmation =
                UpdateAndClassify(context, confirmationInterval, triggerReading.Direction);
            if (confirmation is { Certainty: ExtremeCertainty.Full } confirmed)
            {
                SignalMatch? match = TryBuildSignal(confirmationInterval, confirmed.Direction);
                if (match is { } confirmedMatch)
                {
                    return confirmedMatch with
                    {
                        Reason = $"{triggerInterval} partial reading confirmed on {confirmationInterval}. {confirmedMatch.Reason}"
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Feeds this interval's latest closed candle into its <see cref="StochRsiAnalysisState"/> (once
    /// per candle) and classifies the current extreme condition. When <paramref name="expectedDirection"/>
    /// is set (a confirmation-interval check), only that direction is evaluated.
    /// </summary>
    private (ExtremeCertainty Certainty, ExtremeDirection Direction)? UpdateAndClassify(
        AgentMarketContext context,
        BarInterval interval,
        ExtremeDirection? expectedDirection)
    {
        if (!context.Analysis.TryGet(interval, out AnalysisSnapshot snapshot))
            return null;

        TimeframeState state = _timeframeStates[interval];
        Candle candle = snapshot.LatestCandle;
        bool isNewCandle = state.LastProcessedCandleOpenTime != candle.OpenTime;
        if (isNewCandle)
        {
            // snapshot.Swings is the engine's full accumulated swing window (up to
            // ChartAnnotationOptions.SwingCapacity, re-sent unchanged every candle) - not a
            // per-candle delta. Feeding that whole window into StochRsiAnalysisState.Update every
            // time would re-add already-seen pivots and keep IsNewRelationship stuck true for as
            // long as the pivot stays in the window (see PROJECT_STATE.md SS2.8's bug writeup), so
            // filter down to only the swings confirmed since the last candle this interval processed.
            IReadOnlyList<SwingPoint> newSwings = state.LastProcessedSwingConfirmedAt is { } watermark
                ? snapshot.Swings.Where(swing => swing.ConfirmedAt > watermark).ToArray()
                : snapshot.Swings;
            if (newSwings.Count > 0)
            {
                state.LastProcessedSwingConfirmedAt = newSwings.Max(swing => swing.ConfirmedAt);
            }

            state.StochRsi.Update(candle, snapshot.Indicators.StochRsi.Fast, newSwings, snapshot.Indicators.Atr);
            state.LastProcessedCandleOpenTime = candle.OpenTime;
        }

        return ClassifyExtreme(snapshot, _options, expectedDirection);
    }

    /// <summary>
    /// Builds a signal from <paramref name="interval"/>'s own just-updated StochRSI-fast relationship,
    /// requiring it to be freshly confirmed at this exact pivot (an older, aging relationship says
    /// nothing about whether *this* extreme is exhausted or confirmed) and directionally consistent
    /// with the extreme.
    /// </summary>
    private SignalMatch? TryBuildSignal(BarInterval interval, ExtremeDirection direction)
    {
        StochRsiAnalysisSnapshot analysis = _timeframeStates[interval].StochRsi.Current;
        if (!analysis.IsNewRelationship || analysis.LatestRelationship is not { } relationship)
            return null;

        bool isReversal = relationship.Type is
            StochRsiRelationshipType.RegularBullishDivergence or
            StochRsiRelationshipType.RegularBearishDivergence;
        bool relationshipMatchesDirection = direction == ExtremeDirection.Bullish
            ? relationship.Type is StochRsiRelationshipType.RegularBullishDivergence
                or StochRsiRelationshipType.HiddenBullishDivergence or StochRsiRelationshipType.BullishConvergence
            : relationship.Type is StochRsiRelationshipType.RegularBearishDivergence
                or StochRsiRelationshipType.HiddenBearishDivergence or StochRsiRelationshipType.BearishConvergence;
        if (!relationshipMatchesDirection)
            return null;

        string classification = isReversal
            ? "reversal (regular divergence)"
            : "breakout continuation (confirmed by StochRSI-fast)";
        string reason = $"{direction} extreme on {interval}: {classification}. {relationship.Type}, " +
            $"strength {relationship.Strength:F0}.";

        return new SignalMatch(direction, isReversal, interval, relationship, reason);
    }

    private AgentDecision EnterTrade(
        AgentMarketContext context,
        OrderSide side,
        BarInterval signalInterval,
        string reason,
        decimal confidence = 50m)
    {
        AnalysisSnapshot snapshot = context.Analysis.Get(signalInterval);
        decimal close = snapshot.LatestCandle.Prices.Close;
        decimal atr = snapshot.Indicators.Atr is > 0m ? snapshot.Indicators.Atr.Value : close * 0.002m;
        decimal stopDistance = atr * _options.ProtectiveStopAtrMultiple;
        bool buy = side == OrderSide.Buy;

        return new AgentDecision
        {
            Action = buy ? AgentAction.Buy : AgentAction.Sell,
            Instrument = context.Instrument,
            SuggestedQuantity = _options.Quantity,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = close,
            StopLossPrice = buy ? close - stopDistance : close + stopDistance,
            StopSource = "DivergenceReversalProtectiveStop",
            Confidence = confidence,
            CreatedAt = context.Timestamp,
            SignalInterval = signalInterval,
            Reason = reason
        };
    }

    private static (ExtremeCertainty Certainty, ExtremeDirection Direction)? ClassifyExtreme(
        AnalysisSnapshot snapshot,
        DivergenceReversalStrategyOptions options,
        ExtremeDirection? expectedDirection)
    {
        IndicatorSnapshot indicators = snapshot.Indicators;
        if (indicators.BollingerUpper is not decimal upper ||
            indicators.BollingerLower is not decimal lower ||
            indicators.Rsi is not decimal rsi ||
            indicators.StochRsi.Fast is not decimal stochRsiFast)
        {
            return null;
        }

        Candle candle = snapshot.LatestCandle;
        decimal close = candle.Prices.Close;

        bool checkBearish = expectedDirection is null or ExtremeDirection.Bearish;
        bool checkBullish = expectedDirection is null or ExtremeDirection.Bullish;

        if (checkBearish)
        {
            if (close >= upper && stochRsiFast >= options.StochRsiFastOverbought && rsi >= options.RsiOverbought)
                return (ExtremeCertainty.Full, ExtremeDirection.Bearish);
            if (expectedDirection is null &&
                (candle.Prices.High >= upper || stochRsiFast >= options.PartialStochRsiFastOverbought))
            {
                return (ExtremeCertainty.Partial, ExtremeDirection.Bearish);
            }
        }

        if (checkBullish)
        {
            if (close <= lower && stochRsiFast <= options.StochRsiFastOversold && rsi <= options.RsiOversold)
                return (ExtremeCertainty.Full, ExtremeDirection.Bullish);
            if (expectedDirection is null &&
                (candle.Prices.Low <= lower || stochRsiFast <= options.PartialStochRsiFastOversold))
            {
                return (ExtremeCertainty.Partial, ExtremeDirection.Bullish);
            }
        }

        return null;
    }

    private enum ExtremeCertainty
    {
        Partial,
        Full
    }

    private enum ExtremeDirection
    {
        Bullish,
        Bearish
    }

    private readonly record struct SignalMatch(
        ExtremeDirection Direction,
        bool IsReversal,
        BarInterval SignalInterval,
        StochRsiRelationshipSnapshot Relationship,
        string Reason);

    private sealed class TimeframeState(StochRsiAnalysisState stochRsi)
    {
        public StochRsiAnalysisState StochRsi { get; } = stochRsi;
        public DateTimeOffset? LastProcessedCandleOpenTime { get; set; }
        public DateTimeOffset? LastProcessedSwingConfirmedAt { get; set; }
    }
}
