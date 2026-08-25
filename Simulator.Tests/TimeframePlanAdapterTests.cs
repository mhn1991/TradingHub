using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using TradeManager;

namespace Simulator.Tests;

public sealed class TimeframePlanAdapterTests
{
    [Test]
    public void ProgressiveOptions_ToTimeframePlan_MapsPrimaryRolesAsGates()
    {
        var options = new ProgressiveStrategyOptions();

        TimeframePlan plan = options.ToTimeframePlan();

        Assert.That(plan.First(TimeframeRole.Context, TimeframeInfluence.Gate), Is.EqualTo(options.TrendInterval));
        Assert.That(plan.First(TimeframeRole.Trigger, TimeframeInfluence.Gate), Is.EqualTo(options.EntryInterval));
        Assert.That(plan.First(TimeframeRole.Confirmation, TimeframeInfluence.Vote), Is.EqualTo(options.ConfirmationInterval));
    }

    [Test]
    public void ProgressiveOptions_ToTimeframePlan_MultipleSecondaryTrendsBecomeMultipleVotes()
    {
        BarInterval secondary1 = BarInterval.Minutes(30);
        BarInterval secondary2 = BarInterval.Hours(4);
        var options = new ProgressiveStrategyOptions
        {
            SecondaryTrendIntervals = [secondary1, secondary2]
        };

        TimeframePlan plan = options.ToTimeframePlan();

        var votes = plan.For(TimeframeRole.Context, TimeframeInfluence.Vote);
        Assert.That(votes.Select(a => a.Interval), Is.EquivalentTo(new[] { secondary1, secondary2 }));
    }

    [Test]
    public void ProgressiveOptions_ToTimeframePlan_RegimeIntervalUnset_FallsBackToTrend()
    {
        var options = new ProgressiveStrategyOptions();

        TimeframePlan plan = options.ToTimeframePlan();

        Assert.That(plan.First(TimeframeRole.Regime), Is.EqualTo(options.TrendInterval));
    }

    [Test]
    public void ProgressiveOptions_ToTimeframePlan_RegimeIntervalSet_TakesPriorityOverTrendFallback()
    {
        BarInterval regime = BarInterval.Minutes(30);
        var options = new ProgressiveStrategyOptions { RegimeInterval = regime };

        TimeframePlan plan = options.ToTimeframePlan();

        var fallbacks = plan.For(TimeframeRole.Regime, TimeframeInfluence.Fallback);
        Assert.That(fallbacks[0].Interval, Is.EqualTo(regime));
        Assert.That(fallbacks[1].Interval, Is.EqualTo(options.TrendInterval));
        Assert.That(plan.First(TimeframeRole.Regime), Is.EqualTo(regime));
    }

    [Test]
    public void StructuralOptions_ToTimeframePlan_MapsAllContextIntervalsAsGates()
    {
        BarInterval extraContext = BarInterval.Hours(4);
        var options = new StructuralConfluenceStrategyOptions
        {
            AdditionalContextIntervals = [extraContext]
        };

        TimeframePlan plan = options.ToTimeframePlan();

        Assert.That(plan.First(TimeframeRole.Trigger), Is.EqualTo(options.TriggerInterval));
        Assert.That(plan.First(TimeframeRole.Setup), Is.EqualTo(options.SetupInterval));
        var contextGates = plan.For(TimeframeRole.Context, TimeframeInfluence.Gate);
        Assert.That(contextGates.Select(a => a.Interval), Is.EquivalentTo(new[] { options.ContextInterval, extraContext }));
    }

    [Test]
    public void PositionManagementOptions_ToTimeframePlan_OmitsLegacyManagementIntervalAlias()
    {
        var options = new PositionManagementOptions
        {
            ThesisInterval = BarInterval.Hours(1),
            MainStructureInterval = BarInterval.Minutes(15),
            FastStructureInterval = BarInterval.Minutes(5),
            ManagementInterval = BarInterval.Minutes(15)
        };

        TimeframePlan plan = options.ToTimeframePlan();

        Assert.That(plan.First(TimeframeRole.ManagementThesis), Is.EqualTo(options.ThesisInterval));
        Assert.That(plan.First(TimeframeRole.ManagementMain), Is.EqualTo(options.MainStructureInterval));
        Assert.That(plan.First(TimeframeRole.ManagementFast), Is.EqualTo(options.FastStructureInterval));
        Assert.That(plan.Assignments, Has.Count.EqualTo(3));
    }

    [Test]
    public void PositionManagementOptions_ToTimeframePlan_UnsetFieldsProduceNoAssignment()
    {
        var options = new PositionManagementOptions();

        TimeframePlan plan = options.ToTimeframePlan();

        Assert.That(plan.Assignments, Is.Empty);
    }

    // --- ApplyTimeframePlan: the write-side complement (§4b "Phase 3, revised" - bidirectional
    // adapters instead of a persisted-shape migration). Round-tripping through ToTimeframePlan/
    // ApplyTimeframePlan must reproduce the same effective timeframe fields without ever touching
    // ProgressiveStrategyOptions/StructuralConfluenceStrategyOptions/PositionManagementOptions'
    // own persisted shape.

