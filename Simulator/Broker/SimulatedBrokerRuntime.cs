using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.Broker;

/// <summary>
/// Applies execution candles to open orders. Orders submitted after a market event
/// cannot fill until a later event because eligibility is sequence-based.
/// </summary>
public sealed class SimulatedBrokerRuntime
{
    private readonly SimulatedBrokerState _state;
    private readonly BoundedAsyncEventLog<OrderEvent> _events;
    private readonly SimulationOptions _options;
    private readonly ISimulationClock _clock;
    private readonly Action _ensureActive;

    internal SimulatedBrokerRuntime(
        SimulatedBrokerState state,
        BoundedAsyncEventLog<OrderEvent> events,
        SimulationOptions options,
        ISimulationClock clock,
        Action ensureActive)
    {
        _state = state;
        _events = events;
        _options = options;
        _clock = clock;
        _ensureActive = ensureActive;
    }

    public Task ProcessExecutionCandleAsync(
        Candle candle,
        CancellationToken cancellationToken = default)
    {
        _ensureActive();
        ArgumentNullException.ThrowIfNull(candle);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExecutionCandle(candle);
        _state.AdvanceMarket(candle);
        _state.RecordCandle(candle);

        IReadOnlyList<SimulatedOrder> orders = _state.GetEligibleOrders(
            candle.Instrument,
            candle.Prices.Open);
        foreach (SimulatedOrder candidate in orders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Earlier fills in this candle can resize or cancel protective orders. Refresh the
            // order before evaluating it so a stale pre-loop copy can never over-close and
            // reverse the position after a partial reduction.
            SimulatedOrder? current = _state.GetSimulatedOrder(candidate.BrokerOrderId);
            if (current is null || !current.IsOpen)
                continue;
            SimulatedOrder order = current;

            if (IsExpired(order))
            {
                if (_state.TryExpire(order.BrokerOrderId, out SimulatedOrder? expired) && expired is not null)
                {
                    _events.Append(
                        SimulatedOrderClient.ToEvent(expired, OrderEventType.Expired, _clock.UtcNow));
                }

                continue;
            }

            FillDecision decision = Evaluate(order, candle);
            if (decision.TriggeredOnly)
            {
                if (_state.MarkTriggered(order.BrokerOrderId))
                {
                    _events.Append(
                        SimulatedOrderClient.ToEvent(order, OrderEventType.Triggered, _clock.UtcNow));
                }

                continue;
            }

            if (!decision.ShouldFill)
            {
                ExpireImmediateOrder(order);
                continue;
            }

            decimal quantity = order.Request.Quantity.Value - order.FilledQuantity;
            decimal executionPrice = ApplyCostsToPrice(
                decision.RawPrice,
                order.Request.Side,
                order.Request.Type,
                order.Request.LimitPrice);
            decimal quoteToBaseRate = _state.GetQuoteToBaseCurrencyRate(order.Request.Instrument);
            decimal commission = Math.Abs(executionPrice * quantity) *
                quoteToBaseRate *
                _options.CommissionRate;

            if (!_state.TryApplyFill(
                    order.BrokerOrderId,
                    executionPrice,
                    quantity,
                    commission,
                    _clock.UtcNow,
                    out FillApplicationResult? result,
                    out SimulatedOrder? rejected,
                    out string? rejectionReason))
            {
                if (rejected is not null)
                {
                    _events.Append(SimulatedOrderClient.ToEvent(
                        rejected,
                        OrderEventType.Rejected,
                        _clock.UtcNow,
                        message: rejectionReason));
                }

                continue;
            }

            FillApplicationResult applied = result
                ?? throw new InvalidOperationException("A successful fill did not return a result.");

            _events.Append(
                SimulatedOrderClient.ToEvent(
                    applied.Order,
                    OrderEventType.Filled,
                    _clock.UtcNow,
                    executionPrice,
                    applied.FilledQuantity,
                    applied.Commission));

            foreach (SimulatedOrder resized in applied.ResizedProtectiveOrders)
            {
                _events.Append(SimulatedOrderClient.ToEvent(
                    resized,
                    OrderEventType.Replaced,
                    _clock.UtcNow,
                    message: $"Protective quantity reconciled to {resized.Request.Quantity.Value}."));
            }

            foreach (SimulatedOrder cancelled in applied.CancelledProtectiveOrders)
            {
                _events.Append(SimulatedOrderClient.ToEvent(
                    cancelled,
                    OrderEventType.Cancelled,
                    _clock.UtcNow,
                    message: "Protective order cancelled because the position was fully closed."));
            }

            foreach (SimulatedOrder sibling in _state.CancelOcoSiblings(applied.Order.BrokerOrderId))
            {
                _events.Append(
                    SimulatedOrderClient.ToEvent(sibling, OrderEventType.Cancelled, _clock.UtcNow));
            }

            CreateAttachedExitOrders(applied.Order);
        }

        return Task.CompletedTask;
    }

