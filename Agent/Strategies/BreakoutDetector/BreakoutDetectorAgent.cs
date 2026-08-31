using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using Networking.Notifications;

namespace Agent.Strategies.BreakoutDetector;

/// <summary>
/// Detects a breakout out of a consolidation range on
/// <see cref="BreakoutDetectorStrategyOptions.TriggerInterval"/>, qualified by
/// <see cref="BreakoutDetectorStrategyOptions.ContextInterval"/>, and enters with a bracket
/// (stop + target submitted with the entry - <see cref="AgentExitManagementMode.Bracket"/>).
/// <para>
/// <b>Scaffold.</b> The registration surface is complete - this agent is resolvable through
/// <c>TradingAgentCatalog</c>, serializable through <c>AgentDefinition</c>, and selectable from
/// the backtester as <c>breakout-detector</c> - but <see cref="EvaluateAsync"/> deliberately
/// returns <see cref="AgentAction.Observe"/> on every bar until the detection logic is written.
/// It will therefore produce zero trades, by construction, not by a data or configuration fault.
/// </para>
/// </summary>
public sealed class BreakoutDetectorAgent : ITradingAgent
{
    private readonly BreakoutDetectorStrategyOptions _options;
    private readonly ISignalNotifier _notifier;

    // One decision per trigger candle. The agent wakes on the 1m interval but reads the latest
    // TriggerInterval candle, so without this a single 5m snapshot was evaluated up to five times
    // and could emit several candidates from one source event - measured at 5.0-5.9% duplicate
    // candidates. Keyed by instrument because one agent instance can be reused across them.
    private readonly Dictionary<InstrumentKey, DateTimeOffset> _lastTriggerSeen = [];
    private readonly Dictionary<InstrumentKey, DateTimeOffset> _cooldownUntil = [];

    /// <param name="options">Validated strategy options; see <see cref="BreakoutDetectorStrategyOptions"/>.</param>
    /// <param name="notifier">
    /// Optional outbound alerting. Defaults to <see cref="NullSignalNotifier"/>, which is what
    /// keeps backtests silent: the catalogue builds this agent without a notifier, so a replay
    /// cannot emit traffic. Only a host that injects a real notifier sends anything.
    /// </param>
    public BreakoutDetectorAgent(
        BreakoutDetectorStrategyOptions options,
        ISignalNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _notifier = notifier ?? NullSignalNotifier.Instance;
        RequiredIntervals = options.RequiredIntervals;
        // The agent must wake on the FINEST timeframe: the 1m confirmation has to be consulted on
        // 1m closes to mean anything. Detection still happens on options.TriggerInterval.
        TriggerInterval = options.ConfirmationInterval;
    }

    public string Name => "Breakout detector agent";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval { get; }
    public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // A missing snapshot is a real, expected condition on warm-up bars and whenever a
        // configured interval was not requested by the host, so it is reported distinctly from
        // "evaluated and found nothing" rather than throwing.
        if (!context.Analysis.TryGet(_options.TriggerInterval, out AnalysisSnapshot trigger))
            return Observe(context, $"No {BarIntervalParser.Format(_options.TriggerInterval)} analysis available.");
        if (!context.Analysis.TryGet(_options.ContextInterval, out AnalysisSnapshot contextSnapshot))
            return Observe(context, $"No {BarIntervalParser.Format(_options.ContextInterval)} analysis available.");
        if (!context.Analysis.TryGet(_options.ConfirmationInterval, out AnalysisSnapshot confirmation))
            return Observe(context, $"No {BarIntervalParser.Format(_options.ConfirmationInterval)} analysis available.");
        

        // Already in a position: the bracket owns the exit, so do not re-signal every bar.
        if (context.Positions.Any(item => item.Instrument == context.Instrument && item.Quantity > 0m))
            return Observe(context, "A position is already open; its bracket owns the exit.");

        IndicatorSnapshot ind = trigger.Indicators;
        Candle candle = trigger.LatestCandle;

