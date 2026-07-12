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

        return new Candle
        {
            Instrument = query.Instrument,
            Interval = query.Interval,
            OpenTime = DateTimeOffset.Parse(
                source.Time,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
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
            ClientOrderId = source.ClientExtensions?.Id,
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
