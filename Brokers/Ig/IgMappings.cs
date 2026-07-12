using System.Globalization;
using Brokers.Abstractions;
using Brokers.Exceptions;
using Brokers.Infrastructure;
using Brokers.Models;

namespace Brokers.Ig;

internal static class IgMappings
{
    public static string ToResolution(BarInterval interval) => interval switch
    {
        { Unit: BarUnit.Minute, Value: 1 } => "MINUTE",
        { Unit: BarUnit.Minute, Value: 2 } => "MINUTE_2",
        { Unit: BarUnit.Minute, Value: 3 } => "MINUTE_3",
        { Unit: BarUnit.Minute, Value: 5 } => "MINUTE_5",
        { Unit: BarUnit.Minute, Value: 10 } => "MINUTE_10",
        { Unit: BarUnit.Minute, Value: 15 } => "MINUTE_15",
        { Unit: BarUnit.Minute, Value: 30 } => "MINUTE_30",
        { Unit: BarUnit.Hour, Value: 1 } => "HOUR",
        { Unit: BarUnit.Hour, Value: 2 } => "HOUR_2",
        { Unit: BarUnit.Hour, Value: 3 } => "HOUR_3",
        { Unit: BarUnit.Hour, Value: 4 } => "HOUR_4",
        { Unit: BarUnit.Day, Value: 1 } => "DAY",
        { Unit: BarUnit.Week, Value: 1 } => "WEEK",
        { Unit: BarUnit.Month, Value: 1 } => "MONTH",
        _ => throw new BrokerFeatureNotSupportedException(
            BrokerKind.Ig,
            $"Candle interval {interval}")
    };

    public static Candle ToCandle(
        IgHistoricalPrice source,
        CandleQuery query,
        TimeProvider timeProvider)
    {
        DateTimeOffset openTime = ParseSnapshotTime(source.SnapshotTimeUtc ?? source.SnapshotTime);
        DateTimeOffset closeTime = AddInterval(openTime, query.Interval);

        return new Candle
        {
            Instrument = query.Instrument,
            Interval = query.Interval,
            OpenTime = openTime,
            CloseTime = closeTime,
            Prices = new Ohlc(
                Midpoint(source.OpenPrice),
                Midpoint(source.HighPrice),
                Midpoint(source.LowPrice),
                Midpoint(source.ClosePrice)),
            Volume = source.LastTradedVolume is null
                ? null
                : new MarketVolume(source.LastTradedVolume.Value, VolumeKind.LastTradedQuantity),
            IsComplete = closeTime <= timeProvider.GetUtcNow()
        };
    }

    public static BrokerOrder ToOrder(
        IgWorkingOrder source,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        IgWorkingOrderData data = source.WorkingOrderData
            ?? throw new InvalidOperationException("IG working order did not include workingOrderData.");

        string nativeInstrument = source.MarketData?.Epic
            ?? throw new InvalidOperationException("IG working order did not include a market EPIC.");
        string brokerOrderId = data.DealId ?? data.DealReference
            ?? throw new InvalidOperationException("IG working order did not include an identifier.");

        return new BrokerOrder
        {
            BrokerOrderId = brokerOrderId,
            ClientOrderId = data.DealReference,
            Instrument = InstrumentMappers.FromNative(nativeInstrument, instrumentMappings),
            NativeInstrument = nativeInstrument,
            Side = ToSide(data.Direction),
            Type = data.OrderType ?? "UNKNOWN",
            Status = "WORKING",
            NormalizedStatus = OrderStatus.Open,
            Quantity = data.Size,
            Price = data.Level,
            CreatedAt = ParseOptionalSnapshotTime(data.CreatedDateUtc ?? data.CreatedDate)
        };
    }

    public static BrokerPosition ToPosition(
        IgPositionItem source,
        IReadOnlyDictionary<string, string> instrumentMappings)
    {
        IgPositionData position = source.Position
            ?? throw new InvalidOperationException("IG position did not include position data.");

        string nativeInstrument = source.Market?.Epic
            ?? throw new InvalidOperationException("IG position did not include a market EPIC.");
        string positionId = position.DealId ?? position.DealReference
            ?? throw new InvalidOperationException("IG position did not include an identifier.");

        return new BrokerPosition
        {
            PositionId = positionId,
            Instrument = InstrumentMappers.FromNative(nativeInstrument, instrumentMappings),
            NativeInstrument = nativeInstrument,
            Side = ToSide(position.Direction),
            Quantity = position.Size,
            AveragePrice = position.Level,
            Currency = position.Currency
        };
    }

    private static OrderSide ToSide(string? direction) => direction?.ToUpperInvariant() switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => OrderSide.Unknown
    };

    private static decimal Midpoint(IgPricePoint? point)
    {
        if (point is null)
        {
            throw new InvalidOperationException("IG price data did not contain a price point.");
        }

        if (point.Bid is not null && point.Ask is not null)
        {
            return (point.Bid.Value + point.Ask.Value) / 2m;
        }

        return point.LastTraded ?? point.Bid ?? point.Ask
            ?? throw new InvalidOperationException("IG price point did not contain a usable value.");
    }

    private static DateTimeOffset ParseSnapshotTime(string? value) =>
        ParseOptionalSnapshotTime(value)
        ?? throw new InvalidOperationException("IG price data did not contain a timestamp.");

    private static DateTimeOffset? ParseOptionalSnapshotTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats =
        [
            "yyyy/MM/dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss.fff",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.fff",
            "O"
        ];

        if (DateTimeOffset.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset result))
        {
            return result;
        }

        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static DateTimeOffset AddInterval(DateTimeOffset value, BarInterval interval) =>
        interval.Unit switch
        {
            BarUnit.Second => value.AddSeconds(interval.Value),
            BarUnit.Minute => value.AddMinutes(interval.Value),
            BarUnit.Hour => value.AddHours(interval.Value),
            BarUnit.Day => value.AddDays(interval.Value),
            BarUnit.Week => value.AddDays(interval.Value * 7d),
            BarUnit.Month => value.AddMonths(interval.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };
}