    public void RecordAnalyticalCandle(Candle candle)
    {
        _ensureActive();
        _state.RecordCandle(candle);
    }

    public Task LiquidateAtMarketCloseAsync(
        Candle candle,
        CancellationToken cancellationToken = default)
    {
        _ensureActive();
        ArgumentNullException.ThrowIfNull(candle);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateExecutionCandle(candle);

        BrokerPosition? position = _state.GetPositions()
            .FirstOrDefault(item => item.Instrument == candle.Instrument);
        if (position is null || position.Quantity <= 0m)
        {
            return Task.CompletedTask;
        }

        foreach (BrokerOrder openOrder in _state.GetOpenOrders(candle.Instrument))
        {
            if (_state.TryCancel(openOrder.BrokerOrderId, out SimulatedOrder? cancelled) &&
                cancelled is not null)
            {
                _events.Append(SimulatedOrderClient.ToEvent(
                    cancelled,
                    OrderEventType.Cancelled,
                    _clock.UtcNow,
                    message: "Cancelled during end-of-simulation liquidation."));
            }
        }

        OrderSide side = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        SimulatedOrder liquidation = _state.CreateOrder(new PlaceOrderRequest
        {
            Instrument = candle.Instrument,
            Side = side,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(position.Quantity, QuantityUnit.Units),
            ClientOrderId = $"simulation-liquidation-{_state.MarketSequence}"
        }, _clock.UtcNow);

        decimal executionPrice = ApplyCostsToPrice(
            candle.Prices.Close,
            side,
            StandardOrderType.Market,
            null);
        decimal quoteToBaseRate = _state.GetQuoteToBaseCurrencyRate(candle.Instrument);
        decimal commission = Math.Abs(executionPrice * position.Quantity) *
            quoteToBaseRate *
            _options.CommissionRate;

        if (!_state.TryApplyFill(
                liquidation.BrokerOrderId,
                executionPrice,
                position.Quantity,
                commission,
                _clock.UtcNow,
                out FillApplicationResult? result,
                out SimulatedOrder? rejected,
                out string? rejectionReason))
        {
            if (rejected is not null)
            {
                _events.Append(SimulatedOrderClient.ToEvent(
                    rejected,
                    OrderEventType.Rejected,
                    _clock.UtcNow,
                    message: rejectionReason));
            }

            throw new InvalidOperationException(
                rejectionReason ?? "The final simulated position could not be liquidated.");
        }

        FillApplicationResult applied = result
            ?? throw new InvalidOperationException("Final liquidation did not return a fill result.");
        _events.Append(SimulatedOrderClient.ToEvent(
            applied.Order,
            OrderEventType.Filled,
            _clock.UtcNow,
            executionPrice,
            applied.FilledQuantity,
            applied.Commission,
            "Liquidated at the final available candle close."));
        return Task.CompletedTask;
    }


    private static void ValidateExecutionCandle(Candle candle)
    {
        if (candle.Instrument.IsEmpty || !candle.Interval.IsValid || !candle.IsComplete)
        {
            throw new ArgumentException(
                "An execution candle must have an instrument, valid interval, and be complete.",
                nameof(candle));
        }

        if (candle.CloseTime is not DateTimeOffset closeTime || closeTime <= candle.OpenTime)
        {
            throw new ArgumentException(
                "An execution candle must close after it opens.",
                nameof(candle));
        }

        Ohlc prices = candle.Prices;
        if (prices.Open <= 0m ||
            prices.High <= 0m ||
            prices.Low <= 0m ||
            prices.Close <= 0m ||
            prices.High < Math.Max(prices.Open, prices.Close) ||
            prices.Low > Math.Min(prices.Open, prices.Close) ||
            prices.High < prices.Low)
        {
            throw new ArgumentException("Execution candle OHLC prices are inconsistent.", nameof(candle));
        }
    }

    private bool IsExpired(SimulatedOrder order)
    {
        if (order.Request.ExpireAt is DateTimeOffset expiry && expiry <= _clock.UtcNow)
        {
            return true;
        }

        return order.Request.TimeInForce == StandardTimeInForce.Day &&
            _clock.UtcNow.UtcDateTime.Date > order.SubmittedAt.UtcDateTime.Date;
    }

