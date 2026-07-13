using System.Globalization;
using Brokers.Abstractions;
using Brokers.Exceptions;
using Brokers.Infrastructure;
using Brokers.Models;

namespace Brokers.Oanda;

internal static class OandaMappings
{
    public static string ToGranularity(BarInterval interval) => interval switch
    {
        { Unit: BarUnit.Second, Value: 5 } => "S5",
        { Unit: BarUnit.Second, Value: 10 } => "S10",
        { Unit: BarUnit.Second, Value: 15 } => "S15",
        { Unit: BarUnit.Second, Value: 30 } => "S30",
        { Unit: BarUnit.Minute, Value: 1 } => "M1",
        { Unit: BarUnit.Minute, Value: 2 } => "M2",
        { Unit: BarUnit.Minute, Value: 4 } => "M4",
        { Unit: BarUnit.Minute, Value: 5 } => "M5",
        { Unit: BarUnit.Minute, Value: 10 } => "M10",
        { Unit: BarUnit.Minute, Value: 15 } => "M15",
        { Unit: BarUnit.Minute, Value: 30 } => "M30",
        { Unit: BarUnit.Hour, Value: 1 } => "H1",
        { Unit: BarUnit.Hour, Value: 2 } => "H2",
        { Unit: BarUnit.Hour, Value: 3 } => "H3",
        { Unit: BarUnit.Hour, Value: 4 } => "H4",
        { Unit: BarUnit.Hour, Value: 6 } => "H6",
        { Unit: BarUnit.Hour, Value: 8 } => "H8",
        { Unit: BarUnit.Hour, Value: 12 } => "H12",
        { Unit: BarUnit.Day, Value: 1 } => "D",
        { Unit: BarUnit.Week, Value: 1 } => "W",
        { Unit: BarUnit.Month, Value: 1 } => "M",
        _ => throw new BrokerFeatureNotSupportedException(
            BrokerKind.Oanda,
            $"Candle interval {interval}")
    };

