using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Covers <see cref="RsiAnalysisSnapshot.PreviousValue"/> - the raw one-bar predecessor, added to
/// mirror <see cref="CciAnalysisSnapshot.PreviousValue"/>. Distinct from
/// <see cref="RsiAnalysisSnapshot.MomentumChange"/>, which spans a configurable lookback
/// (default 3 candles) and applies a dead-band, so it cannot answer "did RSI move since the
/// last candle".
/// </summary>
[TestFixture]
public sealed class RsiPreviousValueTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static Candle Bar(int index) => TestCandles.Create(
        Instrument, Start.AddMinutes(5 * index), Interval, 100m, 101m, 99m, 100.5m);

    [Test]
    public void FirstSample_HasNoPreviousValue()
    {
        var state = new RsiAnalysisState();

        RsiAnalysisSnapshot snapshot = state.Update(Bar(0), 50m, [], atr: 1m);

        Assert.That(snapshot.PreviousValue, Is.Null);
    }

    [Test]
    public void SecondSample_ReportsTheImmediatelyPrecedingRsi()
    {
        var state = new RsiAnalysisState();
        state.Update(Bar(0), 50m, [], atr: 1m);

        RsiAnalysisSnapshot snapshot = state.Update(Bar(1), 55m, [], atr: 1m);

        Assert.That(snapshot.PreviousValue, Is.EqualTo(50m));
    }

    [Test]
    public void PreviousValue_TracksOneBarBack_NotTheMomentumLookback()
    {
        // The distinction that motivated the field: with the default 3-bar lookback,
        // MomentumChange still spans three candles while PreviousValue spans exactly one.
        var state = new RsiAnalysisState();
        decimal[] series = [40m, 45m, 50m, 56m];
        RsiAnalysisSnapshot snapshot = RsiAnalysisSnapshot.Empty;
        for (int i = 0; i < series.Length; i++)
            snapshot = state.Update(Bar(i), series[i], [], atr: 1m);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.PreviousValue, Is.EqualTo(50m), "one bar back");
            Assert.That(snapshot.MomentumChange, Is.EqualTo(16m), "three bars back (56 - 40)");
        });
    }

    [Test]
    public void PreviousValue_SupportsATickUpTestThatMomentumCannot()
    {
        // A 0.5-point rise sits inside the default +/-1 dead-band, so MomentumDirection reports
        // Stable. PreviousValue still exposes the move - the case this field exists for.
        var state = new RsiAnalysisState();
        foreach (int i in new[] { 0, 1, 2 })
            state.Update(Bar(i), 50m, [], atr: 1m);

        RsiAnalysisSnapshot snapshot = state.Update(Bar(3), 50.5m, [], atr: 1m);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.MomentumDirection, Is.EqualTo(MomentumDirection.Stable));
            Assert.That(snapshot.PreviousValue, Is.EqualTo(50m));
            Assert.That(snapshot.PreviousValue, Is.LessThan(50.5m), "current is above previous");
        });
    }

    [Test]
    public void WarmupBarWithoutRsi_LeavesPreviousValueNull()
    {
        var state = new RsiAnalysisState();
        state.Update(Bar(0), 50m, [], atr: 1m);

        RsiAnalysisSnapshot snapshot = state.Update(Bar(1), rsi: null, [], atr: 1m);

        Assert.That(snapshot.PreviousValue, Is.Null, "a bar with no RSI reports no comparison");
    }

    [Test]
    public void CciExposesTheSameField_SoBothIndicatorsReadAlike()
    {
        var cci = new CciAnalysisState();
        cci.Update(Bar(0), 10m, [], atr: 1m);

        CciAnalysisSnapshot snapshot = cci.Update(Bar(1), 25m, [], atr: 1m);

        Assert.That(snapshot.PreviousValue, Is.EqualTo(10m));
    }
}
