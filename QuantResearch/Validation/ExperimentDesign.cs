using QuantResearch.Models;

namespace QuantResearch.Validation;

public sealed record FeatureSwitches
{
    public bool PriceAction { get; init; } = true;
    public bool AdxDmi { get; init; } = true;
    public bool RsiRelationship { get; init; } = true;
    public bool BollingerContext { get; init; } = true;
    public bool RegimeRouting { get; init; } = true;
    public bool SecondaryTrend { get; init; } = true;
    public bool SetupIntervals { get; init; } = true;
    public bool CurrencyStrength { get; init; } = true;
    public bool SessionFilter { get; init; } = true;
    public bool ScaleOut { get; init; } = true;
    public bool ProfitFloor { get; init; } = true;
    public bool MfeGiveback { get; init; } = true;
    public bool StructuralTrailing { get; init; } = true;
    public bool AdaptiveSizing { get; init; } = true;
}

public sealed record FeatureAblationResult
{
    public required string Feature { get; init; }
    public required ResearchPerformance Baseline { get; init; }
    public required ResearchPerformance Ablated { get; init; }
    public required decimal NetProfitDelta { get; init; }
    public required decimal DrawdownDelta { get; init; }
}

public static class FeatureAblationRunner
{
    public static async Task<IReadOnlyList<FeatureAblationResult>> RunAsync(
        FeatureSwitches baseline,
        Func<FeatureSwitches, CancellationToken, Task<ResearchPerformance>> evaluate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(evaluate);
        ResearchPerformance baselineResult = await evaluate(baseline, cancellationToken).ConfigureAwait(false);
        var variants = new (string Name, Func<FeatureSwitches, FeatureSwitches> Disable)[]
        {
            (nameof(FeatureSwitches.PriceAction), value => value with { PriceAction = false }),
            (nameof(FeatureSwitches.AdxDmi), value => value with { AdxDmi = false }),
            (nameof(FeatureSwitches.RsiRelationship), value => value with { RsiRelationship = false }),
            (nameof(FeatureSwitches.BollingerContext), value => value with { BollingerContext = false }),
            (nameof(FeatureSwitches.RegimeRouting), value => value with { RegimeRouting = false }),
            (nameof(FeatureSwitches.SecondaryTrend), value => value with { SecondaryTrend = false }),
            (nameof(FeatureSwitches.SetupIntervals), value => value with { SetupIntervals = false }),
            (nameof(FeatureSwitches.CurrencyStrength), value => value with { CurrencyStrength = false }),
            (nameof(FeatureSwitches.SessionFilter), value => value with { SessionFilter = false }),
            (nameof(FeatureSwitches.ScaleOut), value => value with { ScaleOut = false }),
            (nameof(FeatureSwitches.ProfitFloor), value => value with { ProfitFloor = false }),
            (nameof(FeatureSwitches.MfeGiveback), value => value with { MfeGiveback = false }),
            (nameof(FeatureSwitches.StructuralTrailing), value => value with { StructuralTrailing = false }),
            (nameof(FeatureSwitches.AdaptiveSizing), value => value with { AdaptiveSizing = false })
        };
        var output = new List<FeatureAblationResult>(variants.Length);
        foreach ((string name, Func<FeatureSwitches, FeatureSwitches> disable) in variants)
        {
            ResearchPerformance ablated = await evaluate(disable(baseline), cancellationToken).ConfigureAwait(false);
            output.Add(new FeatureAblationResult
            {
                Feature = name,
                Baseline = baselineResult,
                Ablated = ablated,
                NetProfitDelta = baselineResult.NetProfit - ablated.NetProfit,
                DrawdownDelta = baselineResult.MaximumDrawdown - ablated.MaximumDrawdown
            });
        }
        return output;
    }
}

public sealed record ParameterSensitivityPoint
{
    public required IReadOnlyDictionary<string, decimal> Parameters { get; init; }
    public required ResearchPerformance Performance { get; init; }
}

public static class ParameterSensitivityRunner
{
    public static async Task<IReadOnlyList<ParameterSensitivityPoint>> RunAsync(
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> grid,
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<ResearchPerformance>> evaluate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(evaluate);
        IReadOnlyList<IReadOnlyDictionary<string, decimal>> combinations = Cartesian(
            grid.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(), 0,
            new Dictionary<string, decimal>(StringComparer.Ordinal));
        var result = new List<ParameterSensitivityPoint>(combinations.Count);
        foreach (IReadOnlyDictionary<string, decimal> parameters in combinations)
        {
            result.Add(new ParameterSensitivityPoint
            {
                Parameters = parameters,
                Performance = await evaluate(parameters, cancellationToken).ConfigureAwait(false)
            });
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, decimal>> Cartesian(
        KeyValuePair<string, IReadOnlyList<decimal>>[] grid,
        int index,
        Dictionary<string, decimal> current)
    {
        if (index == grid.Length)
            return [new Dictionary<string, decimal>(current, StringComparer.Ordinal)];
        var output = new List<IReadOnlyDictionary<string, decimal>>();
        foreach (decimal value in grid[index].Value.Order())
        {
            current[grid[index].Key] = value;
            output.AddRange(Cartesian(grid, index + 1, current));
        }
        current.Remove(grid[index].Key);
        return output;
    }
}