        // Unwrap every nullable explicitly. GetValueOrDefault() would turn a warm-up null into 0,
        // and comparing against a decimal? uses a lifted operator that yields false when the other
        // side is null - both read as "no signal" and are indistinguishable from a real one.
        if (ind.BollingerUpper is not decimal upperBand)
            return Observe(context, "Bollinger bands are still warming up.");
        if (ind.BollingerLower is not decimal lowerBand)
            return Observe(context, "Bollinger bands are still warming up.");
        if (ind.Atr is not decimal atr || atr <= 0m)
            return Observe(context, "ATR is not ready.");
        if (ind.Rsi is not decimal rsi || ind.RsiAnalysis.PreviousValue is not decimal previousRsi)
            return Observe(context, "RSI has no previous-candle comparison yet.");
        if (ind.Cci is not decimal cci || ind.CciAnalysis.PreviousValue is not decimal previousCci)
            return Observe(context, "CCI has no previous-candle comparison yet.");

        // One decision per trigger candle: a 5m snapshot re-read on each 1m wake-up is the same
        // source event, not a new one.
        if (_lastTriggerSeen.TryGetValue(context.Instrument, out DateTimeOffset lastTrigger) &&
            lastTrigger == candle.OpenTime)
        {
            return Observe(context, "This trigger candle has already been evaluated.");
        }
        _lastTriggerSeen[context.Instrument] = candle.OpenTime;

        if (_cooldownUntil.TryGetValue(context.Instrument, out DateTimeOffset until) &&
            context.Timestamp < until)
        {
            return Observe(context, $"Cooling down until {until:HH:mm} after the last signal.");
        }

        // End of a rising move: the candle tagged the upper band, RSI is still rising, but CCI
        // has already rolled over - momentum diverging while price makes its high. Sell.
        // STRICT comparisons: with >=/<= an unchanged reading counted as divergence, which is the
        // absence of the evidence the setup claims to require (measured at 1.8% of signals).
        bool endOfRise = candle.Prices.High >= upperBand && rsi > previousRsi && cci < previousCci;

        // Mirror image at the lower band: price makes its low, RSI still falling, CCI turning up. Buy.
        bool endOfDown = candle.Prices.Low <= lowerBand && rsi < previousRsi && cci > previousCci;

        // Both can be true at once - a wide candle can tag both bands, and the >=/<= comparisons
        // both hold when RSI or CCI is unchanged. That is a contradictory reading, so take
        // neither rather than letting evaluation order silently pick a side.
        if (endOfRise && endOfDown)
            return Observe(context, "Both end-of-move readings fired on one candle; ambiguous.");
        if (!endOfRise && !endOfDown)
            return Observe(context, "No end-of-move signal on this candle.");

        bool buy = endOfDown;

        // 15m CONTEXT: reject signals against the higher-timeframe structure. This turns the setup
        // from a pure band-fade into a pullback entry - buy the dip only while 15m structure is
        // rising, sell the rally only while it is falling. Sideways/Unknown is not a licence to
        // trade both ways, so it is rejected too.
        MarketStructureDirection context15m = contextSnapshot.MarketStructure.Direction;
        if (buy && context15m != MarketStructureDirection.Rising)
            return Observe(context, $"15m structure is {context15m}; a long fade needs Rising.");
        if (!buy && context15m != MarketStructureDirection.Falling)
            return Observe(context, $"15m structure is {context15m}; a short fade needs Falling.");

        // 1m CONFIRMATION: require the finest timeframe to have turned in the trade's direction.
        // Without it the entry is taken while the 1m is still running against the position.
        Candle confirmingCandle = confirmation.LatestCandle;
        bool confirmed = buy
            ? confirmingCandle.Prices.Close >= confirmingCandle.Prices.Open
            : confirmingCandle.Prices.Close <= confirmingCandle.Prices.Open;
        if (!confirmed)
            return Observe(context, "1m candle has not turned in the signal's direction yet.");

        decimal entry = candle.Prices.Close;
        decimal stop = buy
            ? entry - atr * _options.StopAtrMultiple
            : entry + atr * _options.StopAtrMultiple;
        decimal target = buy
            ? entry + atr * _options.TargetAtrMultiple
            : entry - atr * _options.TargetAtrMultiple; 