    private void ExpireImmediateOrder(SimulatedOrder order)
    {
        if (order.Request.TimeInForce is not (
                StandardTimeInForce.ImmediateOrCancel or StandardTimeInForce.FillOrKill))
        {
            return;
        }

        if (_state.TryExpire(order.BrokerOrderId, out SimulatedOrder? expired) && expired is not null)
        {
            _events.Append(SimulatedOrderClient.ToEvent(
                expired,
                OrderEventType.Expired,
                _clock.UtcNow,
                message: $"{order.Request.TimeInForce} order did not fill on its first eligible candle."));
        }
    }

    private FillDecision Evaluate(SimulatedOrder order, Candle candle)
    {
        decimal open = candle.Prices.Open;
        decimal high = candle.Prices.High;
        decimal low = candle.Prices.Low;
        bool buy = order.Request.Side == OrderSide.Buy;

        return order.Request.Type switch
        {
            StandardOrderType.Market => FillDecision.Fill(open),

            StandardOrderType.Limit => buy
                ? low <= order.Request.LimitPrice!.Value
                    ? FillDecision.Fill(open <= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value)
                    : FillDecision.None
                : high >= order.Request.LimitPrice!.Value
                    ? FillDecision.Fill(open >= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value)
                    : FillDecision.None,

            StandardOrderType.Stop => buy
                ? high >= order.Request.StopPrice!.Value
                    ? FillDecision.Fill(open >= order.Request.StopPrice.Value ? open : order.Request.StopPrice.Value)
                    : FillDecision.None
                : low <= order.Request.StopPrice!.Value
                    ? FillDecision.Fill(open <= order.Request.StopPrice.Value ? open : order.Request.StopPrice.Value)
                    : FillDecision.None,

            StandardOrderType.StopLimit when !order.IsTriggered => buy
                ? high >= order.Request.StopPrice!.Value ? FillDecision.Trigger : FillDecision.None
                : low <= order.Request.StopPrice!.Value ? FillDecision.Trigger : FillDecision.None,

            StandardOrderType.StopLimit => buy
                ? low <= order.Request.LimitPrice!.Value
                    ? FillDecision.Fill(open <= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value)
                    : FillDecision.None
                : high >= order.Request.LimitPrice!.Value
                    ? FillDecision.Fill(open >= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value)
                    : FillDecision.None,

            _ => FillDecision.None
        };
    }

    private decimal ApplyCostsToPrice(
        decimal rawPrice,
        OrderSide side,
        StandardOrderType type,
        decimal? limitPrice)
    {
        decimal adverseBasisPoints =
            (_options.SpreadBasisPoints / 2m) + _options.SlippageBasisPoints;
        decimal multiplier = adverseBasisPoints / 10_000m;
        decimal adjusted = side == OrderSide.Buy
            ? rawPrice * (1m + multiplier)
            : rawPrice * (1m - multiplier);

        if ((type is StandardOrderType.Limit or StandardOrderType.StopLimit) && limitPrice is decimal limit)
        {
            adjusted = side == OrderSide.Buy
                ? Math.Min(adjusted, limit)
                : Math.Max(adjusted, limit);
        }

        return adjusted;
    }

    private void CreateAttachedExitOrders(
        SimulatedOrder filled)
    {
        PlaceOrderRequest request = filled.Request;
        if (request.StopLoss is null && request.TakeProfit is null)
        {
            return;
        }

        OrderSide exitSide = request.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        string ocoGroup = $"OCO-{filled.BrokerOrderId}";

        if (request.StopLoss is not null)
        {
            SimulatedOrder stop = _state.CreateOrder(new PlaceOrderRequest
            {
                Instrument = request.Instrument,
                Side = exitSide,
                Type = StandardOrderType.Stop,
                Quantity = request.Quantity,
                StopPrice = request.StopLoss.Price,
                ClientOrderId = $"{filled.ClientOrderId}-SL"
            }, _clock.UtcNow, ocoGroup);

            _events.Append(
                SimulatedOrderClient.ToEvent(stop, OrderEventType.Accepted, _clock.UtcNow));
        }

        if (request.TakeProfit is not null)
        {
            SimulatedOrder takeProfit = _state.CreateOrder(new PlaceOrderRequest
            {
                Instrument = request.Instrument,
                Side = exitSide,
                Type = StandardOrderType.Limit,
                Quantity = request.Quantity,
                LimitPrice = request.TakeProfit.Price,
                ClientOrderId = $"{filled.ClientOrderId}-TP"
            }, _clock.UtcNow, ocoGroup);

            _events.Append(
                SimulatedOrderClient.ToEvent(takeProfit, OrderEventType.Accepted, _clock.UtcNow));
        }
    }

    private readonly record struct FillDecision(
        bool ShouldFill,
        bool TriggeredOnly,
        decimal RawPrice)
    {
        public static FillDecision None => new(false, false, 0m);
        public static FillDecision Trigger => new(false, true, 0m);
        public static FillDecision Fill(decimal price) => new(true, false, price);
    }
}
