using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;

namespace Dashboard.Contracts;

public sealed record ReplayDataset(
    int SchemaVersion,
    string Title,
    string Instrument,
    DateTimeOffset GeneratedAt,
    string Source,
    ChartAnnotationOptions Parameters,
    IReadOnlyList<ReplaySeries> Series);

public sealed record ReplaySeries(
    string Interval,
    int IntervalSeconds,
    IReadOnlyList<ReplayFrame> Frames);

public sealed record ReplayFrame(
    int Index,
    DateTimeOffset AvailableAt,
    ReplayCandle Candle,
    IndicatorSnapshot Indicators,
    IReadOnlyList<SwingPoint> Swings,
    IReadOnlyList<PriceZone> PriceZones,
    IReadOnlyList<Trendline> Trendlines,
    IReadOnlyList<PriceChannel> Channels,
    ConfidenceScore Confidence,
    double AnalysisMicroseconds);

public sealed record ReplayCandle(
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public static class ReplayContractMapper
{
    public static ReplayFrame ToFrame(
        AnalysisSnapshot snapshot,
        int index,
        double analysisMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Candle candle = snapshot.LatestCandle;
        return new ReplayFrame(
            Index: index,
            AvailableAt: snapshot.AvailableAt,
            Candle: new ReplayCandle(
                candle.OpenTime,
                candle.CloseTime ?? snapshot.AvailableAt,
                candle.Prices.Open,
                candle.Prices.High,
                candle.Prices.Low,
                candle.Prices.Close,
                candle.Volume?.Value ?? 0m),
            Indicators: snapshot.Indicators,
            Swings: snapshot.Swings,
            PriceZones: snapshot.PriceZones,
            Trendlines: snapshot.Trendlines,
            Channels: snapshot.Channels,
            Confidence: snapshot.Confidence,
            AnalysisMicroseconds: analysisMicroseconds);
    }
}
