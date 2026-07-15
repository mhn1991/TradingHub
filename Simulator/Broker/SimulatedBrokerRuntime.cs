using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;
using Simulator.Execution;
using Simulator.Financing;
using PortfolioManager.Risk;

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
    private readonly ISimulationExecutionModel _executionModel;
    private readonly IFinancingModel? _financingModel;
    private readonly TimeZoneInfo? _financingTimeZone;
    private DateTimeOffset? _lastFinancingObservation;

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
        _executionModel = new SimulationExecutionModel(options.ExecutionModel);
        if (options.Financing.Enabled)
        {
            _financingModel = new ConfiguredFinancingModel(options.Financing);
            _financingTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Financing.BrokerTimeZoneId);
        }
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
        PostFinancing(candle);

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

            decimal remainingOrderQuantity = order.Request.Quantity.Value - order.FilledQuantity;
            FillEvaluation decision = _executionModel.Evaluate(order, candle, new ExecutionModelContext
            {
                Sequence = _state.MarketSequence,
                ConfiguredSpreadBasisPoints = _options.SpreadBasisPoints,
                ConfiguredSlippageBasisPoints = _options.SlippageBasisPoints,
                OrderQuantity = order.Request.Quantity.Value,
                RemainingQuantity = remainingOrderQuantity
            });
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

            decimal quantity = decision.FillQuantity;
            decimal executionPrice = decision.ExecutablePrice ??
                throw new InvalidOperationException("A fill evaluation did not provide an executable price.");
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
            _state.RecordExecutionFill(order.Request.Type, decision);

            _events.Append(
                SimulatedOrderClient.ToEvent(
                    applied.Order,
                    decision.IsPartialFill ? OrderEventType.PartiallyFilled : OrderEventType.Filled,
                    _clock.UtcNow,
                    executionPrice,
                    applied.FilledQuantity,
                    applied.Commission,
                    decision.GapThroughStop
                        ? "GapThroughStop: filled at adverse executable open plus slippage."
                        : decision.IsPartialFill
                            ? "Synthetic deterministic partial-fill capacity applied."
                            : null,
                    appliedSpread: decision.AppliedSpreadPrice,
                    appliedSlippage: decision.AppliedSlippagePrice,
                    executionModelVersion: $"simulation-execution-v1:{_options.ExecutionModel.FillModel}"));

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

            if (string.Equals(applied.Order.Status, "FILLED", StringComparison.Ordinal))
            {
                foreach (SimulatedOrder sibling in _state.CancelOcoSiblings(applied.Order.BrokerOrderId))
                {
                    _events.Append(
                        SimulatedOrderClient.ToEvent(sibling, OrderEventType.Cancelled, _clock.UtcNow));
                }
            }

            CreateAttachedExitOrders(applied.Order, applied.Order.FilledQuantity);
            if (decision.IsPartialFill &&
                order.Request.TimeInForce == StandardTimeInForce.ImmediateOrCancel)
                ExpireImmediateOrder(applied.Order);
        }

        return Task.CompletedTask;
    }

    public void RecordAnalyticalCandle(Candle candle)
    {
        _ensureActive();
        _state.RecordCandle(candle);
    }

    private void PostFinancing(Candle candle)
    {
        if (_financingModel is null || _financingTimeZone is null || candle.CloseTime is not DateTimeOffset current)
            return;
        if (_lastFinancingObservation is not DateTimeOffset previous)
        {
            _lastFinancingObservation = current;
            return;
        }

        DateTime previousLocal = TimeZoneInfo.ConvertTime(previous, _financingTimeZone).DateTime;
        DateTime currentLocal = TimeZoneInfo.ConvertTime(current, _financingTimeZone).DateTime;
        DateTime date = previousLocal.Date;
        while (date <= currentLocal.Date)
        {
            DateTime boundaryLocal = date + _options.Financing.RolloverLocalTime.ToTimeSpan();
            if (previousLocal < boundaryLocal && currentLocal >= boundaryLocal)
            {
                DateTime boundaryUtc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(boundaryLocal, DateTimeKind.Unspecified),
                    _financingTimeZone);
                DateTimeOffset boundary = new(boundaryUtc, TimeSpan.Zero);
                int dayCount = ResolveFinancingDayCount(DateOnly.FromDateTime(boundaryLocal));
                foreach (BrokerPosition position in _state.GetPositions()
                             .OrderBy(item => item.Instrument.Value, StringComparer.Ordinal))
                {
                    decimal price = position.AveragePrice ?? candle.Prices.Close;
                    decimal conversion = _state.GetQuoteToBaseCurrencyRate(position.Instrument);
                    FinancingCharge charge = _financingModel.Calculate(
                        new PortfolioPositionLot
                        {
                            LotId = position.PositionId,
                            StrategyId = "shared-account",
                            DecisionId = "financing",
                            Instrument = position.Instrument,
                            Side = position.Side,
                            Quantity = position.Quantity,
                            EntryPrice = price,
                            CurrentPrice = candle.Prices.Close,
                            QuoteToAccountCurrencyRate = conversion
                        },
                        previous,
                        boundary,
                        new FinancingContext
                        {
                            QuoteToAccountCurrencyRate = conversion,
                            RolloverDate = DateOnly.FromDateTime(boundaryLocal),
                            RolloverDayCount = dayCount,
                            AccountCurrency = _options.BaseCurrency
                        });
                    _state.ApplyFinancing(boundary, charge.AmountAccountCurrency,
                        $"{charge.Explanation} Model={charge.ModelVersion}; instrument={position.Instrument}.");
                }
            }
            date = date.AddDays(1);
        }
        _lastFinancingObservation = current;
    }

    private int ResolveFinancingDayCount(DateOnly rolloverDate)
    {
        int dayCount = rolloverDate.DayOfWeek == _options.Financing.TripleFinancingDay ? 3 : 1;
        // The configured calendar represents non-settlement days. Extend the charge
        // horizon for holidays without double-counting weekend days already covered by
        // the normal triple rollover.
        for (int offset = 1; offset <= dayCount; offset++)
        {
            DateOnly settlementDate = rolloverDate.AddDays(offset);
            if (_options.Financing.Holidays.Contains(settlementDate) &&
                settlementDate.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                dayCount++;
        }
        return dayCount;
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
            ClientOrderId = $"simulation-liquidation-{_state.MarketSequence}",
            StrategyId = position.StrategyId,
            DecisionId = position.DecisionId,
            SetupId = position.SetupId,
            PortfolioReservationId = position.PortfolioReservationId,
            RiskClusterId = position.RiskClusterId,
            ReduceOnly = true
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
        SimulatedOrder filled,
        decimal totalFilledQuantity)
    {
        PlaceOrderRequest request = filled.Request;
        if (request.StopLoss is null && request.TakeProfit is null)
        {
            return;
        }

        OrderSide exitSide = request.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        string ocoGroup = $"OCO-{filled.BrokerOrderId}";
        IReadOnlyList<SimulatedOrder> resized = _state.ResizeOpenOcoGroupQuantities(
            ocoGroup,
            totalFilledQuantity);
        if (resized.Count > 0)
        {
            foreach (SimulatedOrder order in resized)
            {
                _events.Append(SimulatedOrderClient.ToEvent(
                    order,
                    OrderEventType.Replaced,
                    _clock.UtcNow,
                    message: $"Protective quantity increased to cumulative entry fill {totalFilledQuantity}."));
            }
            return;
        }

        if (request.StopLoss is not null)
        {
            SimulatedOrder stop = _state.CreateOrder(new PlaceOrderRequest
            {
                Instrument = request.Instrument,
                Side = exitSide,
                Type = StandardOrderType.Stop,
                Quantity = new OrderQuantity(totalFilledQuantity, request.Quantity.Unit),
                StopPrice = request.StopLoss.Price,
                ClientOrderId = $"{filled.ClientOrderId}-SL",
                StrategyId = request.StrategyId,
                DecisionId = request.DecisionId,
                SetupId = request.SetupId,
                PortfolioReservationId = request.PortfolioReservationId,
                RiskClusterId = request.RiskClusterId,
                ReduceOnly = true
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
                Quantity = new OrderQuantity(totalFilledQuantity, request.Quantity.Unit),
                LimitPrice = request.TakeProfit.Price,
                ClientOrderId = $"{filled.ClientOrderId}-TP",
                StrategyId = request.StrategyId,
                DecisionId = request.DecisionId,
                SetupId = request.SetupId,
                PortfolioReservationId = request.PortfolioReservationId,
                RiskClusterId = request.RiskClusterId,
                ReduceOnly = true
            }, _clock.UtcNow, ocoGroup);

            _events.Append(
                SimulatedOrderClient.ToEvent(takeProfit, OrderEventType.Accepted, _clock.UtcNow));
        }
    }

}