    public static Candle ToCandle(OandaCandle source, CandleQuery query)
    {
        OandaPrice price = source.Mid ?? source.Bid ?? source.Ask
            ?? throw new InvalidOperationException("OANDA candle did not contain a price component.");
        DateTimeOffset openTime = DateTimeOffset.Parse(
            source.Time,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        return new Candle
        {
            Instrument = query.Instrument,
            Interval = query.Interval,
            OpenTime = openTime,
            CloseTime = query.Interval.AddTo(openTime),
            Prices = new Ohlc(
                BrokerJson.ParseDecimal(price.Open),
                BrokerJson.ParseDecimal(price.High),
                BrokerJson.ParseDecimal(price.Low),
                BrokerJson.ParseDecimal(price.Close)),
            Volume = new MarketVolume(source.Volume, VolumeKind.TickCount),
            IsComplete = source.Complete
        };
    }

    public static BrokerOrder ToOrder(
        OandaOrder source,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        decimal units = BrokerJson.ParseDecimal(source.Units);
        string nativeInstrument = source.Instrument
            ?? throw new InvalidOperationException("OANDA order did not contain an instrument.");
        string nativeStatus = source.State ?? "UNKNOWN";
        return new BrokerOrder
        {
            BrokerOrderId = source.Id,
            ClientOrderId = source.ClientExtensions?.Id ?? source.ClientOrderId,
            Instrument = InstrumentMappers.FromNative(nativeInstrument, instrumentMappings),
            NativeInstrument = nativeInstrument,
            Side = units switch
            {
                > 0 => OrderSide.Buy,
                < 0 => OrderSide.Sell,
                _ => OrderSide.Unknown
            },
            Type = source.Type ?? "UNKNOWN",
            Status = nativeStatus,
            NormalizedStatus = nativeStatus.ToUpperInvariant() switch
            {
                "PENDING" => OrderStatus.Pending,
                "FILLED" => OrderStatus.Filled,
                "CANCELLED" => OrderStatus.Cancelled,
                "TRIGGERED" => OrderStatus.Filled,
                _ => OrderStatus.Unknown
            },
            Quantity = Math.Abs(units),
            FilledQuantity = null,
            Price = BrokerJson.ParseNullableDecimal(source.Price),
            CreatedAt = ParseDate(source.CreateTime)
        };
    }

    public static void AddPositionSide(
        ICollection<BrokerPosition> destination,
        OandaPosition position,
        OandaPositionSide? side,
        OrderSide orderSide,
        string suffix,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        if (side is null)
        {
            return;
        }

        decimal signedUnits = BrokerJson.ParseDecimal(side.Units);
        if (signedUnits == 0m)
        {
            return;
        }

        destination.Add(new BrokerPosition
        {
            PositionId = $"{position.Instrument}:{suffix}",
            Instrument = InstrumentMappers.FromNative(position.Instrument, instrumentMappings),
            NativeInstrument = position.Instrument,
            Side = orderSide,
            Quantity = Math.Abs(signedUnits),
            AveragePrice = BrokerJson.ParseNullableDecimal(side.AveragePrice),
            UnrealizedProfitLoss = BrokerJson.ParseNullableDecimal(side.UnrealizedPl)
        });
    }

    public static OandaCreateOrderEnvelope ToOrderRequest(
        PlaceOrderRequest request,
        string nativeInstrument,
        string clientOrderId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeInstrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        if (request.Quantity.Unit != QuantityUnit.Units)
        {
            throw new BrokerFeatureNotSupportedException(
                BrokerKind.Oanda,
                $"Order quantity unit {request.Quantity.Unit}");
        }

        string type = request.Type switch
        {
            StandardOrderType.Market => "MARKET",
            StandardOrderType.Limit => "LIMIT",
            StandardOrderType.Stop => "STOP",
            StandardOrderType.StopLimit => throw new BrokerFeatureNotSupportedException(
                BrokerKind.Oanda,
                "Stop-limit orders"),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Type))
        };
        decimal? orderPrice = request.Type switch
        {
            StandardOrderType.Limit => request.LimitPrice,
            StandardOrderType.Stop => request.StopPrice,
            _ => null
        };
        if ((request.Type is StandardOrderType.Limit or StandardOrderType.Stop) &&
            orderPrice is null or <= 0m)
        {
            throw new ArgumentException("A positive order price is required for pending OANDA orders.", nameof(request));
        }

        string timeInForce = request.Type == StandardOrderType.Market
            ? request.TimeInForce == StandardTimeInForce.ImmediateOrCancel ? "IOC" : "FOK"
            : request.TimeInForce switch
            {
                StandardTimeInForce.GoodTillDate when request.ExpireAt is not null => "GTD",
                StandardTimeInForce.Day => "GFD",
                StandardTimeInForce.GoodTillCancelled => "GTC",
                _ => throw new BrokerFeatureNotSupportedException(
                    BrokerKind.Oanda,
                    $"{request.TimeInForce} for {request.Type} orders")
            };
        decimal signedUnits = request.Side switch
        {
            OrderSide.Buy => request.Quantity.Value,
            OrderSide.Sell => -request.Quantity.Value,
            _ => throw new ArgumentException("The OANDA order side must be Buy or Sell.", nameof(request))
        };

