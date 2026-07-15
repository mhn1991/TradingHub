using QuantResearch.Models;

namespace QuantResearch.Validation;

public enum MonteCarloMethod
{
    TradeOrderBootstrap,
    BlockBootstrap,
    SlippageSpreadPerturbation,
    MissedTradeSimulation,
    ClusteredLossStress
}

public sealed record MonteCarloOptions
{
    public int Iterations { get; init; } = 1_000;
    public int Seed { get; init; } = 17;
    public MonteCarloMethod Method { get; init; } = MonteCarloMethod.TradeOrderBootstrap;
    public int BlockSize { get; init; } = 5;
    public decimal MissedTradeProbability { get; init; } = 0.05m;
    public decimal CostPerturbationR { get; init; } = 0.05m;
    public decimal SafetyDrawdownThresholdR { get; init; } = 10m;

    public void Validate()
    {
        if (Iterations < 1 || BlockSize < 1 || MissedTradeProbability is < 0m or > 1m ||
            CostPerturbationR < 0m || SafetyDrawdownThresholdR <= 0m || !Enum.IsDefined(Method))
            throw new ArgumentOutOfRangeException(nameof(MonteCarloOptions));
    }
}

public sealed record MonteCarloIteration
{
    public required int Iteration { get; init; }
    public required decimal FinalEquityR { get; init; }
    public required decimal MaximumDrawdownR { get; init; }
    public required int LongestLossSequence { get; init; }
    public required bool BreachedSafetyThreshold { get; init; }
    public required int? RecoveryTrades { get; init; }
}

public sealed record MonteCarloReport
{
    public required int Seed { get; init; }
    public required MonteCarloMethod Method { get; init; }
    public required IReadOnlyList<MonteCarloIteration> Iterations { get; init; }
    public required decimal SafetyBreachProbability { get; init; }
}

public static class MonteCarloSimulator
{
    public static MonteCarloReport Run(IReadOnlyList<ResearchTrade> trades, MonteCarloOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(trades);
        MonteCarloOptions resolved = options ?? new MonteCarloOptions();
        resolved.Validate();
        decimal[] source = trades.OrderBy(item => item.ClosedAt).ThenBy(item => item.TradeId, StringComparer.Ordinal)
            .Select(item => item.RMultiple).ToArray();
        var random = new Random(resolved.Seed);
        var iterations = new List<MonteCarloIteration>(resolved.Iterations);
        for (int iteration = 0; iteration < resolved.Iterations; iteration++)
        {
            decimal[] path = Generate(source, resolved, random);
            decimal equity = 0m, peak = 0m, drawdown = 0m;
            int lossRun = 0, longestLossRun = 0;
            var equityPath = new decimal[path.Length];
            for (int i = 0; i < path.Length; i++)
            {
                equity += path[i];
                equityPath[i] = equity;
                if (equity >= peak) peak = equity;
                drawdown = Math.Max(drawdown, peak - equity);
                lossRun = path[i] < 0m ? lossRun + 1 : 0;
                longestLossRun = Math.Max(longestLossRun, lossRun);
            }
            iterations.Add(new MonteCarloIteration
            {
                Iteration = iteration,
                FinalEquityR = equity,
                MaximumDrawdownR = drawdown,
                LongestLossSequence = longestLossRun,
                BreachedSafetyThreshold = drawdown >= resolved.SafetyDrawdownThresholdR,
                RecoveryTrades = RecoveryTrades(equityPath)
            });
        }
        return new MonteCarloReport
        {
            Seed = resolved.Seed,
            Method = resolved.Method,
            Iterations = iterations,
            SafetyBreachProbability = iterations.Count(item => item.BreachedSafetyThreshold) / (decimal)iterations.Count
        };
    }

    private static int? RecoveryTrades(IReadOnlyList<decimal> equityPath)
    {
        decimal peak = 0m;
        decimal worst = 0m;
        decimal peakBeforeWorst = 0m;
        int troughIndex = -1;
        for (int i = 0; i < equityPath.Count; i++)
        {
            decimal equity = equityPath[i];
            peak = Math.Max(peak, equity);
            decimal current = peak - equity;
            if (current > worst)
            {
                worst = current;
                peakBeforeWorst = peak;
                troughIndex = i;
            }
        }
        if (troughIndex < 0 || worst <= 0m) return 0;
        for (int i = troughIndex + 1; i < equityPath.Count; i++)
        {
            if (equityPath[i] >= peakBeforeWorst)
                return i - troughIndex;
        }
        return null;
    }

    private static decimal[] Generate(decimal[] source, MonteCarloOptions options, Random random)
    {
        if (source.Length == 0) return [];
        var output = new List<decimal>(source.Length);
        while (output.Count < source.Length)
        {
            if (options.Method == MonteCarloMethod.BlockBootstrap)
            {
                int start = random.Next(source.Length);
                for (int i = 0; i < options.BlockSize && output.Count < source.Length; i++)
                    output.Add(source[(start + i) % source.Length]);
            }
            else
            {
                decimal value = source[random.Next(source.Length)];
                if (options.Method == MonteCarloMethod.MissedTradeSimulation && random.NextDouble() < (double)options.MissedTradeProbability)
                    value = 0m;
                if (options.Method == MonteCarloMethod.SlippageSpreadPerturbation)
                    value -= (decimal)random.NextDouble() * options.CostPerturbationR;
                output.Add(value);
            }
        }
        if (options.Method == MonteCarloMethod.ClusteredLossStress)
            return output.OrderBy(value => value >= 0m).ThenBy(value => value).ToArray();
        return output.ToArray();
    }
}
