using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class IndicatorAndAnnotationEdgeTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = SimulationTestHarness.DefaultStart;

    [TestCase(1)]
    [TestCase(0)]
    [TestCase(-1)]
    public void IndicatorPeriodsBelowTwo_AreRejected(int period)
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new AtrState(period), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new RsiState(period), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new BollingerState(period), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void Bollinger_RejectsNonPositiveDeviationMultiplier()
    {
        Assert.That(
            () => new BollingerState(20, 0m),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void FlatPrices_ProduceNeutralRsiAndZeroWidthBollingerBands()
    {
        var rsi = new RsiState(2);
        var bollinger = new BollingerState(2);
        for (int index = 0; index < 3; index++)
        {
            rsi.Update(100m);
            bollinger.Update(100m);
        }

        Assert.Multiple(() =>
        {
            Assert.That(rsi.IsReady, Is.True);
            Assert.That(rsi.Current, Is.EqualTo(50m));
            Assert.That(bollinger.IsReady, Is.True);
            Assert.That(bollinger.Middle, Is.EqualTo(100m));
            Assert.That(bollinger.Upper, Is.EqualTo(100m));
            Assert.That(bollinger.Lower, Is.EqualTo(100m));
        });
    }

    [Test]
    public void Atr_SeedsAtConfiguredPeriodAndThenUsesWilderSmoothing()
    {
        var atr = new AtrState(2);
        atr.Update(Candle(0, 100m, 102m, 99m, 101m)); // TR 3
        Assert.That(atr.IsReady, Is.False);
        atr.Update(Candle(1, 101m, 105m, 100m, 104m)); // TR 5

        Assert.That(atr.Current, Is.EqualTo(4m));
        atr.Update(Candle(2, 104m, 106m, 103m, 105m)); // TR 3
        Assert.That(atr.Current, Is.EqualTo(3.5m));
    }

    [TestCaseSource(nameof(InvalidAnnotationOptions))]
    public void AnnotationEngine_RejectsInvalidOptions(ChartAnnotationOptions options)
    {
        Assert.That(
            () => new ChartAnnotationEngine(options),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void AnnotationEngine_RejectsInvalidEventEnvelope()
    {
        var engine = new ChartAnnotationEngine();
        Candle valid = Candle(0, 100m, 101m, 99m, 100m);
        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await engine.ProcessAsync(null!),
                Throws.TypeOf<ArgumentNullException>());
            Assert.That(
                async () => await engine.ProcessAsync(new CandleClosedEvent(
                    Instrument,
                    Interval,
                    valid with { IsComplete = false },
                    1)),
                Throws.ArgumentException.With.Message.Contains("completed"));
            Assert.That(
                async () => await engine.ProcessAsync(new CandleClosedEvent(
                    "FX:EUR/USD",
                    Interval,
                    valid,
                    1)),
                Throws.ArgumentException.With.Message.Contains("identity"));
            Assert.That(
                async () => await engine.ProcessAsync(new CandleClosedEvent(
                    Instrument,
                    Interval,
                    valid with { CloseTime = null },
                    1)),
                Throws.ArgumentException.With.Message.Contains("close time"));
        });
    }

    [Test]
    public async Task AnnotationEngine_RejectsDuplicateTimeAndNonIncreasingSequence()
    {
        var engine = new ChartAnnotationEngine();
        Candle first = Candle(0, 100m, 101m, 99m, 100m);
        await engine.ProcessAsync(new CandleClosedEvent(Instrument, Interval, first, 1));

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await engine.ProcessAsync(new CandleClosedEvent(
                    Instrument,
                    Interval,
                    first,
                    2)),
                Throws.InvalidOperationException.With.Message.Contains("chronological"));
            Assert.That(
                async () => await engine.ProcessAsync(new CandleClosedEvent(
                    Instrument,
                    Interval,
                    Candle(1, 100m, 101m, 99m, 100m),
                    1)),
                Throws.InvalidOperationException.With.Message.Contains("sequences"));
        });
    }

    [Test]
    public async Task AnnotationHistory_RespectsCapacityAndVersionsIncrease()
    {
        var engine = new ChartAnnotationEngine(new ChartAnnotationOptions
        {
            CandleCapacity = 2,
            IndicatorCapacity = 2,
            SwingCapacity = 2,
            AtrPeriod = 2,
            RsiPeriod = 2,
            BollingerPeriod = 2
        });
        AnalysisSnapshot? latest = null;
        for (int index = 0; index < 3; index++)
        {
            latest = await engine.ProcessAsync(new CandleClosedEvent(
                Instrument,
                Interval,
                Candle(index, 100m + index, 101m + index, 99m + index, 100m + index),
                index + 1));
        }

        Assert.Multiple(() =>
        {
            Assert.That(latest!.Version, Is.EqualTo(3));
            Assert.That(engine.GetCandles(Instrument, Interval), Has.Count.EqualTo(2));
            Assert.That(engine.GetIndicatorHistory(Instrument, Interval), Has.Count.EqualTo(2));
            Assert.That(engine.GetLatest("FX:EUR/USD", Interval), Is.Null);
        });
    }

    private static IEnumerable<ChartAnnotationOptions> InvalidAnnotationOptions()
    {
        yield return new ChartAnnotationOptions { CandleCapacity = 0 };
        yield return new ChartAnnotationOptions { SwingCapacity = 0 };
        yield return new ChartAnnotationOptions { IndicatorCapacity = 0 };
        yield return new ChartAnnotationOptions { HeavyAnalysisEveryCandles = 0 };
        yield return new ChartAnnotationOptions { AtrPeriod = 1 };
        yield return new ChartAnnotationOptions { RsiPeriod = 1 };
        yield return new ChartAnnotationOptions { BollingerPeriod = 1 };
        yield return new ChartAnnotationOptions { BollingerStandardDeviations = 0m };
        yield return new ChartAnnotationOptions { SwingLeftBars = 0 };
        yield return new ChartAnnotationOptions { SwingRightBars = 0 };
    }

    private static Candle Candle(
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close) => TestCandles.Create(
            Instrument,
            Start.AddMinutes(index * 5),
            Interval,
            open,
            high,
            low,
            close);
}
