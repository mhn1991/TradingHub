using Agent.Strategies.Alfonso;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoRewardTargetTests
{
    [TestCase(false, 0.75)]
    [TestCase(false, 1)]
    [TestCase(true, 0.75)]
    [TestCase(true, 1)]
    public void TargetUsesActualStopDistanceAndEntryReference(bool buy, decimal reward)
    {
        decimal stop = buy ? 90m : 110m;
        Assert.That(AlfonsoAgent.CalculateTarget(buy, 100m, stop, reward),
            Is.EqualTo(buy ? 100m + 10m * reward : 100m - 10m * reward));
        // A confirmation entry at a different close must not reuse the limit entry's risk.
        Assert.That(AlfonsoAgent.CalculateTarget(buy, 101m, stop, reward),
            Is.EqualTo(buy ? 101m + 11m * reward : 101m - 9m * reward));
    }

    [Test]
    public void SilverExampleHasTheExpectedFractionalTargets()
    {
        Assert.That(AlfonsoAgent.CalculateTarget(false, 65.99550m, 66.5583625m, 0.75m),
            Is.EqualTo(65.573353125m));
        Assert.That(AlfonsoAgent.CalculateTarget(false, 65.99550m, 66.5583625m, 1m),
            Is.EqualTo(65.4326375m));
    }

    [TestCase(0.75)]
    [TestCase(1)]
    public void RewardIsConfigurableIndependentlyOfStopPolicy(decimal reward)
    {
        var start = new DateTimeOffset(2025, 12, 17, 0, 0, 0, TimeSpan.Zero);
        var request = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAG/USD"), From = start, To = start.AddDays(1),
            AlfonsoRewardMultiple = reward
        };
        foreach (bool structural in new[] { false, true })
        {
            var options = (request with { AlfonsoUseStructuralSwingStop = structural })
                .ResolveAgentDefinition("alfonso").Alfonso!;
            options.Validate();
            Assert.That(options.Zones.RewardMultiple, Is.EqualTo(reward));
            Assert.That(options.UseStructuralSwingStop, Is.EqualTo(structural));
        }
    }
}
