using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoEntryPolicyTests
{
    private static IEnumerable<TestCaseData> Alignments()
    {
        foreach (AlfonsoTrend top in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend middle in Enum.GetValues<AlfonsoTrend>())
        foreach (AlfonsoTrend lower in Enum.GetValues<AlfonsoTrend>())
            yield return new TestCaseData(top, middle, lower);
    }

    [TestCaseSource(nameof(Alignments))]
    public void ReversalPolicyOnlyPermitsTheTwoExplicitOpposingAlignments(
        AlfonsoTrend top, AlfonsoTrend middle, AlfonsoTrend lower)
    {
        ScenarioResolution scenario = ScenarioMatrix.Resolve(top, middle, lower,
            AlfonsoEntryPolicy.LowerTimeframeReversal);
        bool buy = top == AlfonsoTrend.Downtrend && middle == AlfonsoTrend.Uptrend && lower == AlfonsoTrend.Uptrend;
        bool sell = top == AlfonsoTrend.Uptrend && middle == AlfonsoTrend.Downtrend && lower == AlfonsoTrend.Downtrend;
        Assert.That(scenario.CanTrade, Is.EqualTo(buy || sell));
        Assert.That(scenario.Reason, Does.Contain("Experimental lower-timeframe reversal"));
        if (!scenario.CanTrade)
        {
            Assert.That(scenario.Entries, Is.Empty);
            return;
        }

        Assert.That(scenario.Side, Is.EqualTo(buy ? ImbalanceKind.Demand : ImbalanceKind.Supply));
        Assert.That(scenario.Entries, Has.Count.EqualTo(1));
        Assert.That(scenario.Entries[0].EntryTimeframe, Is.EqualTo(SequenceRole.Lower));
        Assert.That(scenario.Reason, Does.Contain("confirmation required"));
    }

    [TestCaseSource(nameof(Alignments))]
    public void ExplicitCorePreservesEveryDefaultMatrixOutcome(
        AlfonsoTrend top, AlfonsoTrend middle, AlfonsoTrend lower)
    {
        ScenarioResolution baseline = ScenarioMatrix.Resolve(top, middle, lower);
        ScenarioResolution explicitCore = ScenarioMatrix.Resolve(top, middle, lower, AlfonsoEntryPolicy.Core);
        Assert.Multiple(() =>
        {
            Assert.That(explicitCore.CanTrade, Is.EqualTo(baseline.CanTrade));
            Assert.That(explicitCore.Side, Is.EqualTo(baseline.Side));
            Assert.That(explicitCore.Reason, Is.EqualTo(baseline.Reason));
            Assert.That(explicitCore.Entries, Is.EqualTo(baseline.Entries));
        });
    }

    [Test]
    public void ReversalWithoutEntryConfirmationIsRefusedAtBothPublicEntryPoints()
    {
        var options = new AlfonsoStrategyOptions { EntryPolicy = AlfonsoEntryPolicy.LowerTimeframeReversal };
        Assert.Throws<InvalidOperationException>(() => new AlfonsoAgent(options));
        Assert.Throws<ArgumentException>(() => new AlfonsoSequenceAnalyzer(options.Sequence,
            entryPolicy: AlfonsoEntryPolicy.LowerTimeframeReversal));
        Assert.DoesNotThrow(() => new AlfonsoAgent(options with { RequireReversalConfirmation = true }));
    }

    [Test]
    public void InvalidPolicyCannotFallBackToCore()
    {
        Assert.Throws<InvalidOperationException>(() => new AlfonsoStrategyOptions
        {
            EntryPolicy = (AlfonsoEntryPolicy)999
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => ScenarioMatrix.Resolve(
            AlfonsoTrend.Uptrend, AlfonsoTrend.Uptrend, AlfonsoTrend.Uptrend, (AlfonsoEntryPolicy)999));
    }

    [Test]
    public void AnalyzerUsesTheSelectedPolicyInItsReasons()
    {
        var analyzer = new AlfonsoSequenceAnalyzer(new AlfonsoStrategyOptions().Sequence,
            confirmationEntryMode: true, entryPolicy: AlfonsoEntryPolicy.LowerTimeframeReversal);
        Assert.That(analyzer.Scenario.CanTrade, Is.False);
        Assert.That(analyzer.Scenario.Reason, Does.StartWith("Experimental lower-timeframe reversal"));
        Assert.That(analyzer.Candidates(100m), Is.Empty);
    }

    [Test]
    public void ConfigurationWiresPolicyAndRetainsBaselineTimeframesAndProtection()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var request = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAU/USD"), From = start, To = start.AddDays(1),
            AlfonsoEntryPolicy = AlfonsoEntryPolicy.LowerTimeframeReversal,
            AlfonsoRequireReversalConfirmation = true
        };
        AlfonsoStrategyOptions selected = request.ResolveAgentDefinition("alfonso").Alfonso!;
        AlfonsoStrategyOptions baseline = (request with
        {
            AlfonsoEntryPolicy = AlfonsoEntryPolicy.Core
        }).ResolveAgentDefinition("alfonso").Alfonso!;
        Assert.Multiple(() =>
        {
            Assert.That(new AlfonsoStrategyOptions().EntryPolicy, Is.EqualTo(AlfonsoEntryPolicy.Core));
            Assert.That(selected.EntryPolicy, Is.EqualTo(AlfonsoEntryPolicy.LowerTimeframeReversal));
            Assert.That(selected.RequireReversalConfirmation, Is.True);
            Assert.That(selected.TopInterval, Is.EqualTo(BarInterval.Hours(4)));
            Assert.That(selected.MiddleInterval, Is.EqualTo(BarInterval.Hours(1)));
            Assert.That(selected.LowerInterval, Is.EqualTo(BarInterval.Minutes(15)));
            Assert.That(selected.Zones.StopPaddingFraction, Is.EqualTo(baseline.Zones.StopPaddingFraction));
            Assert.That(selected.RequireControlAgreement, Is.EqualTo(baseline.RequireControlAgreement));
            Assert.That(selected.MinimumProfitMarginMultiple, Is.EqualTo(baseline.MinimumProfitMarginMultiple));
        });
        Assert.DoesNotThrow(selected.Validate);
    }
}