        decimal risk = Math.Abs(entry - stop);
        if (risk <= 0m)
            return Observe(context, "Degenerate stop distance.");

        // Computed from the geometry actually being submitted, not asserted. The hardcoded 2.0 was
        // a claim the bracket did not honour: measured executable reward:risk was 1.807 because the
        // fill lands a bar after the signal close the bracket is anchored to.
        decimal rewardRisk = Math.Abs(target - entry) / risk;
        if (rewardRisk < _options.MinimumRewardRisk)
        {
            return Observe(context,
                $"Reward:risk {rewardRisk:F2} is below the {_options.MinimumRewardRisk:F2} minimum.");
        }

        // CooldownCandles was a declared-but-unused option; it now suppresses a burst of
        // near-identical signals from consecutive trigger candles.
        DateTimeOffset cooldownUntil = context.Timestamp;
        for (int candles = 0; candles < _options.CooldownCandles; candles++)
            cooldownUntil = _options.TriggerInterval.AddTo(cooldownUntil);
        _cooldownUntil[context.Instrument] = cooldownUntil;

        return Task.FromResult(new AgentDecision
        {
            Action = buy ? AgentAction.Buy : AgentAction.Sell,
            Instrument = context.Instrument,
            CreatedAt = context.Timestamp,
            Confidence = 70m,
            Reason = buy
                ? $"End of falling move: low {candle.Prices.Low} tagged the lower band {lowerBand:F5}, " +
                  $"RSI {previousRsi:F1}->{rsi:F1} falling, CCI {previousCci:F0}->{cci:F0} turning up."
                : $"End of rising move: high {candle.Prices.High} tagged the upper band {upperBand:F5}, " +
                  $"RSI {previousRsi:F1}->{rsi:F1} rising, CCI {previousCci:F0}->{cci:F0} rolling over.",
            SuggestedQuantity = _options.Quantity,
            ReferencePrice = entry,
            StopLossPrice = stop,
            TakeProfitPrice = target,
            ExpectedRewardRisk = rewardRisk,
            SignalInterval = _options.TriggerInterval,
            // Buy: stop BELOW entry, target ABOVE. Sell: the reverse. PreTradeRiskManager rejects
            // the opposite arrangement, which surfaces as zero trades rather than an error.
            StopSource = $"{_options.StopAtrMultiple:F2} ATR {(buy ? "below" : "above")} entry",
            TargetSource = $"{_options.TargetAtrMultiple:F2} ATR {(buy ? "above" : "below")} entry"
        });

        // The two roles that justify three timeframes are wired above: contextSnapshot (15m)
        // qualifies direction, confirmation (1m) requires the finest timeframe to agree.
    }

    /// <summary>
    /// Sends a signal alert. Never throws and never changes the decision - alerting is a side
    /// effect, so a Telegram outage must not fail the evaluation that produced the signal.
    /// Returns whether the alert was delivered, which callers are free to ignore.
    /// </summary>
    private ValueTask<bool> NotifySignalAsync(
        AgentMarketContext context,
        SignalSide side,
        decimal? referencePrice,
        decimal? stopLossPrice,
        decimal? takeProfitPrice,
        decimal? confidence,
        string reason,
        CancellationToken cancellationToken) => _notifier.NotifyAsync(
            new SignalNotification
            {
                Instrument = context.Instrument.Value,
                Side = side,
                Strategy = Name,
                DecisionTime = context.Timestamp,
                Interval = BarIntervalParser.Format(_options.TriggerInterval),
                ReferencePrice = referencePrice,
                StopLossPrice = stopLossPrice,
                TakeProfitPrice = takeProfitPrice,
                Confidence = confidence,
                Reason = reason
            },
            cancellationToken);

    private static Task<AgentDecision> Observe(AgentMarketContext context, string reason) =>
        Task.FromResult(new AgentDecision
        {
            Action = AgentAction.Observe,
            Instrument = context.Instrument,
            Confidence = 0m,
            CreatedAt = context.Timestamp,
            Reason = reason
        });
}
