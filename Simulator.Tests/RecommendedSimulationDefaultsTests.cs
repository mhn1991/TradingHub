using Brokers.Models;
using ChartAnnotator.Regime;
using RiskManager.Conditions;
using RiskManager;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class RecommendedSimulationDefaultsTests
{
    [Test]
    public void RuntimeDefaults_FormAValidRoleBasedResearchPreset()
    {
        var runtime = new BacktestRuntimeOptions();

        Assert.DoesNotThrow(() => runtime.Validate(selectedStrategyCount: 2));
        Assert.Multiple(() =>
        {
            Assert.That(runtime.WarmupDays, Is.EqualTo(21));
            Assert.That(runtime.StrategyTimeframes.TrendInterval, Is.EqualTo(BarInterval.Hours(2)));
            Assert.That(runtime.StrategyTimeframes.SecondaryTrendIntervals,
                Is.EqualTo(new[] { BarInterval.Hours(1) }));
            Assert.That(runtime.StrategyTimeframes.SetupIntervals,
                Is.EqualTo(new[] { BarInterval.Minutes(30) }));
            Assert.That(runtime.StrategyTimeframes.MinimumSetupAlignments, Is.EqualTo(1));
            Assert.That(runtime.AnalysisIntervals, Does.Contain(BarInterval.Hours(2)));
            Assert.That(runtime.AnalysisIntervals, Does.Contain(BarInterval.Minutes(30)));
            Assert.That(runtime.PositionSizing.Mode, Is.EqualTo(PositionSizingMode.FixedFractionalRisk));
            Assert.That(runtime.PositionSizing.RiskPercentOfEquity, Is.EqualTo(0.25m));
            Assert.That(runtime.PositionSizing.MaximumAccountMarginUsagePercent, Is.EqualTo(30m));
            Assert.That(runtime.PositionSizing.MaximumSinglePositionMarginPercent, Is.EqualTo(10m));
        });
    }

    [Test]
    public void PreviousFullMonth_ReturnsCompleteUtcCalendarWindow()
    {
        // Local August has started, but UTC is still in July.
        var now = new DateTimeOffset(2026, 8, 1, 1, 0, 0, TimeSpan.FromHours(5));

        (DateTimeOffset from, DateTimeOffset to) =
            RecommendedSimulationDefaults.PreviousFullMonth(now);

        Assert.Multiple(() =>
        {
            Assert.That(from, Is.EqualTo(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));
            Assert.That(to, Is.EqualTo(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        });
    }
    [Test]
    public void ReusableConditionDefaults_MatchRelaxedResearchConfiguration()
    {
        var conditions = new TradingConditionOptions();
        var regime = new MarketRegimeOptions();

        Assert.Multiple(() =>
        {
            Assert.That(conditions.AllowedSessions, Is.EquivalentTo(new[]
            {
                TradingSession.Asian,
                TradingSession.London,
                TradingSession.NewYork,
                TradingSession.LondonNewYorkOverlap
            }));
            Assert.That(conditions.RolloverBlackoutMinutesBefore, Is.EqualTo(15));
            Assert.That(conditions.RolloverBlackoutMinutesAfter, Is.EqualTo(15));
            Assert.That(conditions.SoftMaximumSpreadAtr, Is.EqualTo(0.25m));
            Assert.That(conditions.HardMaximumSpreadAtr, Is.EqualTo(0.50m));
            Assert.That(conditions.EconomicEventFilterEnabled, Is.False);
            Assert.That(regime.MaximumTradeableSpreadAtr, Is.EqualTo(0.25m));
            Assert.That(regime.HardMaximumSpreadAtr, Is.EqualTo(0.50m));
        });
    }

}
