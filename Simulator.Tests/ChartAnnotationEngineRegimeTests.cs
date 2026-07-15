using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using ChartAnnotator.PriceAction;
using ChartAnnotator.Regime;

namespace Simulator.Tests;

[TestFixture]
public sealed class ChartAnnotationEngineRegimeTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Candle Candle(int index, decimal open, decimal high, decimal low, decimal close) =>
        TestCandles.Create(Instrument, Start.AddMinutes(5 * index), Interval, open, high, low, close);

    private static async Task<AnalysisSnapshot> RunAsync(ChartAnnotationEngine engine, IReadOnlyList<Candle> candles)
    {
        AnalysisSnapshot? last = null;
        for (int index = 0; index < candles.Count; index++)
        {
            last = await engine.ProcessAsync(new CandleClosedEvent(Instrument, Interval, candles[index], index));
        }

        return last!;
    }

    private static IReadOnlyList<Candle> BuildWarmupSequence(int count)
    {
        var candles = new List<Candle>(count);
        decimal price = 100m;
        for (int index = 0; index < count; index++)
        {
            // Small alternating up/down moves with a gentle upward drift: enough
            // range for ATR/Bollinger/ADX to warm up and enough zigzag for swings
            // and market structure to form.
            decimal drift = index % 2 == 0 ? 1.5m : -0.5m;
            decimal open = price;
            decimal close = price + drift;
            decimal high = Math.Max(open, close) + 0.5m;
            decimal low = Math.Min(open, close) - 0.5m;
            candles.Add(Candle(index, open, high, low, close));
            price = close;
        }

        return candles;
    }

    [Test]
    public async Task Disabled_LeavesMarketRegimeAtUnknownAcrossManyBars()
    {
        var engine = new ChartAnnotationEngine(new ChartAnnotationOptions
        {
            AtrPeriod = 3,
            RsiPeriod = 3,
            BollingerPeriod = 3,
            AdxPeriod = 3,
            EfficiencyRatioPeriod = 3,
            DonchianPeriod = 3
            // MarketRegime.Enabled defaults to false.
        });

        IReadOnlyList<Candle> candles = BuildWarmupSequence(30);
        for (int index = 0; index < candles.Count; index++)
        {
            AnalysisSnapshot snapshot = await engine.ProcessAsync(
                new CandleClosedEvent(Instrument, Interval, candles[index], index));
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.MarketRegime.Regime, Is.EqualTo(MarketRegime.Unknown));
                Assert.That(snapshot.MarketRegime.ReasonCode, Is.EqualTo("NoData"));
                Assert.That(snapshot.MarketRegime.IsTradeable, Is.True);
            });
        }
    }

    [Test]
    public async Task Enabled_ClassifierRunsAndProducesLiveEvaluations()
    {
        var engine = new ChartAnnotationEngine(new ChartAnnotationOptions
        {
            AtrPeriod = 3,
            AtrAnalysisHistoryPeriod = 10,
            AtrAnalysisMinimumSamples = 3,
            RsiPeriod = 3,
            BollingerPeriod = 3,
            BollingerWidthHistoryPeriod = 10,
            BollingerWidthMinimumSamples = 3,
            AdxPeriod = 3,
            EfficiencyRatioPeriod = 3,
            EfficiencyRatioAnalysisHistoryPeriod = 10,
            EfficiencyRatioAnalysisMinimumSamples = 3,
            DonchianPeriod = 3,
            PriceAction = new PriceActionOptions { CalibrationLookback = 10, CalibrationMinimumSamples = 3 },
            MarketRegime = new MarketRegimeOptions
            {
                Enabled = true,
                MinimumConfirmationBars = 1,
                MinimumPersistenceBars = 0,
                AdxCalibrationHistoryPeriod = 10,
                AdxCalibrationMinimumSamples = 3
            }
        });

        AnalysisSnapshot last = await RunAsync(engine, BuildWarmupSequence(30));

        Assert.Multiple(() =>
        {
            Assert.That(last.MarketRegime.ReasonCode, Is.Not.EqualTo("NoData"));
            Assert.That(last.MarketRegime.AgeCandles, Is.GreaterThanOrEqualTo(0));
        });
    }
}
