using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoTrendAuditTests
{
    [TestCase(15, SequenceRole.Lower)]
    [TestCase(60, SequenceRole.Middle)]
    [TestCase(240, SequenceRole.Top)]
    public void EveryArmMatchesAnIndependentAnalyzerWithoutMutatingBaseline(int minutes, SequenceRole role)
    {
        AlfonsoStrategyOptions options = new();
        List<AlfonsoTrendAuditRow> rows = [];
        AlfonsoTrendAudit audit = new(options, rows.Add);
        TimeSpan interval = TimeSpan.FromMinutes(minutes);
        AlfonsoTimeframeAnalyzer baseline = new(interval);
        AlfonsoTimeframeAnalyzer[] independent = Enumerable.Range(0, 4).Select(arm =>
            new AlfonsoTimeframeAnalyzer(interval, options.Zones, options.Trend with
            {
                InvalidateOnPriceStructureBreak = arm is 1 or 3,
                RequireConfirmedTrendStructure = arm is 2 or 3
            }, options.Range)).ToArray();
        DateTimeOffset start = new(2025, 11, 24, 0, 0, 0, TimeSpan.Zero);
        Random random = new(71);
        decimal close = 100m;
        for (int i = 0; i < 1000; i++)
        {
            decimal open = close;
            close += (decimal)(random.NextDouble() - 0.5) * 6m;
            AlfonsoBar bar = new(start + interval * i, open,
                Math.Max(open, close) + (decimal)random.NextDouble(),
                Math.Min(open, close) - (decimal)random.NextDouble(), close);
            baseline.Apply(bar);
            AlfonsoTrendSnapshot before = baseline.Trend;
            foreach (var analyzer in independent) analyzer.Apply(bar);
            audit.Observe("test", bar.OpenTime + interval, role, bar, baseline);
            Assert.That(baseline.Trend, Is.EqualTo(before));
            for (int arm = 0; arm < 4; arm++)
            {
                Assert.That(rows[^1].States[arm].Trend, Is.EqualTo(independent[arm].Trend.Trend.ToString()));
                Assert.That(rows[^1].States[arm].Reason, Is.EqualTo(independent[arm].Trend.Reason));
            }
        }
        Assert.That(rows, Has.Count.EqualTo(1000));
        Assert.That(rows.All(row => row.AvailableAt == row.OpenTime + interval), Is.True);
        Assert.That(rows.SelectMany(row => row.States).Any(state => state.Trend != "Unknown"), Is.True);
    }

    [Test]
    public void RejectsMislabeledBaselineAndMapsRequestOption()
    {
        Assert.Throws<ArgumentException>(() => new AlfonsoTrendAudit(new()
            { Trend = new() { InvalidateOnPriceStructureBreak = true } }, _ => { }));
        var definition = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAU/USD"),
            From = DateTimeOffset.UtcNow, To = DateTimeOffset.UtcNow.AddDays(1),
            AlfonsoTrendAuditPath = "/tmp/audit.jsonl"
        }.ResolveAgentDefinition("alfonso");
        Assert.That(definition.Alfonso!.TrendAuditPath, Is.EqualTo("/tmp/audit.jsonl"));
        Assert.That(new AlfonsoStrategyOptions().TrendAuditPath, Is.Null);
    }
}
