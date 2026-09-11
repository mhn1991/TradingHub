using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoLowerAlignmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly InstrumentKey Instrument = new("METAL:XAG/USD");
    private static readonly AlfonsoStrategyOptions Options = new()
    {
        EntryPolicy = AlfonsoEntryPolicy.LowerTimeframeAligned
    };

    [Test]
    public void OnlyMatchingDirectionalLowerTrendsPermitEntriesRegardlessOfHigherTrends()
    {
        foreach (AlfonsoTrend top in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend middle in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend lower in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend? five in Enum.GetValues<AlfonsoTrend>().Select(x => (AlfonsoTrend?)x).Append(null))
        {
            var scenario = ScenarioMatrix.Resolve(top, middle, lower, Options.EntryPolicy, five);
            bool permitted = lower is AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend && five == lower;
            Assert.That(scenario.CanTrade, Is.EqualTo(permitted), $"{top}/{middle}/{lower}/{five}");
            if (!permitted)
            {
                Assert.That(scenario.Entries, Is.Empty);
                continue;
            }
            Assert.That(scenario.Side, Is.EqualTo(lower == AlfonsoTrend.Uptrend ? ImbalanceKind.Demand : ImbalanceKind.Supply));
            Assert.That(scenario.Entries, Has.Count.EqualTo(1));
            Assert.That(scenario.Entries[0].EntryTimeframe, Is.EqualTo(SequenceRole.Lower));
            Assert.That(scenario.Entries[0].NestedIn, Is.Null);
            Assert.That(scenario.Reason, Does.Contain($"15m {lower}, 5m {five}"));
        }
    }

    [Test]
    public void ConfigurationAddsFiveMinuteIngestionWithoutMovingEntryTimeframeOrChangingDefaults()
    {
        var request = new BacktestRequest
        {
            Instrument = Instrument, From = Now, To = Now.AddDays(1), Strategies = ["alfonso"],
            AlfonsoEntryPolicy = Options.EntryPolicy
        };
        var selected = request.ResolveAgentDefinition("alfonso").Alfonso!;
        var agent = new AlfonsoAgent(selected);
        Assert.That(agent.TriggerInterval, Is.EqualTo(BarInterval.Minutes(5)));
        Assert.That(agent.RequiredIntervals, Is.EquivalentTo(new[]
            { BarInterval.Hours(4), BarInterval.Hours(1), BarInterval.Minutes(15), BarInterval.Minutes(5) }));
        Assert.That(selected.LowerInterval, Is.EqualTo(BarInterval.Minutes(15)));
        Assert.That(new AlfonsoAgent().TriggerInterval, Is.EqualTo(BarInterval.Minutes(15)));
        Assert.That(new AlfonsoStrategyOptions().ConfirmationInterval, Is.Null);
        Assert.Throws<InvalidOperationException>(() => (Options with { LowerInterval = BarInterval.Minutes(10) }).Validate());
        Assert.Throws<ArgumentException>(() => new AlfonsoSequenceAnalyzer(
            TimeframeSequence.Daily, entryPolicy: Options.EntryPolicy));
    }

    [Test]
    public void ConfirmationConsumesEveryClosedFiveMinuteBarOnceAndDoesNotAdvanceFifteenMinuteTrend()
    {
        var analyzer = new AlfonsoSequenceAnalyzer(Options.Sequence, entryPolicy: Options.EntryPolicy);
        var independent = new AlfonsoTimeframeAnalyzer(TimeSpan.FromMinutes(5));
        var future = new AlfonsoBar(Now, 100, 102, 99, 101);
        Assert.That(analyzer.ApplyConfirmation(future, Now.AddMinutes(4)), Is.False);
        Assert.That(analyzer.ConfirmationClosedAt, Is.Null);
        for (int i = 0; i < 60; i++)
        {
            decimal price = 100 + i % 7;
            var bar = new AlfonsoBar(Now.AddMinutes(5 * i), price, price + 2, price - 1, price + 1);
            var close = bar.OpenTime.AddMinutes(5);
            independent.Apply(bar);
            Assert.That(analyzer.ApplyConfirmation(bar, close), Is.True);
            Assert.That(analyzer.ApplyConfirmation(bar, close), Is.False);
            Assert.That(analyzer.ConfirmationClosedAt, Is.EqualTo(close));
            Assert.That(analyzer.ConfirmationTrend, Is.EqualTo(independent.Trend.Trend));
            Assert.That(analyzer.TrendOf(SequenceRole.Lower), Is.EqualTo(AlfonsoTrend.Unknown));
        }
        Assert.That(analyzer.ApplyConfirmation(future, Now.AddDays(1)), Is.False, "Old bars cannot rewind the detector.");
        Assert.That(new AlfonsoSequenceAnalyzer(Options.Sequence).ApplyConfirmation(future, Now.AddMinutes(5)), Is.False);
    }

    [Test]
    public async Task AgentIngestsFiveMinuteUpdatesButDoesNotReevaluateOrdersBetweenFifteenMinuteCloses()
    {
        var agent = new AlfonsoAgent(Options);
        var first = Context(Now, Now.AddMinutes(-5));
        await agent.EvaluateAsync(first);
        var next = await agent.EvaluateAsync(Context(Now.AddMinutes(5), Now));
        Assert.That(next.Action, Is.EqualTo(AgentAction.Observe));
        Assert.That(next.Reason, Does.Contain("waiting for a closed 15m"));
        var fresh = await agent.EvaluateAsync(Context(Now.AddMinutes(15), Now.AddMinutes(10), Now));
        Assert.That(fresh.Reason, Does.Not.Contain("already been evaluated"));
    }

    [TestCase(-10)]
    [TestCase(0)]
    public async Task StaleOrUnclosedConfirmationCannotAdmitAnOrder(int minutes)
    {
        var decision = await new AlfonsoAgent(Options).EvaluateAsync(Context(Now, Now.AddMinutes(minutes)));
        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
        Assert.That(decision.Reason, Does.Contain("current closed 5m"));
    }

    internal static AgentMarketContext Context(DateTimeOffset at, DateTimeOffset fiveOpen, DateTimeOffset? lowerOpen = null,
        IndicatorSnapshot? fiveIndicators = null)
    {
        AnalysisSnapshot[] snapshots = [Snapshot(BarInterval.Hours(4), Now.AddHours(-4)),
            Snapshot(BarInterval.Hours(1), Now.AddHours(-1)),
            Snapshot(BarInterval.Minutes(15), lowerOpen ?? Now.AddMinutes(-15)),
            Snapshot(BarInterval.Minutes(5), fiveOpen) with { Indicators = fiveIndicators ?? new IndicatorSnapshot { Atr = 2m } }];
        return new AgentMarketContext
        {
            Instrument = Instrument, Timestamp = at,
            Analysis = new MultiTimeframeAnalysis(Instrument, at, snapshots.ToDictionary(x => x.Interval)),
            Account = new AccountSnapshot { AccountId = "test", CanTrade = true }, Positions = [], OpenOrders = []
        };
    }

    private static AnalysisSnapshot Snapshot(BarInterval interval, DateTimeOffset open) => new()
    {
        Instrument = Instrument, Interval = interval, AvailableAt = open + AlfonsoStrategyOptions.ToTimeSpan(interval), Version = 1,
        LatestCandle = TestCandles.Create(Instrument, open, interval, 100, 102, 99, 101),
        Indicators = new IndicatorSnapshot { Atr = 2m }, Swings = [], PriceZones = [], Trendlines = [], Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
