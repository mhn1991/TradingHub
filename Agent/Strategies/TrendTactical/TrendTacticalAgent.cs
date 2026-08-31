using System.Collections.Concurrent;
using System.Threading;
using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using TradingClassifier.Features;
using TradingClassifier.Models;
using TrendStatistics.Detection;
using TrendStatistics.Profiles;
using TrendStatistics.Segmentation;
using TrendStatistics.Trading;
using TrendCandle = TrendStatistics.Data.Candle;

namespace Agent.Strategies.TrendTactical;

/// <summary>
/// Trend-gated tactical agent: the higher timeframe decides <b>whether and which direction</b>,
/// the classifier decides <b>when</b>.
/// <para>
/// Implements the composition specified in <c>Blueprint — 4H Statistical Trend Agent.md</c> §41-43,
/// which was designed but never built — §2.18 records that every TrendStatistics number came from a
/// research harness rather than the trading pipeline, because "there is nothing worth connecting to"
/// once the standalone classifier came back at PF 0.85-0.91.
/// </para>
/// <para>
/// The argument for building it anyway is that the classifier's <i>job changes</i>. Standalone it
/// was asked "where will price go from every bar?" and had no edge (§3.12g). Here it is only asked
/// "is this a good moment to enter a trend whose direction is already established?" — a strictly
/// easier question on a pre-filtered population, and one that has never been tested.
/// </para>
/// <para>
/// Exits are <see cref="AgentExitManagementMode.ProtectiveStopAndStrategyExit"/>, never a bracket.
/// §3.19 measured a fixed 3.0 ATR target as reachable by only 9.6% of trades, and declaring
/// <c>Bracket</c> also makes the platform hard-floor reward:risk at 1.5 and silently reject
/// everything below it — zero trades, no error.
/// </para>
/// </summary>
public sealed class TrendTacticalAgent : ITradingAgent
{
    private readonly TrendTacticalStrategyOptions _options;
    private readonly ITradingModel _model;

    // Optional. Null means "no stop model available", in which case the agent keeps its structural
    // stop rather than pretending to use one — a silently-defaulted stop would be indistinguishable
    // from a predicted one in the results.
    private readonly IStopPlacementModel? _stopModel;
    private readonly ConcurrentDictionary<InstrumentKey, InstrumentState> _state = new();

    public TrendTacticalAgent(
        TrendTacticalStrategyOptions options,
        ITradingModel model,
        IStopPlacementModel? stopModel = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        options.Validate();
        _options = options;
        _model = model;
        _stopModel = stopModel;
        RequiredIntervals = options.RequiredIntervals;
        TriggerInterval = options.TriggerInterval;
    }

    public string Name => "Trend-gated tactical agent";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval { get; }

