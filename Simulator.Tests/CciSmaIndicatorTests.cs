using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class CciSmaIndicatorTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void SmaState_AveragesCloseOverWindow()
    {
        var sma = new SmaState(3);
        sma.Update(1m);
        sma.Update(2m);
        Assert.That(sma.IsReady, Is.False);
        sma.Update(3m);
        Assert.Multiple(() =>
        {
            Assert.That(sma.IsReady, Is.True);
            Assert.That(sma.Current, Is.EqualTo(2m));
        });
        sma.Update(6m);
        Assert.That(sma.Current, Is.EqualTo(11m / 3m).Within(0.0000001m));
    }

    [Test]
    public void CciState_IsZeroWhenTypicalPriceIsFlat()
    {
        var cci = new CciState(period: 5);
        for (int index = 0; index < 5; index++)
            cci.Update(Candle(index, high: 1.10m, low: 1.10m, close: 1.10m));

        Assert.Multiple(() =>
        {
            Assert.That(cci.IsReady, Is.True);
            Assert.That(cci.Current, Is.EqualTo(0m));
        });
    }

    [Test]
    public void CciState_IsZeroWhenRepeatingDecimalTypicalPriceRoundsAtWindowAverage()
    {
        var cci = new CciState(period: 20);
        for (int index = 0; index < 20; index++)
            cci.Update(Candle(index, high: 1.1005m, low: 1.0995m, close: 1.1002m));

        Assert.That(cci.Current, Is.EqualTo(0m));
    }

    [Test]
    public async Task Engine_PublishesCciAndMovingAverages_WhenWindowsFill()
    {
        var options = new ChartAnnotationOptions
        {
            AtrPeriod = 3,
            RsiPeriod = 3,
            BollingerPeriod = 3,
            CciPeriod = 5,
            SmaFastPeriod = 3,
            SmaSlowPeriod = 5,
            HeavyAnalysisEveryCandles = 1,
            CandleCapacity = 100,
            IndicatorCapacity = 100,
            SwingCapacity = 50
        };
        options.Validate();
        var engine = new ChartAnnotationEngine(options);

        AnalysisSnapshot? last = null;
        for (int index = 0; index < 8; index++)
        {
            decimal close = 1.1000m + (index * 0.0005m);
            last = await engine.ProcessAsync(
                new CandleClosedEvent(
                    Instrument,
                    Interval,
                    Candle(index, high: close + 0.0002m, low: close - 0.0002m, close: close),
                    index + 1),
                runtimeContext: null);
        }

        Assert.That(last, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(last!.Indicators.Cci, Is.Not.Null);
            Assert.That(last.Indicators.Sma50, Is.Not.Null);
            Assert.That(last.Indicators.Sma200, Is.Not.Null);
            // Fast SMA window is 3 closes: 1.1025, 1.1030, 1.1035
            Assert.That(last.Indicators.Sma50, Is.EqualTo(1.1030m).Within(0.0000001m));
            // Slow SMA window is 5 closes ending at 1.1035
            Assert.That(last.Indicators.Sma200, Is.EqualTo(1.1025m).Within(0.0000001m));
        });
    }

    private static Candle Candle(int index, decimal high, decimal low, decimal close)
    {
        DateTimeOffset open = Start.AddMinutes(5 * index);
        return new Candle
        {
            Instrument = Instrument,
            Interval = Interval,
            OpenTime = open,
            CloseTime = open.AddMinutes(5),
            Prices = new Ohlc(close - 0.0001m, high, low, close),
            Volume = new MarketVolume(100m + index, VolumeKind.TickCount),
            IsComplete = true
        };
    }
}
