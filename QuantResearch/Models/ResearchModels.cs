namespace QuantResearch.Models;

/// <summary>
/// Research-layer performance in <b>R-multiples</b> (risk units), not account currency.
/// Distinct from simulator <c>StrategyPerformanceSnapshot</c>, which uses cash P&amp;L.
/// Expect closed trades only (open trades must be filtered before calling
/// <see cref="ResearchMetrics.Calculate"/>).
/// </summary>
public sealed record ResearchPerformance
{
    /// <summary>Sum of closed-trade R-multiples (not cash net profit).</summary>
    public required decimal NetProfit { get; init; }
    public required decimal AverageR { get; init; }
    public required decimal ProfitFactor { get; init; }
    /// <summary>Peak-to-trough drawdown on the cumulative R equity curve.</summary>
    public required decimal MaximumDrawdown { get; init; }
    public decimal? Sharpe { get; init; }
    public decimal? Sortino { get; init; }
    public required int TradeCount { get; init; }
}

public sealed record ResearchTrade
{
    public required string TradeId { get; init; }
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public required string InstrumentGroup { get; init; }
    public required string Regime { get; init; }
    public required string SetupType { get; init; }
    public required string Direction { get; init; }
    public required string Session { get; init; }
    public required string VolatilityBucket { get; init; }
    public required decimal Confidence { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required DateTimeOffset ClosedAt { get; init; }
    public required decimal RMultiple { get; init; }
    public required decimal MaximumFavourableExcursionR { get; init; }
    public required decimal MaximumAdverseExcursionR { get; init; }
    public required decimal StopDistance { get; init; }
    public decimal PartialExitContributionR { get; init; }
    public decimal RunnerContributionR { get; init; }

    /// <summary>Entry-time NEoWave evidence; null means disabled or not ready.</summary>
    public string? NeoWaveHypothesisId { get; init; }
    public string? NeoWavePatternType { get; init; }
    public decimal? NeoWaveStructuralScore { get; init; }
    public decimal? NeoWaveConflictScore { get; init; }
    public decimal? NeoWaveInvalidationPrice { get; init; }
    public decimal NeoWaveRiskMultiplier { get; init; } = 1m;
}

public static class ResearchMetrics
{
    public static ResearchPerformance Calculate(IReadOnlyList<ResearchTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        decimal gains = trades.Where(item => item.RMultiple > 0m).Sum(item => item.RMultiple);
        decimal losses = -trades.Where(item => item.RMultiple < 0m).Sum(item => item.RMultiple);
        decimal equity = 0m;
        decimal peak = 0m;
        decimal maxDrawdown = 0m;
        foreach (ResearchTrade trade in trades.OrderBy(item => item.ClosedAt).ThenBy(item => item.TradeId, StringComparer.Ordinal))
        {
            equity += trade.RMultiple;
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, peak - equity);
        }
        decimal[] returns = trades.Select(item => item.RMultiple).ToArray();
        decimal mean = returns.Length == 0 ? 0m : returns.Average();
        decimal variance = returns.Length < 2 ? 0m : returns.Sum(value => (value - mean) * (value - mean)) / (returns.Length - 1);
        decimal downside = returns.Where(value => value < 0m).Select(value => value * value).DefaultIfEmpty().Average();
        return new ResearchPerformance
        {
            NetProfit = returns.Sum(),
            AverageR = mean,
            ProfitFactor = losses > 0m ? gains / losses : gains > 0m ? decimal.MaxValue : 0m,
            MaximumDrawdown = maxDrawdown,
            Sharpe = variance > 0m ? mean / (decimal)Math.Sqrt((double)variance) : null,
            Sortino = downside > 0m ? mean / (decimal)Math.Sqrt((double)downside) : null,
            TradeCount = returns.Length
        };
    }
}
