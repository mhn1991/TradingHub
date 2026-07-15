using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class EfficiencyRatioAnalysisStateTests
{
    [Test]
    public void NotReady_ReturnsEmptySnapshot()
    {
        var analysis = new EfficiencyRatioAnalysisState(
            historyPeriod: 10,
            changeLookback: 1,
            minimumSamples: 5);

        EfficiencyAnalysisSnapshot snapshot = analysis.Update(0.5m, isReady: false);

        Assert.That(snapshot, Is.EqualTo(EfficiencyAnalysisSnapshot.Empty));
    }

    [Test]
    public void BeforeMinimumSamples_UsesAbsoluteFallbackThresholds()
    {
        var analysis = new EfficiencyRatioAnalysisState(
            historyPeriod: 20,
            changeLookback: 1,
            minimumSamples: 10,
            highlyChoppyMaximum: 0.20m,
            choppyMaximum: 0.40m,
            transitionalMaximum: 0.60m,
            efficientMaximum: 0.80m);

        EfficiencyAnalysisSnapshot choppy = analysis.Update(0.10m, isReady: true);
        Assert.Multiple(() =>
        {
            Assert.That(choppy.State, Is.EqualTo(MarketEfficiencyState.HighlyChoppy));
            Assert.That(choppy.Percentile, Is.Null);
        });

        EfficiencyAnalysisSnapshot transitional = analysis.Update(0.50m, isReady: true);
        Assert.That(transitional.State, Is.EqualTo(MarketEfficiencyState.Transitional));

        EfficiencyAnalysisSnapshot highlyEfficient = analysis.Update(0.95m, isReady: true);
        Assert.That(highlyEfficient.State, Is.EqualTo(MarketEfficiencyState.HighlyEfficient));
    }

    [Test]
    public void AfterMinimumSamples_SwitchesToPercentileClassification()
    {
        var analysis = new EfficiencyRatioAnalysisState(
            historyPeriod: 10,
            changeLookback: 1,
            minimumSamples: 10,
            choppyPercentile: 25m,
            transitionalPercentile: 50m,
            efficientPercentile: 75m,
            highlyEfficientPercentile: 90m);

        // Seed history with low values so a high value ranks at the top percentile.
        EfficiencyAnalysisSnapshot snapshot = EfficiencyAnalysisSnapshot.Empty;
        decimal[] seedValues = [0.10m, 0.12m, 0.11m, 0.13m, 0.10m, 0.14m, 0.11m, 0.12m, 0.13m];
        foreach (decimal value in seedValues)
        {
            snapshot = analysis.Update(value, isReady: true);
            Assert.That(snapshot.Percentile, Is.Null, "Should still be absolute-classified below minimum samples.");
        }

        snapshot = analysis.Update(0.95m, isReady: true);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Percentile, Is.Not.Null);
            Assert.That(snapshot.Percentile, Is.GreaterThan(90m));
            Assert.That(snapshot.State, Is.EqualTo(MarketEfficiencyState.HighlyEfficient));
            Assert.That(snapshot.SampleCount, Is.EqualTo(10));
        });
    }

    [Test]
    public void Direction_DetectsRisingAndFalling()
    {
        var analysis = new EfficiencyRatioAnalysisState(
            historyPeriod: 10,
            changeLookback: 1,
            minimumSamples: 10,
            directionThresholdPercent: 5m);

        // changeLookback compares against history[Count-1-lookback] captured before
        // the current value is appended, so two seed calls are needed before the
        // comparison window has enough depth to produce a direction.
        analysis.Update(0.40m, isReady: true);
        analysis.Update(0.50m, isReady: true);
        EfficiencyAnalysisSnapshot rising = analysis.Update(0.60m, isReady: true);
        Assert.That(rising.Direction, Is.EqualTo(MomentumDirection.Rising));

        EfficiencyAnalysisSnapshot falling = analysis.Update(0.30m, isReady: true);
        Assert.That(falling.Direction, Is.EqualTo(MomentumDirection.Falling));
    }
}
