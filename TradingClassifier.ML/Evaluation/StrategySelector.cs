using ChartAnnotator.Regime;

namespace TradingClassifier.ML.Evaluation;

/// <summary>Measured performance of one strategy inside one regime.</summary>
public sealed record RegimePerformance
{
    public required string StrategyId { get; init; }
    public required MarketRegime Regime { get; init; }
    public required int Trades { get; init; }
    public required double TotalR { get; init; }
    public double ExpectancyR => Trades == 0 ? 0 : TotalR / Trades;
}

public sealed record StrategyChoice
{
    public required MarketRegime Regime { get; init; }
    public required string? StrategyId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Routes regimes to strategies on measured expectancy — the ML catalogue's Phase 6
/// "strategy selector".
/// <para>
/// The whole value of a selector is that it is fitted on one period and applied to another, so it
/// takes measured history explicitly rather than computing it: the caller is responsible for that
/// history coming from training/validation folds, never from the period being scored.
/// </para>
/// <para>
/// Two refusals are deliberate. It will not select a strategy whose expectancy is negative — routing
/// to the least-bad losing option is worse than not trading, and every strategy measured in this
/// repo so far is negative, so a selector that always picks something would be actively harmful. And
/// it will not select on fewer than <see cref="MinimumTrades"/> observations, because §2.18 recorded
/// walk-forward windows with 1-4 trades producing profit factors of 17 and infinity.
/// </para>
/// </summary>
public sealed class StrategySelector
{
    public int MinimumTrades { get; init; } = 30;

    /// <summary>Expectancy in R a strategy must clear before it is worth routing to at all.</summary>
    public double MinimumExpectancyR { get; init; } = 0.0;

    public IReadOnlyList<StrategyChoice> Fit(IReadOnlyList<RegimePerformance> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        List<StrategyChoice> choices = [];
        foreach (var group in history.GroupBy(item => item.Regime).OrderBy(group => group.Key))
        {
            RegimePerformance[] eligible = [.. group.Where(item => item.Trades >= MinimumTrades)];
            if (eligible.Length == 0)
            {
                choices.Add(new StrategyChoice
                {
                    Regime = group.Key,
                    StrategyId = null,
                    Reason = $"No strategy has {MinimumTrades}+ trades in this regime " +
                        $"(best has {group.Max(item => item.Trades)})."
                });
                continue;
            }

            RegimePerformance best = eligible.OrderByDescending(item => item.ExpectancyR).First();
            if (best.ExpectancyR <= MinimumExpectancyR)
            {
                choices.Add(new StrategyChoice
                {
                    Regime = group.Key,
                    StrategyId = null,
                    Reason = $"Best available is '{best.StrategyId}' at {best.ExpectancyR:F4}R, " +
                        "which does not clear the bar — not trading beats the least-bad loser."
                });
                continue;
            }

            choices.Add(new StrategyChoice
            {
                Regime = group.Key,
                StrategyId = best.StrategyId,
                Reason = $"'{best.StrategyId}' at {best.ExpectancyR:F4}R over {best.Trades} trades."
            });
        }

        return choices;
    }
}
