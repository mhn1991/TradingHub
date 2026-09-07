using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoReversalShadowTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 5, 4, 0, 0, TimeSpan.Zero);
    private static AlfonsoBar Bar(int n, decimal high, decimal low, decimal close, bool buy = true) => buy
        ? new(Start.AddMinutes(5 * n), close, high, low, close)
        : new(Start.AddMinutes(5 * n), 200 - close, 200 - low, 200 - high, 200 - close);
    private static void Feed(AlfonsoReversalShadow detector, AlfonsoBar bar) => detector.Apply(bar, bar.OpenTime.AddMinutes(5));
    private static void Seed(AlfonsoReversalShadow detector, bool buy = true)
    {
        Feed(detector, Bar(0, 105, 99, 100, buy));
        Feed(detector, Bar(1, 106, 98, 100, buy));
        Feed(detector, Bar(2, 110, 97, 100, buy));
        Feed(detector, Bar(3, 106, 98, 100, buy));
        Feed(detector, Bar(4, 105, 99, 100, buy));
        Feed(detector, Bar(5, 104, 96, 98, buy));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RequiresOrderedClosedBarsThenMeasuresOnlyFutureBars(bool buy)
    {
        List<AlfonsoReversalObservation> observations = [];
        var detector = new AlfonsoReversalShadow(observations.Add);
        Seed(detector, buy);
        string side = buy ? "Buy" : "Sell";
        Assert.That(observations.Select(x => x.Stage), Is.EqualTo(new[] { "Sweep", "Reclaimed" }));
        var breakout = Bar(6, 112, 100, 111, buy);
        detector.Apply(breakout, breakout.OpenTime.AddMinutes(4));
        Assert.That(observations, Has.Count.EqualTo(2));
        Feed(detector, breakout);
        Feed(detector, breakout);
        Assert.That(observations.Last(x => x.Side == side).Stage, Is.EqualTo("StructureBreak"), "No same-bar holding retest.");
        Feed(detector, Bar(7, 112, 109, 111, buy));
        Assert.That(observations.Last(x => x.Side == side).Stage, Is.EqualTo("Confirmed"));
        Feed(detector, Bar(8, 127, 110, 120, buy));
        for (int i = 9; i <= 19; i++) Feed(detector, Bar(i, 125, 115, 120, buy));
        var result = observations.Single(x => x.Stage == "Outcome");
        Assert.That(result.Result, Is.EqualTo("OneRFirst"));
        Assert.That(result.MfeR, Is.EqualTo(16m / 15m));
        Assert.That(result.At, Is.EqualTo(Start.AddMinutes(100)));
        Assert.That(result.SwingAt, Is.LessThan(result.SweepAt));
    }

    [TestCase(false, "Invalidated")]
    [TestCase(true, "Expired")]
    public void UnfinishedSetupsCannotRemainValidIndefinitely(bool expire, string stage)
    {
        List<AlfonsoReversalObservation> observations = [];
        var detector = new AlfonsoReversalShadow(observations.Add);
        Seed(detector);
        if (expire)
            for (int i = 6; i <= 18; i++) Feed(detector, Bar(i, 108, 100, 101));
        else Feed(detector, Bar(6, 104, 95, 98));
        Assert.That(observations.Last().Stage, Is.EqualTo(stage));
        Assert.That(observations.Any(x => x.Stage == "Confirmed"), Is.False);
    }

    [TestCase(128, 95, "AmbiguousSameBar")]
    [TestCase(112, 95, "StopFirst")]
    [TestCase(112, 109, "NeitherWithin12Bars")]
    public void ShadowLabelsDoNotAssumeIntrabarPath(decimal high, decimal low, string expected)
    {
        List<AlfonsoReversalObservation> observations = [];
        var detector = new AlfonsoReversalShadow(observations.Add);
        Seed(detector);
        Feed(detector, Bar(6, 112, 100, 111));
        Feed(detector, Bar(7, 112, 109, 111));
        Feed(detector, Bar(8, high, low, 111));
        for (int i = 9; i <= 19; i++) Feed(detector, Bar(i, 112, 109, 111));
        Assert.That(observations.Single(x => x.Stage == "Outcome").Result, Is.EqualTo(expected));
    }

    [Test]
    public void FailedRetestCannotBecomeALaterSignal()
    {
        List<AlfonsoReversalObservation> observations = [];
        var detector = new AlfonsoReversalShadow(observations.Add);
        Seed(detector);
        Feed(detector, Bar(6, 112, 100, 111));
        Feed(detector, Bar(7, 112, 108, 109));
        Feed(detector, Bar(8, 112, 109, 111));
        Assert.That(observations.Any(x => x.Stage == "RetestFailed"), Is.True);
        Assert.That(observations.Any(x => x.Stage == "Confirmed"), Is.False);
    }

    [Test]
    public void ShadowConfigurationIsOptInAndRequiresFiveMinutePolicy()
    {
        var request = new BacktestRequest { Instrument = new InstrumentKey("METAL:XAG/USD"), From = Start,
            To = Start.AddDays(1), AlfonsoEntryPolicy = AlfonsoEntryPolicy.LowerTimeframeAligned,
            AlfonsoReversalShadowLogPath = "/tmp/shadow-options-test.ndjson" };
        var options = request.ResolveAgentDefinition("alfonso").Alfonso!;
        Assert.That(options.ReversalShadowLogPath, Is.EqualTo(request.AlfonsoReversalShadowLogPath));
        Assert.That(new AlfonsoStrategyOptions().ReversalShadowLogPath, Is.Null);
        Assert.Throws<InvalidOperationException>(() => new AlfonsoStrategyOptions { ReversalShadowLogPath = "test" }.Validate());
    }
}
