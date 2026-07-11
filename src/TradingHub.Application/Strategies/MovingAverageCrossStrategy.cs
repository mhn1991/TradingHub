using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;

namespace TradingHub.Application.Strategies;

public sealed class MovingAverageCrossStrategy : TradingStrategyBase
{
    private readonly MovingAverageCrossOptions _options;
    private readonly Dictionary<string, InstrumentState> _states = new(StringComparer.Ordinal);

    public MovingAverageCrossStrategy(MovingAverageCrossOptions options)
        : base(options.StrategyId)
    {
        options.EnsureValid();
        _options = options;
    }

    public override ValueTask<IReadOnlyList<TradeIntent>> OnBarAsync(
        StrategyContext context,
        PriceBar bar,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = GetState(bar.InstrumentId);
        AddPrice(state, bar.MidClose);

        if (state.Prices.Count < _options.LongPeriod)
        {
            return ValueTask.FromResult<IReadOnlyList<TradeIntent>>([]);
        }

        var relation = CompareAverages(state);
        var crossed = state.PreviousRelation is not null && relation != state.PreviousRelation && relation != 0;
        state.PreviousRelation = relation;

        if (!crossed || HasPositionInDirection(context, relation))
        {
            return ValueTask.FromResult<IReadOnlyList<TradeIntent>>([]);
        }

        var intent = CreateIntent(context, bar, relation);
        return ValueTask.FromResult<IReadOnlyList<TradeIntent>>([intent]);
    }

    private InstrumentState GetState(string instrumentId)
    {
        if (!_states.TryGetValue(instrumentId, out var state))
        {
            state = new InstrumentState();
            _states.Add(instrumentId, state);
        }

        return state;
    }

    private void AddPrice(InstrumentState state, decimal price)
    {
        state.Prices.Enqueue(price);
        if (state.Prices.Count > _options.LongPeriod)
        {
            state.Prices.Dequeue();
        }
    }

    private int CompareAverages(InstrumentState state)
    {
        var prices = state.Prices.ToArray();
        var longAverage = prices.Average();
        var shortAverage = prices[^_options.ShortPeriod..].Average();
        return shortAverage.CompareTo(longAverage);
    }

    private static bool HasPositionInDirection(StrategyContext context, int relation)
    {
        return relation > 0
            ? context.NetPositionQuantity > 0m
            : context.NetPositionQuantity < 0m;
    }

    private TradeIntent CreateIntent(StrategyContext context, PriceBar bar, int relation)
    {
        return new TradeIntent
        {
            IntentId = NextIntentId(bar.InstrumentId),
            StrategyId = Id,
            AccountId = context.AccountId,
            InstrumentId = bar.InstrumentId,
            Side = relation > 0 ? OrderSide.Buy : OrderSide.Sell,
            Quantity = _options.OrderQuantity,
            OrderType = OrderType.Market,
            TimeInForce = TimeInForce.FillOrKill,
            CreatedAt = bar.CloseTime,
            ExpiresAt = bar.CloseTime + bar.Period + bar.Period
        };
    }

    private sealed class InstrumentState
    {
        public Queue<decimal> Prices { get; } = new();

        public int? PreviousRelation { get; set; }
    }
}
