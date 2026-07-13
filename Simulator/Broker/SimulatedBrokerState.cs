using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;
using Simulator.Models;

namespace Simulator.Broker;

internal sealed class SimulatedBrokerState
{
    private readonly object _sync = new();
    private readonly SimulationOptions _options;
    private readonly Dictionary<string, SimulatedOrder> _orders = new(StringComparer.Ordinal);
    private readonly Dictionary<InstrumentKey, MutablePosition> _positions = [];
    private readonly Dictionary<ChartKey, RingBuffer<Candle>> _candles = [];
    private readonly Dictionary<InstrumentKey, Candle> _latestCandles = [];
    private readonly RingBuffer<LedgerEntry> _ledger;
    private long _ledgerSequence;
    private long _orderSequence;

    public SimulatedBrokerState(SimulationOptions options)
    {
        _options = options;
        CashBalance = options.StartingBalance;
        _ledger = new RingBuffer<LedgerEntry>(options.LedgerCapacity);
        AddLedgerUnsafe(
            DateTimeOffset.MinValue,
            LedgerEntryType.Deposit,
            options.StartingBalance,
            null,
            "Starting simulation balance");
    }

    public decimal CashBalance { get; private set; }
    public decimal TotalCommission { get; private set; }
    public int SubmittedOrders { get; private set; }
    public int FilledOrders { get; private set; }
    public int RejectedOrders { get; private set; }
    public long MarketSequence { get; private set; }

    public long AdvanceMarket(Candle candle)
    {
        lock (_sync)
        {
            MarketSequence++;
            _latestCandles[candle.Instrument] = candle;
            return MarketSequence;
        }
    }

    public void RecordCandle(Candle candle)
    {
        lock (_sync)
        {
            ChartKey key = new(candle.Instrument, candle.Interval);
            if (!_candles.TryGetValue(key, out RingBuffer<Candle>? buffer))
            {
                buffer = new RingBuffer<Candle>(_options.CandleCapacity);
                _candles.Add(key, buffer);
            }

            buffer.Add(candle);
            _latestCandles[candle.Instrument] = candle;
        }
    }

    public IReadOnlyList<Candle> GetCandles(CandleQuery query)
    {
        query.Validate(_options.CandleCapacity, "Simulator");
        lock (_sync)
        {
            ChartKey key = new(query.Instrument, query.Interval);
            if (!_candles.TryGetValue(key, out RingBuffer<Candle>? buffer))
            {
                return [];
            }

            IEnumerable<Candle> result = buffer;
            if (query.From is not null)
            {
                result = result.Where(candle => candle.OpenTime >= query.From.Value);
            }

            if (query.To is not null)
            {
                result = result.Where(candle => candle.OpenTime <= query.To.Value);
            }

            return result.TakeLast(query.Limit).ToArray();
        }
    }

    public SimulatedOrder CreateOrder(PlaceOrderRequest request, DateTimeOffset now, string? ocoGroupId = null)
    {
        lock (_sync)
        {
            return CreateOrderUnsafe(request, now, ocoGroupId);
        }
    }

    public bool TryCreateOrder(
        PlaceOrderRequest request,
        DateTimeOffset now,
        out SimulatedOrder? order,
        out string? error)
    {
        lock (_sync)
        {
            if (request.ClientOrderId is not null && _orders.Values.Any(existing =>
                    string.Equals(
                        existing.ClientOrderId,
                        request.ClientOrderId,
                        StringComparison.Ordinal)))
            {
                order = null;
                error = $"Client order ID '{request.ClientOrderId}' has already been used.";
                return false;
            }

            order = CreateOrderUnsafe(request, now);
            error = null;
            return true;
        }
    }

    public void RejectOrder()
    {
        lock (_sync)
        {
            SubmittedOrders++;
            RejectedOrders++;
        }
    }

    public string? GetOrderConfigurationError(PlaceOrderRequest request)
    {
        lock (_sync)
        {
            return TryGetQuoteToBaseCurrencyRateUnsafe(
                request.Instrument,
                out _,
                out string? error)
                ? null
                : error;
        }
    }

    public decimal GetQuoteToBaseCurrencyRate(InstrumentKey instrument)
    {
        lock (_sync)
        {
            if (TryGetQuoteToBaseCurrencyRateUnsafe(instrument, out decimal rate, out string? error))
            {
                return rate;
            }

            throw new InvalidOperationException(error);
        }
    }

