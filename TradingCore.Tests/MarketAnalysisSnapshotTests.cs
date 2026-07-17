using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using NUnit.Framework;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace TradingCore.Tests;

[TestFixture]
public sealed class MarketAnalysisSnapshotTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Timeframes_MutatingSourceDictionaryAfterConstruction_DoesNotAffectSnapshot()
    {
        var source = new Dictionary<BarInterval, AnalysisSnapshot> { [Interval] = BuildSnapshot() };
        // Mirrors the existing StreamingComparativeEngine.cs defensive-copy pattern this type is
        // modeled on - the snapshot must own a fresh copy, never a live reference into a
        // caller-owned working dictionary.
        var frozenCopy = new Dictionary<BarInterval, AnalysisSnapshot>(source);

        var snapshot = new MarketAnalysisSnapshot
        {
            Instrument = Instrument,
            Profile = AnalysisProfileKey.Create(
                new ChartAnnotationOptions(),
                new HashSet<BarInterval> { Interval },
                "schema-v1"),
            SnapshotVersion = 1,
            DecisionEpoch = 1,
            AvailableAt = Now,
            Timeframes = frozenCopy,
            CrossMarket = null,
            DataQuality = DataQualityResult.Valid
        };

        source[Interval] = BuildSnapshot() with { Version = 999 };
        source[BarInterval.Minutes(15)] = BuildSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Timeframes.Count, Is.EqualTo(1));
            Assert.That(snapshot.Timeframes[Interval].Version, Is.EqualTo(1));
        });
    }

    private static AnalysisSnapshot BuildSnapshot() => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = new Candle
        {
            Instrument = Instrument,
            Interval = Interval,
            OpenTime = Now.AddMinutes(-5),
            CloseTime = Now,
            Prices = new Ohlc(1.1000m, 1.1005m, 1.0995m, 1.1000m),
            IsComplete = true
        },
        Indicators = new IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
