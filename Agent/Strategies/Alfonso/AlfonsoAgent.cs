using System.Collections.Concurrent;
using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// The Set and Forget supply and demand agent: three timeframes decide direction and permission, a
/// zone decides the price, and the bracket decides the outcome.
/// <para>
/// The name is the method. Module 10: "Set and forget trading is as simple as its name implies, you
/// just set the trade up and then forget about it until the trade is triggered, either for a win or
/// a loss." So the agent places one order with its stop and target attached and does not modify the
/// bracket after entry: module 11 says, "Do not move the stop loss to breakeven. It's either a win
/// or a loss." An unfilled order is cancelled when its pre-planned scenario is invalidated.
/// </para>
/// <para>
/// The simulator routes Alfonso to bracket-only position management as well. Declaring
/// <see cref="AgentExitManagementMode"/> as <c>Bracket</c> controls the agent contract, while the
/// simulator-specific route prevents its generic manager from scaling out, trailing, or replacing
/// the fixed bracket after entry.
/// </para>
/// </summary>
public sealed class AlfonsoAgent : ITradingAgent
{
    private readonly AlfonsoStrategyOptions _options;
    private readonly ConcurrentDictionary<InstrumentKey, InstrumentState> _state = new();

    private readonly Action<AlfonsoCandidateRecord>? _candidateSink;
    private readonly Action<AlfonsoInventorySnapshot>? _inventorySink;

    /// <param name="options">Strategy configuration; defaults are the course's own values.</param>
    /// <param name="candidateSink">
    /// Optional observer of every candidate considered, including rejected ones. Null by default so
    /// production behaviour and cost are untouched; research harnesses pass a writer.
    /// </param>
    /// <param name="inventorySink">
    /// Optional sink for periodic zone-inventory snapshots. Null disables the measurement entirely.
    /// </param>
    public AlfonsoAgent(
        AlfonsoStrategyOptions? options = null,
        Action<AlfonsoCandidateRecord>? candidateSink = null,
        Action<AlfonsoInventorySnapshot>? inventorySink = null)
    {
        _candidateSink = candidateSink;
        _inventorySink = inventorySink;
        _options = options ?? new AlfonsoStrategyOptions();
        _options.Validate();
        RequiredIntervals = _options.RequiredIntervals;
        TriggerInterval = _options.LowerInterval;
    }

    public string Name => "Set and Forget supply/demand agent";

    public IReadOnlySet<BarInterval> RequiredIntervals { get; }

    public BarInterval TriggerInterval { get; }

    /// <summary>
    /// The bracket owns the exit. Entry, protection and target are decided together and never
    /// revised - that is what "set and forget" means.
    /// </summary>
    public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        InstrumentState state = _state.GetOrAdd(context.Instrument, _ => new InstrumentState(_options));

