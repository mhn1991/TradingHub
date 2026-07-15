using Brokers.Models;
using RiskManager;

namespace Simulator.Models;

/// <summary>
/// Balanced, broker-neutral research defaults used by the simulator entry points.
/// They favour a useful sample size and conservative execution assumptions over
/// strategy-specific optimisation.
/// </summary>
public static class RecommendedSimulationDefaults
{
    public const string Instrument = "FX:EUR/USD";
    public const int WarmupDays = 21;

    public static PositionSizingOptions PositionSizing => new()
    {
        Mode = PositionSizingMode.FixedFractionalRisk,
        FixedQuantity = 1_000m,
        FixedCashRisk = 250m,
        RiskPercentOfEquity = 0.25m,
        MinimumQuantity = 1m,
        QuantityStep = 1m,
        MaximumAccountMarginUsagePercent = 30m,
        MaximumSinglePositionMarginPercent = 10m,
        Leverage = 20m
    };

    public static IReadOnlyList<BarInterval> AnalysisIntervals =>
    [
        BarInterval.Minutes(5),
        BarInterval.Minutes(15),
        BarInterval.Minutes(30),
        BarInterval.Hours(1),
        BarInterval.Hours(2)
    ];

    public static ProgressiveStrategyTimeframes StrategyTimeframes => new()
    {
        TrendInterval = BarInterval.Hours(2),
        SecondaryTrendIntervals = [BarInterval.Hours(1)],
        SetupIntervals = [BarInterval.Minutes(30)],
        ConfirmationInterval = BarInterval.Minutes(15),
        EntryInterval = BarInterval.Minutes(5),
        MinimumSecondaryTrendAlignments = 0,
        MinimumSetupAlignments = 1,
        MinimumConfirmationAlignments = 1,
        StrongOppositionVeto = true
    };

    /// <summary>Previous complete UTC month, avoiding a partial current-month sample.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) PreviousFullMonth(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        DateTimeOffset to = new(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (to.AddMonths(-1), to);
    }
}
