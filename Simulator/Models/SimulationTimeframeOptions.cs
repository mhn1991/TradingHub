using Brokers.Models;
using Simulator.MarketData;

namespace Simulator.Models;

/// <summary>Separates the execution clock from the analysis base and higher timeframes.</summary>
public sealed record SimulationTimeframeOptions
{
    /// <summary>Finest stream used for fills, stops, OCO, MFE/MAE.</summary>
    public required BarInterval ExecutionInterval { get; init; }

    /// <summary>Smallest completed candle delivered to ChartAnnotator (default 1m).</summary>
    public required BarInterval AnalysisBaseInterval { get; init; }

    /// <summary>Higher analysis intervals aggregated from the analysis base.</summary>
    public required IReadOnlyList<BarInterval> AnalysisIntervals { get; init; }

    public static SimulationTimeframeOptions FromPrecision(
        SimulationPrecisionMode mode,
        IReadOnlyList<BarInterval>? analysisIntervals = null)
    {
        IReadOnlyList<BarInterval> analysis = analysisIntervals ??
            RecommendedSimulationDefaults.AnalysisIntervals;

        return mode switch
        {
            SimulationPrecisionMode.Fast => new SimulationTimeframeOptions
            {
                ExecutionInterval = BarInterval.Minutes(1),
                AnalysisBaseInterval = BarInterval.Minutes(1),
                AnalysisIntervals = analysis
            },
            SimulationPrecisionMode.BrokerNativePrecision => new SimulationTimeframeOptions
            {
                ExecutionInterval = BarInterval.Seconds(5),
                AnalysisBaseInterval = BarInterval.Minutes(1),
                AnalysisIntervals = analysis
            },
            SimulationPrecisionMode.HighPrecision => new SimulationTimeframeOptions
            {
                ExecutionInterval = BarInterval.Seconds(1),
                AnalysisBaseInterval = BarInterval.Minutes(1),
                AnalysisIntervals = analysis
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public void Validate()
    {
        if (!ExecutionInterval.IsValid)
            throw new ArgumentException("ExecutionInterval is invalid.");
        if (!AnalysisBaseInterval.IsValid)
            throw new ArgumentException("AnalysisBaseInterval is invalid.");
        if (AnalysisIntervals is null || AnalysisIntervals.Count == 0)
            throw new ArgumentException("At least one analysis interval is required.");
        if (AnalysisIntervals.Any(i => !i.IsValid))
            throw new ArgumentException("Every analysis interval must be valid.");

        // Execution must be finer or equal to analysis base.
        if (BarIntervalParser.CompareDuration(ExecutionInterval, AnalysisBaseInterval) > 0)
        {
            throw new ArgumentException(
                $"ExecutionInterval ({BarIntervalParser.Format(ExecutionInterval)}) must be " +
                $"<= AnalysisBaseInterval ({BarIntervalParser.Format(AnalysisBaseInterval)}).");
        }

        if (ExecutionInterval != AnalysisBaseInterval &&
            !BarIntervalParser.IsDivisible(AnalysisBaseInterval, ExecutionInterval))
        {
            throw new ArgumentException(
                $"AnalysisBaseInterval {BarIntervalParser.Format(AnalysisBaseInterval)} is not " +
                $"divisible by ExecutionInterval {BarIntervalParser.Format(ExecutionInterval)}.");
        }

        foreach (BarInterval interval in AnalysisIntervals)
        {
            if (interval == AnalysisBaseInterval)
                continue;
            if (BarIntervalParser.CompareDuration(interval, AnalysisBaseInterval) < 0)
            {
                throw new ArgumentException(
                    $"Analysis interval {BarIntervalParser.Format(interval)} is finer than analysis base.");
            }

            if (!BarIntervalParser.IsDivisible(interval, AnalysisBaseInterval) &&
                interval.Unit is BarUnit.Second or BarUnit.Minute or BarUnit.Hour)
            {
                throw new ArgumentException(
                    $"Analysis interval {BarIntervalParser.Format(interval)} is not aligned with " +
                    $"analysis base {BarIntervalParser.Format(AnalysisBaseInterval)}.");
            }
        }
    }

    /// <summary>Union analysis base, requested analysis, and any extra required intervals.</summary>
    public IReadOnlyList<BarInterval> EffectiveAnalysisIntervals(
        IEnumerable<BarInterval>? additionalRequired = null)
    {
        var set = new HashSet<BarInterval> { AnalysisBaseInterval };
        foreach (BarInterval interval in AnalysisIntervals)
            set.Add(interval);
        if (additionalRequired is not null)
        {
            foreach (BarInterval interval in additionalRequired)
                set.Add(interval);
        }

        return set
            .OrderBy(BarIntervalParser.ApproximateSeconds)
            .ToArray();
    }
}

/// <summary>Configurable role-based progressive strategy timeframe stack.</summary>
public sealed record ProgressiveStrategyTimeframes
{
    public BarInterval TrendInterval { get; init; } = BarInterval.Hours(1);
    public IReadOnlyList<BarInterval> SecondaryTrendIntervals { get; init; } = [];
    public IReadOnlyList<BarInterval> SetupIntervals { get; init; } = [];
    public BarInterval ConfirmationInterval { get; init; } = BarInterval.Minutes(15);
    public IReadOnlyList<BarInterval> AdditionalConfirmationIntervals { get; init; } = [];
    public BarInterval EntryInterval { get; init; } = BarInterval.Minutes(5);
    public int MinimumSecondaryTrendAlignments { get; init; }
    public int MinimumSetupAlignments { get; init; }
    public int MinimumConfirmationAlignments { get; init; } = 1;
    public bool StrongOppositionVeto { get; init; } = true;

    public void Validate()
    {
        var options = new Agent.Strategies.ProgressiveStrategyOptions
        {
            TrendInterval = TrendInterval,
            SecondaryTrendIntervals = SecondaryTrendIntervals,
            SetupIntervals = SetupIntervals,
            ConfirmationInterval = ConfirmationInterval,
            AdditionalConfirmationIntervals = AdditionalConfirmationIntervals,
            EntryInterval = EntryInterval,
            MinimumSecondaryTrendAlignments = MinimumSecondaryTrendAlignments,
            MinimumSetupAlignments = MinimumSetupAlignments,
            MinimumConfirmationAlignments = MinimumConfirmationAlignments,
            StrongOppositionVeto = StrongOppositionVeto
        };
        options.Validate();
    }

    public IReadOnlyList<BarInterval> RequiredIntervals =>
        [
            EntryInterval,
            ConfirmationInterval,
            .. AdditionalConfirmationIntervals,
            .. SetupIntervals,
            .. SecondaryTrendIntervals,
            TrendInterval
        ];
}