        lock (state.Gate)
        {
            // Every timeframe is advanced only by its OWN closed candles. Feeding one detector
            // another timeframe's bars turns it into a copy of that timeframe and it silently stops
            // disagreeing - the failure that made three gate configurations produce byte-identical
            // results in this repo before.
            bool topBarClosed = false;
            foreach ((SequenceRole role, BarInterval interval) in state.Intervals)
            {
                if (!context.Analysis.TryGet(interval, out AnalysisSnapshot snapshot))
                    return Observe(context, $"No {BarIntervalParser.Format(interval)} analysis.");

                Candle candle = snapshot.LatestCandle;
                if (state.LastCandle[role] == candle.OpenTime)
                    continue;

                state.LastCandle[role] = candle.OpenTime;
                if (role == SequenceRole.Top)
                {
                    state.RecordTopClose(candle.Prices.Close, _options.DriftLookbackCandles);
                    topBarClosed = true;
                }
                state.Analyzer.Apply(role, new AlfonsoBar(
                    candle.OpenTime,
                    candle.Prices.Open,
                    candle.Prices.High,
                    candle.Prices.Low,
                    candle.Prices.Close));
            }

            if (!context.Analysis.TryGet(_options.LowerInterval, out AnalysisSnapshot trigger))
                return Observe(context, "No execution timeframe analysis.");

            // An open position is left entirely alone. There is no position-management path in this
            // agent by design; module 11 is explicit that the trade is either a win or a loss.
            bool hasPosition = context.Positions.Any(
                item => item.Instrument == context.Instrument && item.Quantity > 0m);
            if (hasPosition)
            {
                state.PendingOrder = null;
                return Observe(context, "Position open; the bracket owns it.");
            }

            BrokerOrder? restingOrder = context.OpenOrders.FirstOrDefault(order =>
                order.Instrument == context.Instrument &&
                order.NormalizedStatus is OrderStatus.Pending or OrderStatus.Open or OrderStatus.PartiallyFilled);

            Candle bar = trigger.LatestCandle;
            if (state.LastEvaluated == bar.OpenTime)
                return Observe(context, "This execution candle has already been evaluated.");
            state.LastEvaluated = bar.OpenTime;

            decimal? atr = trigger.Indicators.Atr;
            if (atr is decimal sample && sample > 0m)
                state.RecordAtr(sample);

            if (topBarClosed)
                RecordInventory(context, state, bar.Prices.Close, atr);

            decimal? atrPercentile = state.AtrPercentile(atr);
            bool atrRegimeOk = atrPercentile is not decimal band ||
                (band >= _options.MinimumAtrPercentile && band <= _options.MaximumAtrPercentile);

            ScenarioResolution scenario = state.Analyzer.Scenario;

            if (restingOrder is not null)
            {
                if (state.PendingOrder is not ZoneOrderKey planned)
                    return Observe(context, "A resting order already owns the next entry.");

                IReadOnlyList<TradeCandidate> live = state.Analyzer.Candidates(bar.Prices.Close);
                TradeCandidate? held = live.FirstOrDefault(candidate =>
                    Key(candidate) == planned &&
                    IsAheadOfPrice(candidate.Side, candidate.Zone.Proximal, bar.Prices.Close));

                if (held is not null)
                {
                    // Candidates arrive sorted by distance, so the first is the best available now.
                    // Holding a far commitment while a much nearer level is on offer is what leaves
                    // the only order slot occupied by something that will not fill.
                    if (_options.RestingOrderReplacementAtr > 0m &&
                        atr is decimal unit && unit > 0m &&
                        live.Count > 0)
                    {
                        decimal heldAway = Math.Abs(bar.Prices.Close - held.Zone.Proximal);
                        decimal bestAway = Math.Abs(bar.Prices.Close - live[0].Zone.Proximal);
                        if ((heldAway - bestAway) / unit >= _options.RestingOrderReplacementAtr)
                        {
                            state.PendingOrder = null;
                            return Task.FromResult(new AgentDecision
                            {
                                Action = AgentAction.Cancel,
                                Instrument = context.Instrument,
                                BrokerOrderId = restingOrder.BrokerOrderId,
                                CreatedAt = context.Timestamp,
                                Confidence = 0m,
                                Reason =
                                    $"A level {(heldAway - bestAway) / unit:F1} ATR nearer is available; " +
                                    "releasing the slot rather than holding a commitment that will not fill."
                            });
                        }
                    }

                    return Observe(context, "The pre-planned limit remains valid and is awaiting its first touch.");
                }

                // Module 11 requires the plan to define conditions that cancel a pending trade and
                // says set-and-forget entries remain available only while trend/realignment still
                // apply. Cancelling an invalid entry is not management of an open position.
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Cancel,
                    Instrument = context.Instrument,
                    BrokerOrderId = restingOrder.BrokerOrderId,
                    CreatedAt = context.Timestamp,
                    Confidence = 0m,
                    Reason = "Cancel the resting Alfonso entry: its zone or timeframe realignment is no longer valid."
                });
            }

            // A prior submission was rejected, expired, cancelled, or filled without leaving a
            // position. It must not reserve the zone forever; broker context is the source of truth.
            state.PendingOrder = null;

            if (!scenario.CanTrade)
                return Observe(context, scenario.Reason);

            state.Analyzer.ReferenceAtr = atr;
            state.Analyzer.ReachableAtr = _options.ReachableDistanceAtr;
            IReadOnlyList<TradeCandidate> candidates = state.Analyzer.Candidates(bar.Prices.Close);
            if (candidates.Count == 0)
                return Observe(context, $"{scenario.Reason} No plannable zone ahead of price.");

            // A limit order has to exist before the first touch. The simulator executes resting
            // orders before this closed candle reaches the agent, exactly as a live broker would.
            foreach (TradeCandidate candidate in candidates)
            {
                Imbalance zone = candidate.Zone;
                ZoneOrderKey key = Key(candidate);

                bool firstPullback = _options.RequireReversalConfirmation &&
                    zone.State == ImbalanceState.Tested && zone.TestCount <= 1;
                if (_options.FreshLevelsOnly && zone.State != ImbalanceState.Fresh && !firstPullback &&
                    !(_options.AllowConfirmationEntries && candidate.Host is not null &&
                      zone.State == ImbalanceState.Tested))
                {
                    LogSimple(context, candidate, bar, atrPercentile, CandidateOutcome.NotFresh);
                    continue;
                }

                if (_options.RequireNestedEntries && candidate.Host is null)
                    continue;

                bool buy = candidate.Side == ImbalanceKind.Demand;

                // Only trade the side the drift is already pushing price toward. The opposite side
                // is the one a drifting market keeps filling and then running over.
                if (_options.RequireDriftAlignment &&
                    state.Drift(_options.DriftLookbackCandles) is decimal drift && drift != 0m &&
                    (buy ? drift < 0m : drift > 0m))
                {
                    LogSimple(context, candidate, bar, atrPercentile, CandidateOutcome.Superseded);
                    continue;
                }

                if (_options.MaximumPlacementDistanceAtr > 0m &&
                    atr is decimal reach && reach > 0m &&
                    Math.Abs(bar.Prices.Close - zone.Proximal) / reach >
                        _options.MaximumPlacementDistanceAtr)
                {
                    LogSimple(context, candidate, bar, atrPercentile, CandidateOutcome.TooFarToFill);
                    continue;
                }

                decimal stop = zone.StopPrice(_options.Zones.StopPaddingFraction);
                decimal reference = zone.Proximal;

                if (!_options.RequireReversalConfirmation)
                {
                    if (!IsAheadOfPrice(candidate.Side, zone.Proximal, bar.Prices.Close))
                    {
                        LogSimple(context, candidate, bar, atrPercentile, CandidateOutcome.NotReached);
                        continue;
                    }
                }
                else
                {
                    // "Price reached the level" has to come from the zone engine, not from this
                    // candle. The agent is triggered on the lower interval and sees a sampled bar,
                    // so testing the bar's own low/high misses any touch that happened between
                    // samples - which is nearly all of them, and gave 2 trades over six instruments.
                    // The engine already records the touch continuously on the zone's own timeframe.
                    bool reached = zone.State == ImbalanceState.Tested;

                    bool heldBack = buy
                        ? bar.Prices.Close > zone.Proximal
                        : bar.Prices.Close < zone.Proximal;
                    bool survived = buy
                        ? bar.Prices.Close > zone.Distal
                        : bar.Prices.Close < zone.Distal;

                    if (!reached || !heldBack || !survived)
                    {
                        LogSimple(context, candidate, bar, atrPercentile, CandidateOutcome.NotReached);
                        continue;
                    }

                    // Entry is at market on this close, so risk is measured from there.
                    reference = bar.Prices.Close;
                }

                decimal risk = Math.Abs(reference - stop);
                decimal target = _options.RequireReversalConfirmation
                    ? (buy
                        ? reference + (_options.Zones.RewardMultiple * risk)
                        : reference - (_options.Zones.RewardMultiple * risk))
                    : zone.TargetPrice(
                        _options.Zones.StopPaddingFraction, _options.Zones.RewardMultiple);
                decimal? costToRisk = context.RoundTripCostEstimate is decimal cost && risk > 0m
                    ? cost / risk
                    : null;
                decimal? stopAtr = atr is decimal unit && unit > 0m ? risk / unit : null;

                if (buy ? stop >= reference || target <= reference
                        : stop <= reference || target >= reference)
                {
                    Log(context, candidate, bar, stop, target, risk, costToRisk, stopAtr,
                        atrPercentile, CandidateOutcome.DegenerateGeometry);
                    continue;
                }

                // A stop this tight loses a large share of its planned R before price moves, and no
                // target multiple recovers that. The structural stop is untouched; the trade is
                // simply refused.
                if (_options.MaximumCostToRiskFraction > 0m &&
                    costToRisk is decimal ratio && ratio > _options.MaximumCostToRiskFraction)
                {
                    Log(context, candidate, bar, stop, target, risk, costToRisk, stopAtr,
                        atrPercentile, CandidateOutcome.CostTooHigh);
                    continue;
                }

                if (_options.MinimumStopAtrMultiple > 0m &&
                    stopAtr is decimal multiple && multiple < _options.MinimumStopAtrMultiple)
                {
                    Log(context, candidate, bar, stop, target, risk, costToRisk, stopAtr,
                        atrPercentile, CandidateOutcome.StopTooTight);
                    continue;
                }

                if (!atrRegimeOk)
                {
                    Log(context, candidate, bar, stop, target, risk, costToRisk, stopAtr,
                        atrPercentile, CandidateOutcome.AtrRegime);
                    continue;
                }

                Log(context, candidate, bar, stop, target, risk, costToRisk, stopAtr,
                    atrPercentile, CandidateOutcome.Entered);
                state.PendingOrder = key;

                return Task.FromResult(new AgentDecision
                {
                    Action = buy ? AgentAction.Buy : AgentAction.Sell,
                    Instrument = context.Instrument,
                    CreatedAt = context.Timestamp,
                    Confidence = zone.Strength == ImpulseStrength.Gap ? 90m
                        : zone.Strength == ImpulseStrength.Strong ? 75m : 50m,
                    Reason =
                        $"{candidate.Reason} Zone {zone.Proximal:F2}/{zone.Distal:F2} " +
                        $"({zone.Strength}, {zone.ImpulseToBaseRatio:F2}:1, {zone.Accomplished})" +
                        (candidate.Host is null ? "." : $", nested at {candidate.Host.Proximal:F2}."),
                    SuggestedQuantity = _options.Quantity,
                    OrderType = _options.RequireReversalConfirmation
                        ? StandardOrderType.Market
                        : StandardOrderType.Limit,
                    LimitPrice = _options.RequireReversalConfirmation ? null : zone.Proximal,
                    ReferencePrice = reference,
                    StopLossPrice = stop,
                    TakeProfitPrice = target,
                    SignalInterval = _options.LowerInterval,
                    StopSource =
                        $"distal {zone.Distal:F2} padded {_options.Zones.StopPaddingFraction:P0}, " +
                        $"target {_options.Zones.RewardMultiple:F1}:1"
                });
            }

            return Observe(context,
                $"{scenario.Reason} {candidates.Count} zone(s) considered; none can take a new resting order.");
        }
    }

    /// <summary>
    /// Snapshots how many zones are live per timeframe and side, and how far they sit from price.
    /// <para>
    /// What the agent can trade is decided by where its zones are: fill rate falls from 21-35%
    /// inside 3 ATR to zero beyond 16, and about 60% of placements sit where fills never happen.
    /// Counting placements without weighting by distance treats inert orders as intent, which is
    /// how "intentions are near-symmetric" was concluded from a population that is 1.88:1
    /// supply-heavy once restricted to reachable zones.
    /// </para>
    /// </summary>
    private void RecordInventory(
        AgentMarketContext context, InstrumentState state, decimal price, decimal? atr)
    {
        if (_inventorySink is null)
            return;

        foreach ((SequenceRole role, _) in _options.Sequence.All())
        {
            IReadOnlyList<Imbalance> zones = state.Analyzer.ZonesOf(role);
            foreach (ImbalanceKind kind in new[] { ImbalanceKind.Demand, ImbalanceKind.Supply })
            {
                List<decimal> distances = [];
                int live = 0;
                foreach (Imbalance zone in zones)
                {
                    if (zone.Kind != kind || zone.State == ImbalanceState.Eliminated)
                        continue;

                    live++;
                    if (atr is decimal unit && unit > 0m)
                        distances.Add(Math.Abs(price - zone.Proximal) / unit);
                }

                // The same measurement for zones that would actually be eligible, evaluated for
                // BOTH sides so it is not conditioned on which side the scenario happens to permit.
                List<decimal> qualifying = [];
                foreach (Imbalance zone in state.Analyzer.TradeableZonesOf(role, kind, price))
                {
                    if (atr is decimal q && q > 0m)
                        qualifying.Add(Math.Abs(price - zone.Proximal) / q);
                }

                qualifying.Sort();
                distances.Sort();
                _inventorySink(new AlfonsoInventorySnapshot
                {
                    At = context.Timestamp,
                    Instrument = context.Instrument,
                    Role = role,
                    Kind = kind,
                    LiveZones = live,
                    Reachable = distances.Count(d => d <= _options.ReachableDistanceAtr),
                    Qualifying = qualifying.Count,
                    NearestQualifyingAtr = qualifying.Count == 0 ? null : qualifying[0],
                    MedianDistanceAtr = distances.Count == 0 ? null : distances[distances.Count / 2],
                    NearestDistanceAtr = distances.Count == 0 ? null : distances[0],
                    Price = price,
                    Atr = atr,
                    Filters = role == SequenceRole.Lower ? state.Analyzer.TallyOf(kind) : null
                });
            }
        }
    }

    private void Log(
        AgentMarketContext context, TradeCandidate candidate, Candle bar,
        decimal stop, decimal target, decimal risk, decimal? costToRisk, decimal? stopAtr,
        decimal? atrPercentile, CandidateOutcome outcome)
    {
        if (_candidateSink is null)
            return;

        Imbalance zone = candidate.Zone;
        _state.TryGetValue(context.Instrument, out InstrumentState? logged);
        _candidateSink(new AlfonsoCandidateRecord
        {
            At = context.Timestamp,
            TopTrend = logged?.Analyzer.TrendOf(SequenceRole.Top),
            MiddleTrend = logged?.Analyzer.TrendOf(SequenceRole.Middle),
            LowerTrend = logged?.Analyzer.TrendOf(SequenceRole.Lower),
            Instrument = context.Instrument,
            Outcome = outcome,
            Side = candidate.Side,
            EntryTimeframe = candidate.EntryTimeframe,
            Proximal = zone.Proximal,
            Distal = zone.Distal,
            Stop = stop,
            Target = target,
            Risk = risk,
            MarketPrice = bar.Prices.Close,
            Nested = candidate.Host is not null,
            IsContinuationPattern = zone.IsContinuationPattern,
            State = zone.State,
            Strength = zone.Strength,
            Accomplished = zone.Accomplished,
            ImpulseToBaseRatio = zone.ImpulseToBaseRatio,
            ImpulseDisplacement = zone.ImpulseDisplacement,
            BaseCandleCount = zone.BaseCandleCount,
            CostToRisk = costToRisk,
            StopAtrMultiple = stopAtr,
            AtrPercentile = atrPercentile,
            Scenario = candidate.Reason
        });
    }

    /// <summary>Record for a candidate rejected before its geometry was computed.</summary>
    private void LogSimple(
        AgentMarketContext context, TradeCandidate candidate, Candle bar,
        decimal? atrPercentile, CandidateOutcome outcome)
    {
        if (_candidateSink is null)
            return;

        Imbalance zone = candidate.Zone;
        decimal stop = zone.StopPrice(_options.Zones.StopPaddingFraction);
        Log(context, candidate, bar, stop,
            zone.TargetPrice(_options.Zones.StopPaddingFraction, _options.Zones.RewardMultiple),
            Math.Abs(zone.Proximal - stop), null, null, atrPercentile, outcome);
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

    internal static bool IsAheadOfPrice(ImbalanceKind side, decimal proximal, decimal price) =>
        side == ImbalanceKind.Demand ? price > proximal : price < proximal;

    private static ZoneOrderKey Key(TradeCandidate candidate) =>
        new(candidate.EntryTimeframe, candidate.Zone.BaseEnd, candidate.Zone.Kind);

    private sealed class InstrumentState
    {
        private readonly List<decimal> _topCloses = [];

        /// <summary>Keeps just enough top-timeframe history to measure drift over the lookback.</summary>
        public void RecordTopClose(decimal close, int lookback)
        {
            if (lookback <= 0)
                return;

            _topCloses.Add(close);
            int keep = lookback + 1;
            if (_topCloses.Count > keep)
                _topCloses.RemoveRange(0, _topCloses.Count - keep);
        }

        /// <summary>Change in top-timeframe close over the lookback, or null before enough history.</summary>
        public decimal? Drift(int lookback)
        {
            if (lookback <= 0 || _topCloses.Count <= lookback)
                return null;

            return _topCloses[^1] - _topCloses[^(lookback + 1)];
        }


        public InstrumentState(AlfonsoStrategyOptions options)
        {
            Analyzer = new AlfonsoSequenceAnalyzer(
                options.Sequence, options.Zones, options.Trend, options.Range, options.FreshLevelsOnly,
                options.RequireControlAgreement, options.AllowConfirmationEntries,
                options.MinimumProfitMarginMultiple, options.Zones.StopPaddingFraction,
                options.RequireReversalConfirmation);

            Intervals =
            [
                (SequenceRole.Top, options.TopInterval),
                (SequenceRole.Middle, options.MiddleInterval),
                (SequenceRole.Lower, options.LowerInterval)
            ];

            AtrLookback = options.AtrPercentileLookback;
            foreach ((SequenceRole role, _) in Intervals)
                LastCandle[role] = null;
        }

        public object Gate { get; } = new();

        public AlfonsoSequenceAnalyzer Analyzer { get; }

        public IReadOnlyList<(SequenceRole Role, BarInterval Interval)> Intervals { get; }

        public Dictionary<SequenceRole, DateTimeOffset?> LastCandle { get; } = [];

        public DateTimeOffset? LastEvaluated { get; set; }

        public ZoneOrderKey? PendingOrder { get; set; }

        private readonly List<decimal> _atrHistory = [];

        public int AtrLookback { get; init; } = 200;

        /// <summary>Keeps a bounded window of execution-timeframe ATR for the percentile veto.</summary>
        public void RecordAtr(decimal value)
        {
            _atrHistory.Add(value);
            if (_atrHistory.Count > AtrLookback)
                _atrHistory.RemoveRange(0, _atrHistory.Count - AtrLookback);
        }

        /// <summary>
        /// Where <paramref name="value"/> sits in the retained history, or null until the window has
        /// filled. Returning null rather than a provisional figure keeps the veto from firing on a
        /// percentile computed from a handful of bars.
        /// </summary>
        public decimal? AtrPercentile(decimal? value)
        {
            if (value is not decimal current || _atrHistory.Count < AtrLookback)
                return null;

            int below = _atrHistory.Count(sample => sample < current);
            return (decimal)below / _atrHistory.Count;
        }
    }

    private readonly record struct ZoneOrderKey(
        SequenceRole Role,
        DateTimeOffset BaseEnd,
        ImbalanceKind Kind);
}