    [Test]
    public void ProgressiveOptions_RoundTrip_ReproducesAllTimeframeFields()
    {
        var original = new ProgressiveStrategyOptions
        {
            TrendInterval = BarInterval.Hours(4),
            SecondaryTrendIntervals = [BarInterval.Hours(1), BarInterval.Minutes(30)],
            SetupIntervals = [BarInterval.Minutes(15)],
            ConfirmationInterval = BarInterval.Minutes(10),
            AdditionalConfirmationIntervals = [BarInterval.Minutes(5)],
            EntryInterval = BarInterval.Minutes(1),
            RegimeInterval = BarInterval.Minutes(30)
        };

        TimeframePlan plan = original.ToTimeframePlan();
        ProgressiveStrategyOptions reconstructed = new ProgressiveStrategyOptions().ApplyTimeframePlan(plan);

        Assert.That(reconstructed.TrendInterval, Is.EqualTo(original.TrendInterval));
        Assert.That(reconstructed.SecondaryTrendIntervals, Is.EquivalentTo(original.SecondaryTrendIntervals));
        Assert.That(reconstructed.SetupIntervals, Is.EquivalentTo(original.SetupIntervals));
        Assert.That(reconstructed.ConfirmationInterval, Is.EqualTo(original.ConfirmationInterval));
        Assert.That(reconstructed.AdditionalConfirmationIntervals, Is.EquivalentTo(original.AdditionalConfirmationIntervals));
        Assert.That(reconstructed.EntryInterval, Is.EqualTo(original.EntryInterval));
        Assert.That(reconstructed.RegimeInterval, Is.EqualTo(original.RegimeInterval));
    }

    [Test]
    public void ProgressiveOptions_ApplyTimeframePlan_UnsetRegimeStaysNullNotRedundantTrend()
    {
        var options = new ProgressiveStrategyOptions { TrendInterval = BarInterval.Hours(2) };

        TimeframePlan plan = options.ToTimeframePlan();
        ProgressiveStrategyOptions reapplied = options.ApplyTimeframePlan(plan);

        Assert.That(reapplied.RegimeInterval, Is.Null);
        Assert.That(reapplied.EffectiveRegimeInterval, Is.EqualTo(BarInterval.Hours(2)));
    }

    [Test]
    public void ProgressiveOptions_ApplyTimeframePlan_RoleNotInPlanLeavesBaselineUntouched()
    {
        var baseline = new ProgressiveStrategyOptions
        {
            ConfirmationInterval = BarInterval.Minutes(20),
            AdditionalConfirmationIntervals = [BarInterval.Minutes(45)]
        };
        var triggerOnlyPlan = new TimeframePlan
        {
            Assignments = [new TimeframeAssignment { Role = TimeframeRole.Trigger, Interval = BarInterval.Minutes(1), Influence = TimeframeInfluence.Gate }]
        };

        ProgressiveStrategyOptions result = baseline.ApplyTimeframePlan(triggerOnlyPlan);

        Assert.That(result.EntryInterval, Is.EqualTo(BarInterval.Minutes(1)));
        Assert.That(result.ConfirmationInterval, Is.EqualTo(baseline.ConfirmationInterval));
        Assert.That(result.AdditionalConfirmationIntervals, Is.EquivalentTo(baseline.AdditionalConfirmationIntervals));
    }

    [Test]
    public void StructuralOptions_RoundTrip_ReproducesAllTimeframeFields()
    {
        var original = new StructuralConfluenceStrategyOptions
        {
            TriggerInterval = BarInterval.Minutes(1),
            SetupInterval = BarInterval.Minutes(5),
            ContextInterval = BarInterval.Minutes(30),
            AdditionalContextIntervals = [BarInterval.Hours(4)]
        };

        TimeframePlan plan = original.ToTimeframePlan();
        StructuralConfluenceStrategyOptions reconstructed = new StructuralConfluenceStrategyOptions().ApplyTimeframePlan(plan);

        Assert.That(reconstructed.TriggerInterval, Is.EqualTo(original.TriggerInterval));
        Assert.That(reconstructed.SetupInterval, Is.EqualTo(original.SetupInterval));
        Assert.That(reconstructed.ContextInterval, Is.EqualTo(original.ContextInterval));
        Assert.That(reconstructed.AdditionalContextIntervals, Is.EquivalentTo(original.AdditionalContextIntervals));
    }

    [Test]
    public void PositionManagementOptions_RoundTrip_ReproducesSetFieldsAndLeavesUnsetAlone()
    {
        var original = new PositionManagementOptions
        {
            ThesisInterval = BarInterval.Hours(1),
            MainStructureInterval = BarInterval.Minutes(15)
            // FastStructureInterval deliberately left unset.
        };

        TimeframePlan plan = original.ToTimeframePlan();
        PositionManagementOptions reconstructed = new PositionManagementOptions().ApplyTimeframePlan(plan);

        Assert.That(reconstructed.ThesisInterval, Is.EqualTo(original.ThesisInterval));
        Assert.That(reconstructed.MainStructureInterval, Is.EqualTo(original.MainStructureInterval));
        Assert.That(reconstructed.FastStructureInterval, Is.Null);
    }
}
