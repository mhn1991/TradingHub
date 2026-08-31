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

    public AlfonsoAgent(AlfonsoStrategyOptions? options = null)
    {
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
            foreach ((SequenceRole role, BarInterval interval) in state.Intervals)
            {
                if (!context.Analysis.TryGet(interval, out AnalysisSnapshot snapshot))
                    return Observe(context, $"No {BarIntervalParser.Format(interval)} analysis.");

                Candle candle = snapshot.LatestCandle;
                if (state.LastCandle[role] == candle.OpenTime)
                    continue;

                state.LastCandle[role] = candle.OpenTime;
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

            ScenarioResolution scenario = state.Analyzer.Scenario;

            if (restingOrder is not null)
            {
                if (state.PendingOrder is not ZoneOrderKey planned)
                    return Observe(context, "A resting order already owns the next entry.");

                bool stillValid = scenario.CanTrade && state.Analyzer.Candidates(bar.Prices.Close)
                    .Any(candidate =>
                        Key(candidate) == planned &&
                        IsAheadOfPrice(candidate.Side, candidate.Zone.Proximal, bar.Prices.Close));
                if (stillValid)
                    return Observe(context, "The pre-planned limit remains valid and is awaiting its first touch.");

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

            IReadOnlyList<TradeCandidate> candidates = state.Analyzer.Candidates(bar.Prices.Close);
            if (candidates.Count == 0)
                return Observe(context, $"{scenario.Reason} No plannable zone ahead of price.");

            // A limit order has to exist before the first touch. The simulator executes resting
            // orders before this closed candle reaches the agent, exactly as a live broker would.
            foreach (TradeCandidate candidate in candidates)
            {
                Imbalance zone = candidate.Zone;
                ZoneOrderKey key = Key(candidate);

                if (_options.FreshLevelsOnly && zone.State != ImbalanceState.Fresh)
                    continue;

                bool aheadOfPrice = IsAheadOfPrice(
                    candidate.Side, zone.Proximal, bar.Prices.Close);
                if (!aheadOfPrice)
                    continue;

                decimal stop = zone.StopPrice(_options.Zones.StopPaddingFraction);
                decimal target = zone.TargetPrice(
                    _options.Zones.StopPaddingFraction, _options.Zones.RewardMultiple);

                bool buy = candidate.Side == ImbalanceKind.Demand;
                if (buy ? stop >= zone.Proximal || target <= zone.Proximal
                        : stop <= zone.Proximal || target >= zone.Proximal)
                {
                    continue;
                }

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
                    OrderType = StandardOrderType.Limit,
                    LimitPrice = zone.Proximal,
                    ReferencePrice = zone.Proximal,
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
        public InstrumentState(AlfonsoStrategyOptions options)
        {
            Analyzer = new AlfonsoSequenceAnalyzer(
                options.Sequence, options.Zones, options.Trend, options.Range, options.FreshLevelsOnly);

            Intervals =
            [
                (SequenceRole.Top, options.TopInterval),
                (SequenceRole.Middle, options.MiddleInterval),
                (SequenceRole.Lower, options.LowerInterval)
            ];

            foreach ((SequenceRole role, _) in Intervals)
                LastCandle[role] = null;
        }

        public object Gate { get; } = new();

        public AlfonsoSequenceAnalyzer Analyzer { get; }

        public IReadOnlyList<(SequenceRole Role, BarInterval Interval)> Intervals { get; }

        public Dictionary<SequenceRole, DateTimeOffset?> LastCandle { get; } = [];

        public DateTimeOffset? LastEvaluated { get; set; }

        public ZoneOrderKey? PendingOrder { get; set; }
    }

    private readonly record struct ZoneOrderKey(
        SequenceRole Role,
        DateTimeOffset BaseEnd,
        ImbalanceKind Kind);
}
