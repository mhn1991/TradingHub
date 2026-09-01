using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.Alfonso;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// The cost-to-risk gate, the ATR-percentile regime veto, and decision-time candidate logging.
/// <para>
/// These attack the binding constraint rather than searching for signal. A fixed 3:1 needs a 28.2%
/// win rate to clear costs at M15 on gold and the method delivers 19.5%; measured cost drag is 12.6%
/// of R at the median zone and 30.5% at the tightest decile, so the worst trades are unprofitable
/// before price moves. Nothing here creates a zone or changes a stop or target.
/// </para>
/// </summary>
[TestFixture]
public sealed class AlfonsoCostAndRegimeGateTests
{
    private static readonly InstrumentKey Instrument = new("METAL:XAU/USD");
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void EveryNewGateIsOffByDefaultSoBehaviourIsUnchanged()
    {
        AlfonsoStrategyOptions defaults = new();

        Assert.That(defaults.MaximumCostToRiskFraction, Is.Zero);
        Assert.That(defaults.MinimumStopAtrMultiple, Is.Zero);
        Assert.That(defaults.MinimumAtrPercentile, Is.Zero);
        Assert.That(defaults.MaximumAtrPercentile, Is.EqualTo(1m));
        Assert.DoesNotThrow(defaults.Validate);
    }

    [Test]
    public void NonsensicalGateSettingsAreRejectedAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(
            () => new AlfonsoStrategyOptions { MaximumCostToRiskFraction = 1m }.Validate());
        Assert.Throws<InvalidOperationException>(
            () => new AlfonsoStrategyOptions { MinimumStopAtrMultiple = -1m }.Validate());
        // An inverted band would refuse everything, silently.
        Assert.Throws<InvalidOperationException>(
            () => new AlfonsoStrategyOptions
            {
                MinimumAtrPercentile = 0.8m,
                MaximumAtrPercentile = 0.2m
            }.Validate());
        Assert.Throws<InvalidOperationException>(
            () => new AlfonsoStrategyOptions { AtrPercentileLookback = 1 }.Validate());
    }

    [Test]
    public async Task TheCandidateSinkIsSilentWhenTheAgentNeverReachesACandidate()
    {
        // A sink that fires when nothing was considered would corrupt any study built on it.
        List<AlfonsoCandidateRecord> seen = [];
        AlfonsoAgent agent = new(new AlfonsoStrategyOptions(), seen.Add);

        AgentDecision decision = await agent.EvaluateAsync(Context(
            Snapshot(BarInterval.Hours(4), 2000m),
            Snapshot(BarInterval.Hours(1), 2000m),
            Snapshot(BarInterval.Minutes(15), 2000m)));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
        Assert.That(seen, Is.Empty);
    }

    [Test]
    public void ANullSinkIsTheDefaultAndCostsNothing()
    {
        Assert.DoesNotThrow(() => new AlfonsoAgent());
        Assert.DoesNotThrow(() => new AlfonsoAgent(new AlfonsoStrategyOptions(), candidateSink: null));
    }

    /// <summary>
    /// The arithmetic the cost gate rests on, pinned directly: a zone whose stop is tight relative
    /// to the instrument's round-trip cost loses a large share of its R before price moves.
    /// </summary>
    [Test]
    public void CostToRiskIsTheRatioTheGateCompares()
    {
        // Gold at 4,560 with a 2.4bp round trip costs about $1.09 to trade.
        decimal cost = 4560m * 2.4m / 10_000m;

        // A median M15 zone: 6.93 wide, padded 25%, so 8.66 of risk.
        decimal medianRisk = 6.93m * 1.25m;
        Assert.That(cost / medianRisk, Is.EqualTo(0.126m).Within(0.005m));

        // The tightest decile: 2.87 wide, 3.59 of risk - nearly a third gone to costs.
        decimal tightRisk = 2.87m * 1.25m;
        Assert.That(cost / tightRisk, Is.EqualTo(0.305m).Within(0.01m));

        // A 15% ceiling keeps the median and refuses the tightest decile.
        const decimal ceiling = 0.15m;
        Assert.That(cost / medianRisk, Is.LessThan(ceiling));
        Assert.That(cost / tightRisk, Is.GreaterThan(ceiling));
    }

    private static AgentMarketContext Context(params AnalysisSnapshot[] snapshots) => new()
    {
        Instrument = Instrument,
        Timestamp = Now,
        Analysis = new MultiTimeframeAnalysis(
            Instrument, Now, snapshots.ToDictionary(snapshot => snapshot.Interval)),
        Account = new AccountSnapshot { AccountId = "account", CanTrade = true },
        Positions = [],
        OpenOrders = [],
        RoundTripCostEstimate = 1.09m
    };

    private static AnalysisSnapshot Snapshot(BarInterval interval, decimal price) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument, Now.AddMinutes(-15), interval, price, price + 1m, price - 1m, price + 0.5m),
        Indicators = new IndicatorSnapshot { Atr = 2m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
