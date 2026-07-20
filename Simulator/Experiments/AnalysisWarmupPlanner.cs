using System.Reflection;
using Agent.Factories;
using Agent.Abstractions;
using Brokers.Models;
using Simulator.Experiments.Models;

namespace Simulator.Experiments;

public sealed record AnalysisWarmupPlan
{
    public required int RequestedMinimumDays { get; init; }
    public required int ResolvedWarmupDays { get; init; }
    public required DateTimeOffset StreamFrom { get; init; }
    public required int RequiredAnalysisBars { get; init; }
    public required string DominantRequirement { get; init; }
    public required bool HasSufficientData { get; init; }
    public required bool PromotionEligible { get; init; }
}

public static class AnalysisWarmupPlanner
{
    public static AnalysisWarmupPlan Plan(
        SimulationStrategyProfile profile,
        DateTimeOffset entriesEnabledFrom,
        int requestedMinimumDays,
        int? warmupBaseCandleCount = null,
        DateTimeOffset? availableDataFrom = null,
        bool allowInsufficientWarmup = false)
    {
        profile.Validate();
        if (entriesEnabledFrom.Offset != TimeSpan.Zero || requestedMinimumDays < 0 || warmupBaseCandleCount < 0)
            throw new ArgumentException("Warm-up inputs must be non-negative and entriesEnabledFrom must be UTC.");

        ITradingAgent agent = TradingAgentFactory.Create(profile.Agent);
        BarInterval slowest = agent.RequiredIntervals
            .Concat(ManagementIntervals(profile))
            .OrderByDescending(ApproximateDuration)
            .First();
        (int bars, string reason) = FindDominantBarRequirement(profile.Analysis);
        TimeSpan analysisRequirement = Multiply(ApproximateDuration(slowest), bars);
        TimeSpan baseCountRequirement = warmupBaseCandleCount is > 0
            ? Multiply(ApproximateDuration(profile.Runtime.Options.AnalysisBaseInterval), warmupBaseCandleCount.Value)
            : TimeSpan.Zero;
        TimeSpan requested = TimeSpan.FromDays(requestedMinimumDays);

        (TimeSpan duration, string dominant) = new[]
            {
                (requested, "RequestedMinimumWarmup"),
                (analysisRequirement, $"{reason}@{slowest}"),
                (baseCountRequirement, "WarmupBaseCandleCount")
            }
            .OrderByDescending(item => item.Item1)
            .First();
        int resolvedDays = (int)Math.Ceiling(duration.TotalDays);
        DateTimeOffset streamFrom = entriesEnabledFrom.AddDays(-resolvedDays);
        bool sufficient = availableDataFrom is null || availableDataFrom <= streamFrom;
        if (!sufficient && !allowInsufficientWarmup)
            throw new ArgumentException("Available data cannot satisfy the calculated analysis warm-up.");

        return new AnalysisWarmupPlan
        {
            RequestedMinimumDays = requestedMinimumDays,
            ResolvedWarmupDays = resolvedDays,
            StreamFrom = streamFrom,
            RequiredAnalysisBars = bars,
            DominantRequirement = dominant,
            HasSufficientData = sufficient,
            PromotionEligible = sufficient
        };
    }

    private static IEnumerable<BarInterval> ManagementIntervals(SimulationStrategyProfile profile)
    {
        if (profile.Management.FastStructureInterval is { } fast) yield return fast;
        if (profile.Management.MainStructureInterval is { } main) yield return main;
        if (profile.Management.ThesisInterval is { } thesis) yield return thesis;
        if (profile.Management.ManagementInterval is { } legacy) yield return legacy;
    }

    private static (int Bars, string Reason) FindDominantBarRequirement(object root)
    {
        var candidates = new List<(int Value, string Path)>();
        Visit(root, root.GetType().Name, candidates, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return candidates.Count == 0
            ? (1, "MinimumAnalysisHistory")
            : candidates.OrderByDescending(item => item.Value).ThenBy(item => item.Path, StringComparer.Ordinal)
                .Select(item => (item.Value, item.Path)).First();
    }

    private static void Visit(object? value, string path, ICollection<(int Value, string Path)> output, ISet<object> visited)
    {
        if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum || !visited.Add(value))
            return;
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? child = property.GetValue(value);
            string childPath = $"{path}.{property.Name}";
            if (child is int number && number > 0 && IsBarRequirement(property.Name))
                output.Add((number, childPath));
            else if (child is not System.Collections.IEnumerable)
                Visit(child, childPath, output, visited);
        }
    }

    // Widened from the original 6 tokens after surveying real option-class field names across
    // ChartAnnotator/Agent/RiskManager/TradeManager: the original list missed several genuine
    // lookback-shaped fields entirely, e.g. *MinimumSamples (AdxCalibrationMinimumSamples,
    // BollingerWidthMinimumSamples, ...), *MinimumBaseCandles/*MinimumOriginationCandles, and
    // MinimumConfirmedMonoWaves ("Confirmed" doesn't substring-match "Confirmation"). A missed
    // token here means the computed warmup requirement silently under-counts, which the
    // sufficiency check downstream cannot catch since it only validates the computed number
    // against available data, not whether the computation itself found every real requirement.
    private static bool IsBarRequirement(string name) =>
        new[]
        {
            "Period", "Lookback", "Capacity", "Window", "Bars", "Persistence", "Confirmation",
            "Confirmed", "Samples", "Candles", "Touch", "History"
        }.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static TimeSpan ApproximateDuration(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => TimeSpan.FromSeconds(interval.Value),
        BarUnit.Minute => TimeSpan.FromMinutes(interval.Value),
        BarUnit.Hour => TimeSpan.FromHours(interval.Value),
        BarUnit.Day => TimeSpan.FromDays(interval.Value),
        BarUnit.Week => TimeSpan.FromDays(interval.Value * 7d),
        BarUnit.Month => TimeSpan.FromDays(interval.Value * 31d),
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    private static TimeSpan Multiply(TimeSpan value, int count) =>
        TimeSpan.FromTicks(checked(value.Ticks * count));
}
