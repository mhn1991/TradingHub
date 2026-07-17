using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using Simulator.Engine;
using TradingCore.Pipeline;

namespace Simulator.Tests;

[TestFixture]
public sealed class AnalysisProfileRegistryTests
{
    private static readonly IReadOnlySet<BarInterval> Intervals =
        new HashSet<BarInterval> { BarInterval.Minutes(5), BarInterval.Minutes(15) };

    [Test]
    public void GetOrCreateProfile_IdenticalOptions_ReturnsSameKeyAndEngine()
    {
        var registry = new AnalysisProfileRegistry("schema-v1");
        var options = new ChartAnnotationOptions { RsiPeriod = 14 };

        AnalysisProfileKey a = registry.GetOrCreateProfile(options, Intervals);
        AnalysisProfileKey b = registry.GetOrCreateProfile(new ChartAnnotationOptions { RsiPeriod = 14 }, Intervals);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(registry.EngineFor(a), Is.SameAs(registry.EngineFor(b)));
        Assert.That(registry.Snapshot, Has.Count.EqualTo(1));
    }

    [Test]
    public void GetOrCreateProfile_DifferentRsiPeriod_ReturnsDistinctProfilesAndEngines()
    {
        var registry = new AnalysisProfileRegistry("schema-v1");

        AnalysisProfileKey a = registry.GetOrCreateProfile(new ChartAnnotationOptions { RsiPeriod = 14 }, Intervals);
        AnalysisProfileKey b = registry.GetOrCreateProfile(new ChartAnnotationOptions { RsiPeriod = 21 }, Intervals);

        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(registry.EngineFor(a), Is.Not.SameAs(registry.EngineFor(b)));
        Assert.That(registry.Snapshot, Has.Count.EqualTo(2));
    }

    [Test]
    public void GetOrCreateProfile_DifferentRequiredIntervals_ReturnsDistinctProfiles()
    {
        var registry = new AnalysisProfileRegistry("schema-v1");
        var options = new ChartAnnotationOptions();

        AnalysisProfileKey a = registry.GetOrCreateProfile(options, Intervals);
        AnalysisProfileKey b = registry.GetOrCreateProfile(
            options, new HashSet<BarInterval> { BarInterval.Minutes(5) });

        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(registry.Snapshot, Has.Count.EqualTo(2));
    }

    [Test]
    public void GetOrCreateProfile_NullOptions_TreatedAsDefaultOptions()
    {
        var registry = new AnalysisProfileRegistry("schema-v1");

        AnalysisProfileKey a = registry.GetOrCreateProfile(null, Intervals);
        AnalysisProfileKey b = registry.GetOrCreateProfile(new ChartAnnotationOptions(), Intervals);

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void EngineFor_UnregisteredProfile_Throws()
    {
        var registry = new AnalysisProfileRegistry("schema-v1");
        AnalysisProfileKey unregistered = AnalysisProfileKey.Create(
            new ChartAnnotationOptions(), Intervals, "schema-v1");

        Assert.Throws<InvalidOperationException>(() => registry.EngineFor(unregistered));
    }

    [Test]
    public void Publish_MutatingSourceDictionaryAfterConstruction_DoesNotAffectSnapshot()
    {
        var instrument = new InstrumentKey("FX:EUR/USD");
        AnalysisProfileKey profile = AnalysisProfileKey.Create(new ChartAnnotationOptions(), Intervals, "schema-v1");
        var interval = BarInterval.Minutes(5);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new Dictionary<BarInterval, AnalysisSnapshot>
        {
            [interval] = new AnalysisSnapshot
            {
                Instrument = instrument,
                Interval = interval,
                AvailableAt = now,
                Version = 1,
                LatestCandle = new Candle
                {
                    Instrument = instrument,
                    Interval = interval,
                    OpenTime = now.AddMinutes(-5),
                    CloseTime = now,
                    Prices = new Ohlc(1.1000m, 1.1005m, 1.0995m, 1.1000m),
                    IsComplete = true
                },
                Indicators = new IndicatorSnapshot(),
                Swings = [],
                PriceZones = [],
                Trendlines = [],
                Channels = [],
                Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
            }
        };

        MarketAnalysisSnapshot snapshot = AnalysisProfileRegistry.Publish(
            instrument, profile, snapshotVersion: 1, decisionEpoch: 1, availableAt: now,
            timeframes: source, crossMarket: null, dataQuality: TradingCore.MarketData.DataQualityResult.Valid);

        source[BarInterval.Minutes(15)] = source[interval];

        Assert.That(snapshot.Timeframes.Count, Is.EqualTo(1));
    }
}
