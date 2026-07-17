using Brokers.Models;
using QuantResearch.Models;
using QuantResearch.Training.Mapping;
using QuantResearch.Validation;
using QuantResearchRunner.Models;
using Simulator.Models;
using Simulator.Services;

namespace QuantResearchRunner.Experiments;

/// <summary>
/// Builds the real backtest-driven "evaluate" closures <see cref="FeatureAblationRunner"/> and
/// <see cref="ParameterSensitivityRunner"/> expect. Each closure runs once over the plan's full
/// [<see cref="QuantResearchPlan.From"/>, <see cref="QuantResearchPlan.To"/>) window per variant
/// for every instrument × strategy pair in the plan, then aggregates R-metrics.
/// </summary>
public static class ExperimentEvaluators
{
    public static Func<FeatureSwitches, CancellationToken, Task<ResearchPerformance>> BuildAblationEvaluator(
        QuantResearchPlan plan, BacktestApplicationService service)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(service);

        return async (switches, cancellationToken) =>
        {
            var trades = new List<ResearchTrade>();
            foreach (InstrumentKey instrument in plan.Instruments)
            {
                foreach (string strategy in plan.Strategies)
                {
                    BacktestRequest request = BuildRequest(plan, instrument, strategy, switches, parameters: null);
                    ComparativeSimulationResult result = await service
                        .RunToCompletionAsync(request, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    trades.AddRange(MapTrades(result, strategy));
                }
            }

            return ResearchMetrics.Calculate(trades);
        };
    }

    public static Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<ResearchPerformance>> BuildSensitivityEvaluator(
        QuantResearchPlan plan, BacktestApplicationService service)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(service);

        return async (parameters, cancellationToken) =>
        {
            var trades = new List<ResearchTrade>();
            foreach (InstrumentKey instrument in plan.Instruments)
            {
                foreach (string strategy in plan.Strategies)
                {
                    BacktestRequest request = BuildRequest(plan, instrument, strategy, plan.FeatureSwitches, parameters);
                    ComparativeSimulationResult result = await service
                        .RunToCompletionAsync(request, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    trades.AddRange(MapTrades(result, strategy));
                }
            }

            return ResearchMetrics.Calculate(trades);
        };
    }

    private static BacktestRequest BuildRequest(
        QuantResearchPlan plan,
        InstrumentKey instrument,
        string strategy,
        FeatureSwitches switches,
        IReadOnlyDictionary<string, decimal>? parameters)
    {
        DateTimeOffset streamFrom = plan.BaselineRuntime.ResolveWarmupFrom(plan.From);
        BacktestRequest request = new()
        {
            Instrument = instrument,
            From = plan.From,
            To = plan.To,
            Strategies = [strategy],
            StartingBalance = plan.StartingBalance,
            Quantity = plan.Quantity,
            OutputDirectory = Path.Combine(plan.OutputDirectory, "runs"),
            JobsDirectory = Path.Combine(plan.OutputDirectory, "jobs"),
            Runtime = plan.BaselineRuntime,
            InlineCandles = plan.InlineCandles is { } master
                ? CandlePrefetchPlanner.SliceForWindow(master, plan.From, plan.To, streamFrom)
                : null
        };
        request = FeatureSwitchMapper.Apply(request, switches);
        return parameters is null ? request : ParameterOverrideApplier.Apply(request, parameters);
    }

    private static IReadOnlyList<ResearchTrade> MapTrades(ComparativeSimulationResult result, string strategy) =>
        ResearchTradeMapper.ToResearchTrades(
            result.Strategies
                .Where(item => item.StrategyId == strategy)
                .SelectMany(item => item.Result.Trades));
}