    public bool TryCancel(string brokerOrderId, out SimulatedOrder? cancelled)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(brokerOrderId, out SimulatedOrder? order) || !order.IsOpen)
            {
                cancelled = null;
                return false;
            }

            order.Status = "CANCELLED";
            cancelled = order.Copy();
            return true;
        }
    }

    public bool TryExpire(string brokerOrderId, out SimulatedOrder? expired)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(brokerOrderId, out SimulatedOrder? order) || !order.IsOpen)
            {
                expired = null;
                return false;
            }

            order.Status = "EXPIRED";
            expired = order.Copy();
            return true;
        }
    }

    public IReadOnlyList<SimulatedOrder> GetEligibleOrders(InstrumentKey instrument)
    {
        lock (_sync)
        {
            return _orders.Values
                .Where(order =>
                    order.IsOpen &&
                    order.Request.Instrument == instrument &&
                    order.SubmittedMarketSequence < MarketSequence)
                .OrderBy(order => order.SubmittedMarketSequence)
                .ThenBy(GetOcoExecutionPriority)
                .ThenBy(order => order.BrokerOrderId, StringComparer.Ordinal)
                .Select(order => order.Copy())
                .ToArray();
        }
    }

    public bool MarkTriggered(string brokerOrderId)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(brokerOrderId, out SimulatedOrder? order) || !order.IsOpen)
            {
                return false;
            }

            order.IsTriggered = true;
            order.SubmittedMarketSequence = MarketSequence;
            order.Status = "TRIGGERED";
            return true;
        }
    }

    public bool TryApplyFill(
        string brokerOrderId,
        decimal price,
        decimal quantity,
        decimal commission,
        DateTimeOffset timestamp,
        out FillApplicationResult? result,
        out SimulatedOrder? rejectedOrder,
        out string? rejectionReason)
    {
        lock (_sync)
        {
            result = null;
            rejectedOrder = null;
            rejectionReason = null;

            if (!_orders.TryGetValue(brokerOrderId, out SimulatedOrder? order) || !order.IsOpen)
            {
                return false;
            }

            rejectionReason = GetMarginRejectionReasonUnsafe(order, price, quantity, commission);
            if (rejectionReason is not null)
            {
                order.Status = "REJECTED";
                RejectedOrders++;
                rejectedOrder = order.Copy();
                return false;
            }

            order.FilledQuantity += quantity;
            order.Status = order.FilledQuantity >= order.Request.Quantity.Value
                ? "FILLED"
                : "PARTIALLY_FILLED";

            if (order.Status == "FILLED")
            {
                FilledOrders++;
            }

            decimal signedFill = order.Request.Side == OrderSide.Buy ? quantity : -quantity;
            decimal quoteToBaseRate = GetQuoteToBaseCurrencyRateUnsafe(order.Request.Instrument);
            decimal realised = ApplyPositionFillUnsafe(
                order.Request.Instrument,
                signedFill,
                price) * quoteToBaseRate;

            CashBalance += realised - commission;
            TotalCommission += commission;

            if (commission != 0m)
            {
                AddLedgerUnsafe(
                    timestamp,
                    LedgerEntryType.Commission,
                    -commission,
                    order.BrokerOrderId,
                    "Execution commission");
            }

            if (realised != 0m)
            {
                AddLedgerUnsafe(
                    timestamp,
                    LedgerEntryType.RealisedProfitLoss,
                    realised,
                    order.BrokerOrderId,
                    "Realised position profit/loss");
            }

            result = new FillApplicationResult(
                order.Copy(),
                realised,
                commission,
                GetPositionUnsafe(order.Request.Instrument));
            return true;
        }
    }

    public IReadOnlyList<SimulatedOrder> CancelOcoSiblings(string filledOrderId)
    {
        lock (_sync)
        {
            if (!_orders.TryGetValue(filledOrderId, out SimulatedOrder? filled) ||
                string.IsNullOrWhiteSpace(filled.OcoGroupId))
            {
                return [];
            }

            SimulatedOrder[] siblings = _orders.Values
                .Where(order =>
                    order.BrokerOrderId != filledOrderId &&
                    order.IsOpen &&
                    string.Equals(order.OcoGroupId, filled.OcoGroupId, StringComparison.Ordinal))
                .ToArray();

            foreach (SimulatedOrder sibling in siblings)
            {
                sibling.Status = "CANCELLED";
            }

            return siblings.Select(order => order.Copy()).ToArray();
        }
    }

    public IReadOnlyList<BrokerOrder> GetOpenOrders(InstrumentKey? instrument)
    {
        lock (_sync)
        {
            return _orders.Values
                .Where(order => order.IsOpen && (instrument is null || order.Request.Instrument == instrument.Value))
                .Select(ToBrokerOrder)
                .ToArray();
        }
    }

    public IReadOnlyList<BrokerPosition> GetPositions()
    {
        lock (_sync)
        {
            return _positions.Values.Select(ToBrokerPositionUnsafe).ToArray();
        }
    }

    public AccountSnapshot GetAccount()
    {
        lock (_sync)
        {
            decimal unrealised = _positions.Values.Sum(position => CalculateUnrealisedUnsafe(position));
            decimal margin = _positions.Values.Sum(position => CalculateMarginUnsafe(position));
            return new AccountSnapshot
            {
                AccountId = _options.AccountId,
                AccountType = "Simulated Margin",
                Currency = _options.BaseCurrency,
                Balance = CashBalance,
                Available = CashBalance + unrealised - margin,
                MarginUsed = margin,
                UnrealizedProfitLoss = unrealised,
                CanTrade = CashBalance + unrealised - margin > 0m
            };
        }
    }

    public SimulationResult BuildResult(DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        lock (_sync)
        {
            decimal unrealised = _positions.Values.Sum(position => CalculateUnrealisedUnsafe(position));
            decimal equity = CashBalance + unrealised;
            return new SimulationResult
            {
                StartedAt = startedAt,
                EndedAt = endedAt,
                StartingBalance = _options.StartingBalance,
                FinalBalance = CashBalance,
                FinalEquity = equity,
                UnrealizedProfitLoss = unrealised,
                NetProfit = equity - _options.StartingBalance,
                TotalCommission = TotalCommission,
                SubmittedOrders = SubmittedOrders,
                FilledOrders = FilledOrders,
                RejectedOrders = RejectedOrders,
                OpenPositions = _positions.Values.Select(ToBrokerPositionUnsafe).ToArray(),
                Ledger = _ledger.Snapshot()
            };
        }
    }

    public IReadOnlyList<LedgerEntry> GetLedger()
    {
        lock (_sync)
        {
            return _ledger.Snapshot();
        }
    }

    private decimal ApplyPositionFillUnsafe(
        InstrumentKey instrument,
        decimal signedFill,
        decimal fillPrice)
    {
        if (!_positions.TryGetValue(instrument, out MutablePosition? position))
        {
            _positions[instrument] = new MutablePosition(instrument, signedFill, fillPrice);
            return 0m;
        }

        decimal existing = position.SignedQuantity;
        if (Math.Sign(existing) == Math.Sign(signedFill))
        {
            decimal newQuantity = existing + signedFill;
            position.AveragePrice =
                ((Math.Abs(existing) * position.AveragePrice) + (Math.Abs(signedFill) * fillPrice)) /
                Math.Abs(newQuantity);
            position.SignedQuantity = newQuantity;
            return 0m;
        }

        decimal closingQuantity = Math.Min(Math.Abs(existing), Math.Abs(signedFill));
        decimal realised = existing > 0m
            ? (fillPrice - position.AveragePrice) * closingQuantity
            : (position.AveragePrice - fillPrice) * closingQuantity;

        decimal remaining = existing + signedFill;
        if (remaining == 0m)
        {
            _positions.Remove(instrument);
        }
        else if (Math.Sign(remaining) != Math.Sign(existing))
        {
            position.SignedQuantity = remaining;
            position.AveragePrice = fillPrice;
        }
        else
        {
            position.SignedQuantity = remaining;
        }

        return realised;
    }

    private BrokerPosition? GetPositionUnsafe(InstrumentKey instrument) =>
        _positions.TryGetValue(instrument, out MutablePosition? position)
            ? ToBrokerPositionUnsafe(position)
            : null;

    private BrokerPosition ToBrokerPositionUnsafe(MutablePosition position) => new()
    {
        PositionId = $"SIM-POS-{position.Instrument.Value}",
        Instrument = position.Instrument,
        Side = position.SignedQuantity >= 0m ? OrderSide.Buy : OrderSide.Sell,
        Quantity = Math.Abs(position.SignedQuantity),
        AveragePrice = position.AveragePrice,
        UnrealizedProfitLoss = CalculateUnrealisedUnsafe(position),
        Currency = _options.BaseCurrency
    };

    private decimal CalculateUnrealisedUnsafe(MutablePosition position)
    {
        if (!_latestCandles.TryGetValue(position.Instrument, out Candle? candle))
        {
            return 0m;
        }

        decimal mark = candle.Prices.Close;
        decimal quoteProfitLoss = position.SignedQuantity >= 0m
            ? (mark - position.AveragePrice) * Math.Abs(position.SignedQuantity)
            : (position.AveragePrice - mark) * Math.Abs(position.SignedQuantity);
        return quoteProfitLoss * GetQuoteToBaseCurrencyRateUnsafe(position.Instrument);
    }

    private decimal CalculateMarginUnsafe(MutablePosition position)
    {
        decimal mark = _latestCandles.TryGetValue(position.Instrument, out Candle? candle)
            ? candle.Prices.Close
            : position.AveragePrice;
        return Math.Abs(position.SignedQuantity) *
            mark *
            GetQuoteToBaseCurrencyRateUnsafe(position.Instrument) /
            _options.Leverage;
    }

    private static BrokerOrder ToBrokerOrder(SimulatedOrder order) => new()
    {
        BrokerOrderId = order.BrokerOrderId,
        ClientOrderId = order.ClientOrderId,
        Instrument = order.Request.Instrument,
        Side = order.Request.Side,
        Type = order.Request.Type.ToString(),
        Status = order.Status,
        NormalizedStatus = ToNormalizedStatus(order.Status),
        Quantity = order.Request.Quantity.Value,
        FilledQuantity = order.FilledQuantity,
        Price = order.Request.LimitPrice ?? order.Request.StopPrice,
        CreatedAt = order.SubmittedAt
    };

    private int GetOcoExecutionPriority(SimulatedOrder order)
    {
        if (order.OcoGroupId is null)
        {
            return 0;
        }

        return (_options.OcoFillPolicy, order.Request.Type) switch
        {
            (OcoFillPolicy.StopLossFirst, StandardOrderType.Stop) => -1,
            (OcoFillPolicy.StopLossFirst, StandardOrderType.Limit) => 1,
            (OcoFillPolicy.TakeProfitFirst, StandardOrderType.Limit) => -1,
            (OcoFillPolicy.TakeProfitFirst, StandardOrderType.Stop) => 1,
            _ => 0
        };
    }

    private string? GetMarginRejectionReasonUnsafe(
        SimulatedOrder order,
        decimal price,
        decimal quantity,
        decimal commission)
    {
        if (!_options.EnforceMarginRequirements)
        {
            return null;
        }

        decimal existingQuantity = _positions.TryGetValue(
            order.Request.Instrument,
            out MutablePosition? position)
            ? position.SignedQuantity
            : 0m;
        decimal signedFill = order.Request.Side == OrderSide.Buy ? quantity : -quantity;
        decimal additionalExposure = Math.Max(
            0m,
            Math.Abs(existingQuantity + signedFill) - Math.Abs(existingQuantity));

        if (additionalExposure == 0m)
        {
            return null;
        }

        decimal available = CalculateAvailableUnsafe(order.Request.Instrument, price);
        decimal required = additionalExposure *
            price *
            GetQuoteToBaseCurrencyRateUnsafe(order.Request.Instrument) /
            _options.Leverage +
            commission;
        return required > available
            ? $"Insufficient available margin. Required {required:F8} {_options.BaseCurrency}, " +
              $"available {available:F8} {_options.BaseCurrency}."
            : null;
    }

    private decimal CalculateAvailableUnsafe(InstrumentKey executionInstrument, decimal executionPrice)
    {
        decimal unrealised = 0m;
        decimal margin = 0m;
        foreach (MutablePosition position in _positions.Values)
        {
            decimal mark = position.Instrument == executionInstrument
                ? executionPrice
                : _latestCandles.TryGetValue(position.Instrument, out Candle? candle)
                    ? candle.Prices.Close
                    : position.AveragePrice;
            decimal positionProfitLoss = position.SignedQuantity >= 0m
                ? (mark - position.AveragePrice) * Math.Abs(position.SignedQuantity)
                : (position.AveragePrice - mark) * Math.Abs(position.SignedQuantity);
            decimal rate = GetQuoteToBaseCurrencyRateUnsafe(position.Instrument);
            unrealised += positionProfitLoss * rate;
            margin += Math.Abs(position.SignedQuantity) * mark * rate / _options.Leverage;
        }

        return CashBalance + unrealised - margin;
    }

    private static OrderStatus ToNormalizedStatus(string status) => status switch
    {
        "ACCEPTED" or "TRIGGERED" => OrderStatus.Open,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELLED" => OrderStatus.Cancelled,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" => OrderStatus.Expired,
        _ => OrderStatus.Unknown
    };

    private decimal GetQuoteToBaseCurrencyRateUnsafe(InstrumentKey instrument)
    {
        if (TryGetQuoteToBaseCurrencyRateUnsafe(instrument, out decimal rate, out string? error))
        {
            return rate;
        }

        throw new InvalidOperationException(error);
    }

    private bool TryGetQuoteToBaseCurrencyRateUnsafe(
        InstrumentKey instrument,
        out decimal rate,
        out string? error)
    {
        string value = instrument.Value;
        int prefix = value.IndexOf(':');
        string pair = prefix >= 0 ? value[(prefix + 1)..] : value;
        int separator = pair.LastIndexOfAny(['/', '_', '-']);
        if (separator < 0 || separator == pair.Length - 1)
        {
            rate = 0m;
            error = $"Instrument {instrument} does not expose a quote currency. " +
                "Use a canonical BASE/QUOTE instrument key.";
            return false;
        }

        string quoteCurrency = pair[(separator + 1)..];
        if (string.Equals(quoteCurrency, _options.BaseCurrency, StringComparison.OrdinalIgnoreCase))
        {
            rate = 1m;
            error = null;
            return true;
        }

        foreach ((string currency, decimal configuredRate) in _options.QuoteToBaseCurrencyRates)
        {
            if (string.Equals(currency, quoteCurrency, StringComparison.OrdinalIgnoreCase))
            {
                rate = configuredRate;
                error = null;
                return true;
            }
        }

        rate = 0m;
        error = $"No {quoteCurrency}-to-{_options.BaseCurrency} conversion rate is configured " +
            $"for {instrument}.";
        return false;
    }

    private void AddLedgerUnsafe(
        DateTimeOffset timestamp,
        LedgerEntryType type,
        decimal amount,
        string? orderId,
        string description)
    {
        _ledger.Add(new LedgerEntry
        {
            Sequence = ++_ledgerSequence,
            Timestamp = timestamp,
            Type = type,
            Amount = amount,
            Currency = _options.BaseCurrency,
            OrderId = orderId,
            Description = description
        });
    }

    private SimulatedOrder CreateOrderUnsafe(
        PlaceOrderRequest request,
        DateTimeOffset now,
        string? ocoGroupId = null)
    {
        SubmittedOrders++;
        string id = $"SIM-{++_orderSequence:D10}";
        var order = new SimulatedOrder
        {
            BrokerOrderId = id,
            ClientOrderId = request.ClientOrderId ?? id,
            Request = request,
            SubmittedAt = now,
            SubmittedMarketSequence = MarketSequence,
            Status = "ACCEPTED",
            OcoGroupId = ocoGroupId
        };
        _orders.Add(id, order);
        return order.Copy();
    }

    private sealed class MutablePosition(
        InstrumentKey instrument,
        decimal signedQuantity,
        decimal averagePrice)
    {
        public InstrumentKey Instrument { get; } = instrument;
        public decimal SignedQuantity { get; set; } = signedQuantity;
        public decimal AveragePrice { get; set; } = averagePrice;
    }
}

internal sealed class SimulatedOrder
{
    public required string BrokerOrderId { get; init; }
    public required string ClientOrderId { get; init; }
    public required PlaceOrderRequest Request { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required long SubmittedMarketSequence { get; set; }
    public required string Status { get; set; }
    public decimal FilledQuantity { get; set; }
    public bool IsTriggered { get; set; }
    public string? OcoGroupId { get; init; }
    public bool IsOpen => Status is "ACCEPTED" or "TRIGGERED" or "PARTIALLY_FILLED";

    public SimulatedOrder Copy() => (SimulatedOrder)MemberwiseClone();
}

internal sealed record FillApplicationResult(
    SimulatedOrder Order,
    decimal RealisedProfitLoss,
    decimal Commission,
    BrokerPosition? Position);
