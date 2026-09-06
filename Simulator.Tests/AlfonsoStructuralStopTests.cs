using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoStructuralStopTests
{
    private static readonly DateTimeOffset Start = new(2025, 12, 17, 5, 15, 0, TimeSpan.Zero);

    private static AlfonsoBar Bar(int index, decimal high, decimal low = 60m) =>
        new(Start.AddMinutes(index * 15), (high + low) / 2m, high, low, (high + low) / 2m);

    [Test]
    public void PivotIsUnavailableUntilBothRightHandCandlesClose()
    {
        var history = new AlfonsoStructuralStop(48);
        foreach (int i in Enumerable.Range(0, 4))
            history.Apply(Bar(i, i == 2 ? 66.53510m : 66m));
        Assert.That(history.Resolve(false, 66.08855m, 0.0232625m).Anchor, Is.Null);
        history.Apply(Bar(4, 66m));
        var result = history.Resolve(false, 66.08855m, 0.0232625m);
        Assert.Multiple(() =>
        {
            Assert.That(result.Anchor!.Value.OpenTime, Is.EqualTo(Start.AddMinutes(30)));
            Assert.That(result.Stop, Is.EqualTo(66.5583625m));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExtremeSwingWinsOverTheLatestMinorSwing(bool buy)
    {
        var history = new AlfonsoStructuralStop(48);
        decimal[] highs = [65m, 66m, 70m, 66m, 65m, 66m, 68m, 66m, 65m];
        for (int i = 0; i < highs.Length; i++)
            history.Apply(buy ? Bar(i, 80m, 140m - highs[i]) : Bar(i, highs[i]));
        var result = history.Resolve(buy, buy ? 75m : 65m, 0.25m);
        Assert.That(result.Anchor!.Value.OpenTime, Is.EqualTo(Start.AddMinutes(30)));
        Assert.That(result.Stop, Is.EqualTo(buy ? 69.75m : 70.25m));
    }

    [Test]
    public void AnchorExpiresAndDuplicateOrOldBarsCannotConfirmAPivot()
    {
        var history = new AlfonsoStructuralStop(5);
        for (int i = 0; i < 4; i++)
            history.Apply(Bar(i, i == 2 ? 70m : 66m));
        history.Apply(Bar(3, 66m));
        history.Apply(Bar(1, 66m));
        Assert.That(history.Resolve(false, 65m, 0.25m).Anchor, Is.Null);
        history.Apply(Bar(4, 66m));
        history.Apply(Bar(5, 66m));
        history.Apply(Bar(6, 66m));
        Assert.That(history.Resolve(false, 65m, 0.25m).Stop, Is.EqualTo(70.25m));
        history.Apply(Bar(7, 66m));
        var expired = history.Resolve(false, 65m, 0.25m);
        Assert.That(expired.Anchor, Is.Null);
        Assert.That(expired.Stop, Is.EqualTo(65.25m));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StructuralStopNeverNarrowsZoneProtection(bool buy)
    {
        var history = new AlfonsoStructuralStop(48);
        for (int i = 0; i < 5; i++)
            history.Apply(Bar(i, i == 2 ? 70m : 66m, i == 2 ? 50m : 60m));
        Assert.That(history.Resolve(buy, buy ? 40m : 80m, 0.25m).Stop,
            Is.EqualTo(buy ? 39.75m : 80.25m));
    }

    [Test]
    public void FlatHighsAndLowsAreNotStrictPivots()
    {
        var history = new AlfonsoStructuralStop(48);
        for (int i = 0; i < 8; i++)
            history.Apply(Bar(i, 70m));
        Assert.That(history.Resolve(false, 65m, 0m).Anchor, Is.Null);
        Assert.That(history.Resolve(true, 65m, 0m).Anchor, Is.Null);
    }

    [Test]
    public void RequestWiresOptInAndPreservesExistingDefaults()
    {
        var request = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAG/USD"), From = Start, To = Start.AddDays(1),
            AlfonsoUseStructuralSwingStop = true, AlfonsoStructuralStopLookbackCandles = 32
        };
        AlfonsoStrategyOptions selected = request.ResolveAgentDefinition("alfonso").Alfonso!;
        Assert.Multiple(() =>
        {
            Assert.That(selected.UseStructuralSwingStop, Is.True);
            Assert.That(selected.StructuralStopLookbackCandles, Is.EqualTo(32));
            Assert.That(selected.LowerInterval, Is.EqualTo(BarInterval.Minutes(15)));
            Assert.That(new AlfonsoStrategyOptions().UseStructuralSwingStop, Is.False);
            Assert.That(new AlfonsoStrategyOptions().StructuralStopLookbackCandles, Is.EqualTo(48));
            Assert.That(new AlfonsoStrategyOptions().Zones.RewardMultiple, Is.EqualTo(3m));
        });
    }

    [TestCase(4)]
    [TestCase(10001)]
    public void InvalidLookbackIsRejected(int lookback) =>
        Assert.Throws<InvalidOperationException>(() => new AlfonsoStrategyOptions
        {
            StructuralStopLookbackCandles = lookback
        }.Validate());
}
