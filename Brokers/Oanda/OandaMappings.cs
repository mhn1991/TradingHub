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


    public static bool IsTradeDependentOrder(string? type) =>
        type is not null && type.ToUpperInvariant() is
            "STOP_LOSS" or "TAKE_PROFIT" or "TRAILING_STOP_LOSS" or "GUARANTEED_STOP_LOSS";

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
            BrokerTradeId = source.TradeId,
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


    public static BrokerPosition ToPosition(
        OandaTrade source,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        ArgumentNullException.ThrowIfNull(source);
        decimal currentUnits = BrokerJson.ParseDecimal(source.CurrentUnits);
        if (currentUnits == 0m)
            throw new InvalidOperationException("OANDA returned an open trade with zero current units.");
        return new BrokerPosition
        {
            PositionId = source.Id,
            Instrument = InstrumentMappers.FromNative(source.Instrument, instrumentMappings),
            NativeInstrument = source.Instrument,
            Side = currentUnits > 0m ? OrderSide.Buy : OrderSide.Sell,
            InitialQuantity = Math.Abs(BrokerJson.ParseNullableDecimal(source.InitialUnits) ?? currentUnits),
            Quantity = Math.Abs(currentUnits),
            AveragePrice = BrokerJson.ParseNullableDecimal(source.Price),
            UnrealizedProfitLoss = BrokerJson.ParseNullableDecimal(source.UnrealizedPl),
            ClientTradeId = source.ClientExtensions?.Id,
            ProtectiveStopOrderId = source.StopLossOrder?.Id,
            ProtectiveStopPrice = BrokerJson.ParseNullableDecimal(source.StopLossOrder?.Price),
            TakeProfitOrderId = source.TakeProfitOrder?.Id,
            TakeProfitPrice = BrokerJson.ParseNullableDecimal(source.TakeProfitOrder?.Price),
            OpenedAt = ParseDate(source.OpenTime)
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

    public static InstrumentTradingMetadata ToInstrumentMetadata(
        OandaInstrument instrument,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        InstrumentKey canonical = InstrumentMappers.FromNative(instrument.Name, instrumentMappings);
        decimal minimum = BrokerJson.ParseNullableDecimal(instrument.MinimumTradeSize) ?? 1m;
        decimal? maximum = BrokerJson.ParseNullableDecimal(instrument.MaximumOrderUnits);
        if (maximum <= 0m)
            maximum = null;
        var metadata = new InstrumentTradingMetadata
        {
            Instrument = canonical,
            MinimumQuantity = minimum,
            MaximumOrderQuantity = maximum,
            QuantityStep = DecimalIncrement(instrument.TradeUnitsPrecision),
            PriceIncrement = DecimalIncrement(instrument.DisplayPrecision),
            PipSize = DecimalPowerOfTen(instrument.PipLocation),
            MarginRate = BrokerJson.ParseNullableDecimal(instrument.MarginRate),
            PricePrecision = instrument.DisplayPrecision,
            QuantityPrecision = instrument.TradeUnitsPrecision
        };
        metadata.Validate();
        return metadata;
    }

    public static string CanonicalInstrumentName(OandaInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        string prefix = string.Equals(instrument.Type, "CURRENCY", StringComparison.OrdinalIgnoreCase)
            ? "FX"
            : string.Equals(instrument.Type, "METAL", StringComparison.OrdinalIgnoreCase)
                ? "METAL"
                : string.Equals(instrument.Type, "CFD", StringComparison.OrdinalIgnoreCase)
                    ? "CFD"
                    : "OANDA";
        return $"{prefix}:{instrument.Name.Replace('_', '/')}";
    }

    private static decimal DecimalIncrement(int precision)
    {
        if (precision is < 0 or > 28)
            throw new ArgumentOutOfRangeException(nameof(precision));
        decimal value = 1m;
        for (int index = 0; index < precision; index++)
            value /= 10m;
        return value;
    }

    private static decimal DecimalPowerOfTen(int exponent)
    {
        if (exponent is < -28 or > 28)
            throw new ArgumentOutOfRangeException(nameof(exponent));
        decimal value = 1m;
        if (exponent >= 0)
        {
            for (int index = 0; index < exponent; index++)
                value *= 10m;
        }
        else
        {
            for (int index = 0; index > exponent; index--)
                value /= 10m;
        }
        return value;
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
                PositionFill = request.ReduceOnly ? "REDUCE_ONLY" : "DEFAULT",
                Price = orderPrice is null ? null : Format(orderPrice.Value),
                GtdTime = timeInForce == "GTD"
                    ? request.ExpireAt!.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                    : null,
                ClientExtensions = new OandaClientExtensions { Id = clientOrderId },
                TradeClientExtensions = new OandaClientExtensions { Id = clientOrderId },
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
        OandaTradeSummary? closedTrade = transaction.TradesClosed.FirstOrDefault();
        string? brokerTradeId = transaction.TradeOpened?.TradeId ??
            transaction.TradeReduced?.TradeId ?? closedTrade?.TradeId ?? transaction.TradeId;
        OrderPositionEffect positionEffect = transaction.TradeOpened is not null
            ? OrderPositionEffect.OpenOrIncrease
            : transaction.TradeReduced is not null
                ? OrderPositionEffect.Reduce
                : transaction.TradesClosed.Count > 0
                    ? OrderPositionEffect.Close
                    : OrderPositionEffect.Unknown;
        decimal? tradeUnits = BrokerJson.ParseNullableDecimal(
            transaction.TradeOpened?.Units ?? transaction.TradeReduced?.Units ?? closedTrade?.Units);
        decimal? effectiveFillQuantity = units is not null
            ? Math.Abs(units.Value)
            : tradeUnits is not null
                ? Math.Abs(tradeUnits.Value)
                : null;
        decimal realizedProfitLoss = BrokerJson.ParseNullableDecimal(
            transaction.TradeReduced?.RealizedPl) ?? 0m;
        realizedProfitLoss += transaction.TradesClosed.Sum(
            trade => BrokerJson.ParseNullableDecimal(trade.RealizedPl) ?? 0m);

        return new OrderEvent
        {
            BrokerTransactionId = transaction.Id,
            BrokerTradeId = brokerTradeId,
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
            FillQuantity = eventType == OrderEventType.Filled ? effectiveFillQuantity : null,
            PositionEffect = positionEffect,
            PositionQuantityAfter = positionEffect == OrderPositionEffect.Close ? 0m : null,
            RealizedProfitLoss = realizedProfitLoss == 0m ? null : realizedProfitLoss,
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