        return new OandaCreateOrderEnvelope
        {
            Order = new OandaCreateOrderRequest
            {
                Type = type,
                Instrument = nativeInstrument,
                Units = Format(signedUnits),
                TimeInForce = timeInForce,
                Price = orderPrice is null ? null : Format(orderPrice.Value),
                GtdTime = timeInForce == "GTD"
                    ? request.ExpireAt!.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                    : null,
                ClientExtensions = new OandaClientExtensions { Id = clientOrderId },
                StopLossOnFill = DependentOrder(request.StopLoss?.Price),
                TakeProfitOnFill = DependentOrder(request.TakeProfit?.Price)
            }
        };
    }

    public static OrderSubmission ToSubmission(
        OandaOrderMutationResponse response,
        string clientOrderId)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.OrderRejectTransaction is { } rejected)
        {
            return new OrderSubmission
            {
                ClientOrderId = clientOrderId,
                BrokerOrderId = rejected.OrderId ?? rejected.Id,
                Status = SubmissionStatus.Rejected,
                Certainty = ExecutionCertainty.Rejected,
                RejectionReason = response.ErrorMessage ?? rejected.Reason ?? "OANDA rejected the order."
            };
        }

        OandaTransaction? created = response.OrderCreateTransaction;
        OandaTransaction? filled = response.OrderFillTransaction;
        if (created is null && filled is null)
        {
            throw new InvalidOperationException("OANDA did not return an order creation or fill transaction.");
        }

        return new OrderSubmission
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = filled?.OrderId ?? created?.OrderId ?? created?.Id,
            Status = filled is null ? SubmissionStatus.Accepted : SubmissionStatus.Filled,
            Certainty = ExecutionCertainty.Accepted
        };
    }

    public static OrderEvent? ToOrderEvent(
        OandaTransaction transaction,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        string type = transaction.Type?.ToUpperInvariant() ?? string.Empty;
        OrderEventType? eventType = type switch
        {
            "MARKET_ORDER" or "LIMIT_ORDER" or "STOP_ORDER" or "MARKET_IF_TOUCHED_ORDER" =>
                OrderEventType.Accepted,
            "ORDER_FILL" => OrderEventType.Filled,
            "ORDER_CANCEL" => OrderEventType.Cancelled,
            _ when type.EndsWith("_REJECT", StringComparison.Ordinal) => OrderEventType.Rejected,
            _ => null
        };
        if (eventType is null || string.IsNullOrWhiteSpace(transaction.OrderId ?? transaction.Id))
        {
            return null;
        }

        decimal? units = BrokerJson.ParseNullableDecimal(transaction.Units);
        return new OrderEvent
        {
            BrokerOrderId = transaction.OrderId ?? transaction.Id!,
            ClientOrderId = transaction.ClientExtensions?.Id ?? transaction.ClientOrderId,
            Instrument = string.IsNullOrWhiteSpace(transaction.Instrument)
                ? new InstrumentKey("OANDA:UNKNOWN")
                : InstrumentMappers.FromNative(transaction.Instrument, instrumentMappings),
            Type = eventType.Value,
            Timestamp = ParseDate(transaction.Time) ?? DateTimeOffset.UtcNow,
            FillPrice = eventType == OrderEventType.Filled
                ? BrokerJson.ParseNullableDecimal(transaction.Price)
                : null,
            FillQuantity = eventType == OrderEventType.Filled && units is not null
                ? Math.Abs(units.Value)
                : null,
            Message = transaction.Reason
        };
    }

    public static OandaPriceTick? ToPriceTick(OandaPricingStreamMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Type, "PRICE", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(message.Instrument) ||
            string.IsNullOrWhiteSpace(message.Time) ||
            message.Bids.Count == 0 ||
            message.Asks.Count == 0)
        {
            return null;
        }

        decimal bid = message.Bids
            .Select(bucket => BrokerJson.ParseDecimal(bucket.Price))
            .Max();
        decimal ask = message.Asks
            .Select(bucket => BrokerJson.ParseDecimal(bucket.Price))
            .Min();
        if (bid <= 0m || ask <= 0m || ask < bid)
        {
            throw new InvalidOperationException("OANDA returned an invalid bid/ask price.");
        }

        return new OandaPriceTick(
            message.Instrument,
            DateTimeOffset.Parse(
                message.Time,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            bid,
            ask,
            message.Tradeable);
    }

    private static OandaDependentOrderRequest? DependentOrder(decimal? price)
    {
        if (price is null)
        {
            return null;
        }
        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price));
        }

        return new OandaDependentOrderRequest { Price = Format(price.Value) };
    }

    private static string Format(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