    /// <summary>The strategy owns its exit: it holds while the trend holds.</summary>
    public AgentExitManagementMode ExitManagementMode =>
        AgentExitManagementMode.ProtectiveStopAndStrategyExit;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!context.Analysis.TryGet(_options.TriggerInterval, out AnalysisSnapshot trigger))
            return Observe(context, $"No {BarIntervalParser.Format(_options.TriggerInterval)} analysis.");
        if (!context.Analysis.TryGet(_options.TrendInterval, out AnalysisSnapshot trendSnapshot))
            return Observe(context, $"No {BarIntervalParser.Format(_options.TrendInterval)} analysis.");

        InstrumentState state = _state.GetOrAdd(context.Instrument, _ => new InstrumentState(_options));

        lock (state.Gate)
        {
            // ---- trend layer: replay the detector on CLOSED higher-timeframe candles only --------
            // Incremental replay, never a backfill of completed-trend direction or endpoints into
            // earlier bars — the causality requirement in the V2 document §4.3 / §6.2.
            Candle trendCandle = trendSnapshot.LatestCandle;
            if (state.LastTrendCandle != trendCandle.OpenTime)
            {
                state.LastTrendCandle = trendCandle.OpenTime;
                TrendCandle trendBar = new()
                {
                    Symbol = context.Instrument.Value,
                    OpenTime = trendCandle.OpenTime,
                    Open = trendCandle.Prices.Open,
                    High = trendCandle.Prices.High,
                    Low = trendCandle.Prices.Low,
                    Close = trendCandle.Prices.Close
                };
                TrendDetectorUpdate update = state.Detector.Apply(trendBar);
                state.Trend = update.State;

                // Completed trends accumulate as they finish. The profile is rebuilt from trends
                // that had ALREADY ENDED at this bar (TrendProfiles.BuildAsOf enforces it), so a
                // percentile can never be informed by a trend still running — the leakage rule the
                // research path calls section 49.
                if (update.CompletedTrend is TrendRecord completed)
                {
                    state.CompletedTrends.Add(completed);
                    state.Profile = SymbolTrendProfile.BuildAsOf(
                        context.Instrument.Value, state.CompletedTrends, trendBar.OpenTime);
                }
            }

            // Each secondary detector is driven by ITS OWN timeframe's snapshot, advanced only when
            // that timeframe closes a candle.
            //
            // They were previously fed the PRIMARY's closed candles, on the theory that this made
            // the gate merely conservative. It did not — a 30m detector fed 2h bars is just another
            // 2h detector, so it always agreed with the primary and the gate rejected nothing. The
            // three gate configurations produced byte-identical results, which is what a silent
            // no-op looks like from the outside.
            for (int index = 0; index < _options.SecondaryTrendMinutes.Count; index++)
            {
                BarInterval interval = BarInterval.Minutes(_options.SecondaryTrendMinutes[index]);
                if (!context.Analysis.TryGet(interval, out AnalysisSnapshot secondarySnapshot))
                    continue;

                Candle secondaryCandle = secondarySnapshot.LatestCandle;
                if (state.SecondaryLastCandle[index] == secondaryCandle.OpenTime)
                    continue;

                state.SecondaryLastCandle[index] = secondaryCandle.OpenTime;
                state.SecondaryTrends[index] = state.SecondaryDetectors[index].Apply(new TrendCandle
                {
                    Symbol = context.Instrument.Value,
                    OpenTime = secondaryCandle.OpenTime,
                    Open = secondaryCandle.Prices.Open,
                    High = secondaryCandle.Prices.High,
                    Low = secondaryCandle.Prices.Low,
                    Close = secondaryCandle.Prices.Close
                }).State;
            }

            TrendState trend = state.Trend;

            // ---- exits come before entries: an open position is managed, not re-signalled --------
            // BrokerPosition.Quantity is ABSOLUTE (SimulatedBrokerState.cs:835-836 sets
            // Quantity = Math.Abs(SignedQuantity) and puts direction in Side), so "Quantity > 0"
            // is true for shorts as well. Reading it as "long" closed shorts while their bearish
            // trend was intact and let them survive a reversal to bullish.
            bool hasPosition = context.Positions.Any(
                item => item.Instrument == context.Instrument && item.Quantity > 0m);

            // A position that was open last bar and is not now has closed — possibly by the
            // platform's own structural exit rather than by this agent, which is exactly the case
            // §3.22 measured (75 of 84 exits were StructuralInvalidation). The cooldown must start
            // from any close, not only from ones this agent asked for.
            if (state.HadPosition && !hasPosition)
                state.LastExitAt = context.Timestamp;
            state.HadPosition = hasPosition;

            if (hasPosition)
                return ManageOpenPosition(context, trend);

            if (_options.ReentryCooldownBars > 0 && state.LastExitAt is DateTimeOffset lastExit)
            {
                DateTimeOffset ready = lastExit;
                for (int step = 0; step < _options.ReentryCooldownBars; step++)
                    ready = _options.TriggerInterval.AddTo(ready);
                if (context.Timestamp < ready)
                {
                    return Observe(context,
                        $"Re-entry cooldown until {ready:HH:mm} ({_options.ReentryCooldownBars} bars).");
                }
            }

            // ---- direction gate (blueprint §41) --------------------------------------------------
            if (trend.Direction is not TrendDirection direction)
                return Observe(context, $"No trend direction yet (phase {trend.Phase}).");

            bool phaseAllows = trend.Phase == TrendPhase.Confirmed ||
                trend.Phase == TrendPhase.Mature ||
                (_options.TradeCandidatePhase && trend.Phase == TrendPhase.Candidate);
            if (!phaseAllows)
                return Observe(context, $"Trend phase {trend.Phase} does not permit new entries.");

            // Identify the trend by its structural start: a genuinely new trend re-arms the agent,
            // while the same trend continuing does not.
            DateTimeOffset? trendId = trend.StructuralStartTime ?? trend.ConfirmationTime;
            if (_options.OneEntryPerTrend && trendId is not null && state.LastTradedTrend == trendId)
            {
                return Observe(context,
                    $"This {BarIntervalParser.Format(_options.TrendInterval)} trend has already been traded.");
            }

            bool buy = direction == TrendDirection.Bullish;

            // ---- Rung A: the validated swing-entry rule -------------------------------------
            // Without this the agent enters on every eligible trigger bar, which is NOT the
            // strategy that produced §2.18's PF 1.694 and cannot be compared with it.
            if (_options.UseSwingEntryRule)
            {
                if (state.Profile is not SymbolTrendProfile profile)
                    return Observe(context, "No trend profile yet; not enough completed trends.");

                DirectionTrendProfile directional = profile.For(direction);
                if (directional.SampleCount < _options.MinimumTrendSamples)
                {
                    return Observe(context,
                        $"Profile has {directional.SampleCount} completed {direction} trends; " +
                        $"{_options.MinimumTrendSamples} required.");
                }

                SwingSignal signal = state.SwingSignals.Evaluate(trend, directional);
                if (!signal.IsActionable)
                    return Observe(context, $"Swing entry rule declined: {signal.Reason}");

                // The rule owns direction as well as timing; disagreeing with the detector would
                // mean two different answers to the same question.
                buy = signal.Action == SwingSignalAction.Buy;
            }

            // Secondary-timeframe gate: reject only when a secondary trend actively opposes.
            for (int index = 0; index < _options.SecondaryTrendMinutes.Count; index++)
            {
                TrendDirection? secondary = state.SecondaryTrends[index].Direction;
                if (secondary is TrendDirection opposing &&
                    (opposing == TrendDirection.Bullish) != buy)
                {
                    return Observe(context,
                        $"{_options.SecondaryTrendMinutes[index]}m trend is {opposing}, opposing the " +
                        $"{BarIntervalParser.Format(_options.TrendInterval)} direction.");
                }
            }

            // ---- entry layer: the classifier only times the trend's own direction ----------------
            IndicatorSnapshot indicators = trigger.Indicators;
            if (indicators.Atr is not decimal atr || atr <= 0m)
                return Observe(context, "ATR is not ready for stop placement.");

            Candle bar = trigger.LatestCandle;
            if (state.LastTriggerCandle == bar.OpenTime)
                return Observe(context, "This trigger candle has already been evaluated.");
            state.LastTriggerCandle = bar.OpenTime;

            // Rung B: trend gate only, no ML. Also the bootstrap path — candidates cannot be
            // generated to train or select features on until something can enter.
            if (!_options.RequireClassifierAgreement)
            {
                decimal ruleStop = StructuralStop(trigger, buy, bar.Prices.Close, atr);
                if (buy ? ruleStop >= bar.Prices.Close : ruleStop <= bar.Prices.Close)
                    return Observe(context, "Structural stop is on the wrong side of entry.");

                state.LastTradedTrend = trendId;

                return Task.FromResult(new AgentDecision
                {
                    Action = buy ? AgentAction.Buy : AgentAction.Sell,
                    Instrument = context.Instrument,
                    CreatedAt = context.Timestamp,
                    Confidence = 50m,
                    Reason =
                        $"Rung B (no ML): {BarIntervalParser.Format(_options.TrendInterval)} trend " +
                        $"{direction} in phase {trend.Phase}, {trend.CurrentMovePct:F2}% over " +
                        $"{trend.DurationBars} bars.",
                    SuggestedQuantity = _options.Quantity,
                    ReferencePrice = bar.Prices.Close,
                    StopLossPrice = ruleStop,
                    SignalInterval = _options.TriggerInterval,
                    StopSource = $"structural, floored at {_options.StopAtrMultiple:F2} ATR"
                });
            }

            FeatureVector? features = state.Engine.Update(
                new ClassifierCandle(
                    trigger.AvailableAt,
                    bar.Prices.Open, bar.Prices.High, bar.Prices.Low, bar.Prices.Close),
                trigger);
            if (features is null)
                return Observe(context, "Feature engine is still warming up.");

            Prediction prediction = _model.Predict(features);
            double agreeing = buy ? prediction.BuyProbability : prediction.SellProbability;

            // Blueprint §43: the older the trend, the more confidence a new entry must show.
            double threshold = _options.BaseEntryThreshold +
                (trend.Phase == TrendPhase.Mature ? _options.MatureThresholdStep : 0);
            if (agreeing < threshold)
            {
                return Observe(context,
                    $"Classifier p={agreeing:F3} below the {threshold:F2} required in phase {trend.Phase}.");
            }

            decimal entry = bar.Prices.Close;
            decimal stop = PredictedStop(features, buy, entry, atr)
                ?? StructuralStop(trigger, buy, entry, atr);
            if (buy ? stop >= entry : stop <= entry)
                return Observe(context, "Structural stop is on the wrong side of entry.");

            state.LastTradedTrend = trendId;

            return Task.FromResult(new AgentDecision
            {
                Action = buy ? AgentAction.Buy : AgentAction.Sell,
                Instrument = context.Instrument,
                CreatedAt = context.Timestamp,
                Confidence = (decimal)agreeing * 100m,
                Reason =
                    $"{BarIntervalParser.Format(_options.TrendInterval)} trend {direction} " +
                    $"({trend.Phase}, {trend.CurrentMovePct:F2}% over {trend.DurationBars} bars); " +
                    $"classifier p={agreeing:F3} >= {threshold:F2}.",
                SuggestedQuantity = _options.Quantity,
                ReferencePrice = entry,
                StopLossPrice = stop,
                // No take-profit on purpose: the trend decides the exit, not a fixed multiple.
                SignalInterval = _options.TriggerInterval,
                StopSource = $"structural, floored at {_options.StopAtrMultiple:F2} ATR"
            });
        }
    }

    /// <summary>Blueprint §43's tail: hold while the trend holds, leave when it does not.</summary>
    private Task<AgentDecision> ManageOpenPosition(AgentMarketContext context, TrendState trend)
    {
        // The quantity actually open — ExecutionCoordinator.ValidateDecision requires one on a
        // CLOSE just as much as on an entry, and a close without it throws mid-run rather than
        // being rejected. Sizing the exit from the position also means a partially reduced position
        // is closed for what remains, not for what was originally opened.
        decimal openQuantity = context.Positions
            .Where(item => item.Instrument == context.Instrument)
            .Sum(item => Math.Abs(item.Quantity));

        if (openQuantity <= 0m)
            return Observe(context, "No open quantity to manage.");

        if (_options.ExitOnExhaustion && trend.Phase == TrendPhase.Exhaustion)
            return Exit(context, openQuantity, "Trend reached Exhaustion.");

        if (trend.Phase == TrendPhase.Neutral)
            return Exit(context, openQuantity, "Trend is no longer active.");

        if (_options.ExitOnTrendReversal && trend.Direction is TrendDirection direction)
        {
            // Direction comes from Side. The previous check used Quantity > 0m, which is true for
            // every position, so shorts were evaluated as longs.
            bool longOpen = context.Positions.Any(item =>
                item.Instrument == context.Instrument && item.Quantity > 0m &&
                item.Side == OrderSide.Buy);
            bool shortOpen = context.Positions.Any(item =>
                item.Instrument == context.Instrument && item.Quantity > 0m &&
                item.Side == OrderSide.Sell);

            bool trendUp = direction == TrendDirection.Bullish;
            if ((longOpen && !trendUp) || (shortOpen && trendUp))
            {
                return Exit(context, openQuantity,
                    $"Opposite trend: {direction} against an open {(longOpen ? "long" : "short")}.");
            }
        }

        return Observe(context, $"Holding: trend still {trend.Direction} in phase {trend.Phase}.");
    }

    private static Task<AgentDecision> Exit(AgentMarketContext context, decimal quantity, string reason) =>
        Task.FromResult(new AgentDecision
        {
            Action = AgentAction.Close,
            Instrument = context.Instrument,
            CreatedAt = context.Timestamp,
            Confidence = 100m,
            SuggestedQuantity = quantity,
            Reason = reason
        });

    /// <summary>
    /// Stop placed beyond the model's expected adverse excursion, or null when no model is wired or
    /// the option is off.
    /// <para>
    /// Adverse excursion is the only quantity measured with usable signal here (rho 0.0597 against
    /// favourable excursion's -0.0010), which is why the stop is the half of the bracket worth
    /// predicting. The safety multiple matters: a stop placed exactly at the EXPECTED adverse
    /// excursion is hit about half the time by construction, so it is deliberately placed beyond it.
    /// </para>
    /// <para>
    /// Bounded on both sides. An unbounded prediction would let one degenerate output size the risk
    /// on a live position.
    /// </para>
    /// </summary>
    private decimal? PredictedStop(FeatureVector features, bool buy, decimal entry, decimal atr)
    {
        if (!_options.UseMlStopPlacement || _stopModel is null || atr <= 0m)
            return null;

        decimal predicted = _stopModel.PredictAdverseExcursionAtr(features);
        if (predicted <= 0m)
            return null;

        decimal multiple = Math.Clamp(
            predicted * _options.MlStopSafetyMultiple,
            _options.MinimumStopAtrMultiple,
            _options.MaximumStopAtrMultiple);

        decimal distance = atr * multiple;
        return buy ? entry - distance : entry + distance;
    }

    /// <summary>
    /// Stop at the last opposing swing, floored at an ATR multiple so a swing sitting almost on top
    /// of price cannot produce a stop that is stopped out by noise.
    /// </summary>
    private decimal StructuralStop(AnalysisSnapshot snapshot, bool buy, decimal entry, decimal atr)
    {
        decimal floor = atr * _options.StopAtrMultiple;
        decimal fallback = buy ? entry - floor : entry + floor;

        SwingPoint? pivot = null;
        for (int index = snapshot.Swings.Count - 1; index >= 0; index--)
        {
            SwingPoint swing = snapshot.Swings[index];
            if (buy && swing.Type == SwingType.Low && swing.Price < entry) { pivot = swing; break; }
            if (!buy && swing.Type == SwingType.High && swing.Price > entry) { pivot = swing; break; }
        }

        if (pivot is null)
            return fallback;

        return buy
            ? Math.Min(pivot.Price, fallback)
            : Math.Max(pivot.Price, fallback);
    }

    private static Task<AgentDecision> Observe(AgentMarketContext context, string reason) =>
        Task.FromResult(new AgentDecision
        {
            Action = AgentAction.Observe,
            Instrument = context.Instrument,
            CreatedAt = context.Timestamp,
            Confidence = 0m,
            Reason = reason
        });

    private sealed class InstrumentState
    {
        public InstrumentState(TrendTacticalStrategyOptions options)
        {
            Engine = new FeatureEngine(options.Classifier);
            Detector = new TrendDetector(new TrendDetectorConfig
            {
                Timeframe = TimeSpan.FromMinutes(
                    BarIntervalParser.ApproximateSeconds(options.TrendInterval) / 60.0)
            });
            SecondaryDetectors = [.. options.SecondaryTrendMinutes.Select(minutes =>
                new TrendDetector(new TrendDetectorConfig { Timeframe = TimeSpan.FromMinutes(minutes) }))];
            SecondaryTrends = [.. options.SecondaryTrendMinutes.Select(_ => TrendState.Neutral)];
            SecondaryLastCandle = new DateTimeOffset?[options.SecondaryTrendMinutes.Count];
            SwingSignals = new SwingSignalGenerator(new SwingEntryOptions
            {
                EntryPercentile = options.EntryPercentile,
                MaximumEntryPercentile = options.MaximumEntryPercentile,
                Mode = options.SwingEntryMode,
                MinimumTrendSamples = options.MinimumTrendSamples
            });
        }

        public TrendDetector[] SecondaryDetectors { get; }
        public List<TrendRecord> CompletedTrends { get; } = [];
        public SymbolTrendProfile? Profile { get; set; }
        public SwingSignalGenerator SwingSignals { get; }
        public TrendState[] SecondaryTrends { get; }
        public DateTimeOffset?[] SecondaryLastCandle { get; }

        public FeatureEngine Engine { get; }
        public TrendDetector Detector { get; }
        public Lock Gate { get; } = new();
        public TrendState Trend { get; set; } = TrendState.Neutral;
        public DateTimeOffset? LastTrendCandle { get; set; }
        public DateTimeOffset? LastTriggerCandle { get; set; }
        public DateTimeOffset? LastExitAt { get; set; }
        public DateTimeOffset? LastTradedTrend { get; set; }
        public bool HadPosition { get; set; }
    }
}
