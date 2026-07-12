using Brokers.Abstractions;
using Brokers.Exceptions;
using Brokers.Infrastructure;
using Brokers.Models;

namespace Brokers.Binance;

internal static class BinanceMappings
{
    public static string ToInterval(BarInterval interval) => interval switch
    {
        { Unit: BarUnit.Second, Value: 1 } => "1s",
        { Unit: BarUnit.Minute, Value: 1 } => "1m",
        { Unit: BarUnit.Minute, Value: 3 } => "3m",
        { Unit: BarUnit.Minute, Value: 5 } => "5m",
        { Unit: BarUnit.Minute, Value: 15 } => "15m",
        { Unit: BarUnit.Minute, Value: 30 } => "30m",
        { Unit: BarUnit.Hour, Value: 1 } => "1h",
        { Unit: BarUnit.Hour, Value: 2 } => "2h",
        { Unit: BarUnit.Hour, Value: 4 } => "4h",
        { Unit: BarUnit.Hour, Value: 6 } => "6h",
        { Unit: BarUnit.Hour, Value: 8 } => "8h",
        { Unit: BarUnit.Hour, Value: 12 } => "12h",
        { Unit: BarUnit.Day, Value: 1 } => "1d",
        { Unit: BarUnit.Day, Value: 3 } => "3d",
        { Unit: BarUnit.Week, Value: 1 } => "1w",
        { Unit: BarUnit.Month, Value: 1 } => "1M",
        _ => throw new BrokerFeatureNotSupportedException(
            BrokerKind.Binance,
            $"Candle interval {interval}")
    };

    public static Candle ToCandle(
        BinanceKlineRow row,
        CandleQuery query,
        TimeProvider timeProvider) => new()
        {
            Instrument = query.Instrument,
            Interval = query.Interval,
            OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(row.OpenTime),
            CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(row.CloseTime),
            Prices = new Ohlc(
            BrokerJson.ParseDecimal(row.Open),
            BrokerJson.ParseDecimal(row.High),
            BrokerJson.ParseDecimal(row.Low),
            BrokerJson.ParseDecimal(row.Close)),
            Volume = new MarketVolume(
            BrokerJson.ParseDecimal(row.Volume),
            VolumeKind.BaseAssetQuantity),
            IsComplete = row.CloseTime < timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

    public static BrokerOrder ToOrder(
        BinanceOrderDto source,
        IReadOnlyDictionary<string, string> instrumentMappings) => new()
        {
            BrokerOrderId = source.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ClientOrderId = source.ClientOrderId,
            Instrument = InstrumentMappers.FromNative(source.Symbol, instrumentMappings),
            NativeInstrument = source.Symbol,
            Side = source.Side?.ToUpperInvariant() switch
            {
                "BUY" => OrderSide.Buy,
                "SELL" => OrderSide.Sell,
                _ => OrderSide.Unknown
            },
            Type = source.Type ?? "UNKNOWN",
            Status = source.Status ?? "UNKNOWN",
            NormalizedStatus = ToStatus(source.Status),
            Quantity = BrokerJson.ParseNullableDecimal(source.OriginalQuantity),
            FilledQuantity = BrokerJson.ParseNullableDecimal(source.ExecutedQuantity),
            Price = BrokerJson.ParseNullableDecimal(source.Price),
            CreatedAt = source.Time > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(source.Time)
            : null
        };

    private static OrderStatus ToStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "NEW" => OrderStatus.Open,
        "PENDING_NEW" => OrderStatus.Pending,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELED" => OrderStatus.Cancelled,
        "PENDING_CANCEL" => OrderStatus.Pending,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
        _ => OrderStatus.Unknown
    };
}
