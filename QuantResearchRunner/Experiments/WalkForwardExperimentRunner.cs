using System.Globalization;
using Brokers.Models;
using QuantResearch.Models;
using QuantResearch.Training.Mapping;
using QuantResearch.Validation;
using QuantResearchRunner.Models;
using Simulator.Models;
using Simulator.Services;

namespace QuantResearchRunner.Experiments;

/// <summary>
/// Drives real backtests through <see cref="BacktestApplicationService"/> across a
/// walk-forward plan: selects the best parameter combination on each fold's
/// <b>training</b> window, then runs the selected parameters once on validation and once
/// on the untouched test window.
/// </summary>
public static class WalkForwardExperimentRunner
{
    public sealed record CandidateResult
    {
        public required IReadOnlyDictionary<string, decimal> Parameters { get; init; }
        public required ResearchPerformance Performance { get; init; }
    }

    public static async Task<IReadOnlyList<WalkForwardFoldResult>> RunAsync(
        QuantResearchPlan plan,
        BacktestApplicationService service,
        ExperimentLedger? ledger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(service);
        plan.Validate();

        IReadOnlyList<WalkForwardFold> folds = WalkForwardPlanner.Create(plan.From, plan.To, plan.WalkForward);
        IReadOnlyList<IReadOnlyDictionary<string, decimal>> combinations = plan.ParameterGrid.Count == 0
            ? [new Dictionary<string, decimal>()]
            : ParameterSensitivityRunner.Combinations(plan.ParameterGrid);

        var results = new List<WalkForwardFoldResult>();
        foreach (InstrumentKey instrument in plan.Instruments)
        {
            foreach (string strategy in plan.Strategies)
            {
                foreach (WalkForwardFold fold in folds)
                {
                    results.Add(await RunFoldAsync(
                        plan, service, ledger, instrument, strategy, fold, combinations, cancellationToken)
                        .ConfigureAwait(false));
                }
            }
        }

        return results;
    }

    private static async Task<WalkForwardFoldResult> RunFoldAsync(
        QuantResearchPlan plan,
        BacktestApplicationService service,
        ExperimentLedger? ledger,
        InstrumentKey instrument,
        string strategy,
        WalkForwardFold fold,
        IReadOnlyList<IReadOnlyDictionary<string, decimal>> combinations,
        CancellationToken cancellationToken)
    {
        ResearchPerformance? bestTraining = null;
        IReadOnlyDictionary<string, decimal>? bestParameters = null;

        foreach (IReadOnlyDictionary<string, decimal> candidate in combinations)
        {
            string unitKey = ExperimentLedger.ComputeKey(
                "training",
                instrument.Value,
                strategy,
                fold.Fold,
                FormatParameters(candidate));

            CandidateResult? restored = ledger is null
                ? null
                : await ledger.TryGetResultAsync<CandidateResult>(unitKey, cancellationToken)
                    .ConfigureAwait(false);

            ResearchPerformance performance;
            if (restored is not null)
            {
                performance = restored.Performance;
            }
            else
            {
                BacktestRequest request = BuildRequest(
                    plan, instrument, strategy, fold.TrainingFrom, fold.TrainingTo, candidate);

                try
                {
                    ComparativeSimulationResult result = await service
                        .RunToCompletionAsync(request, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    performance = ResearchMetrics.Calculate(MapTrades(result, strategy));
                    if (ledger is not null)
                    {
                        await ledger.MarkCompletedAsync(
                                unitKey,
                                new CandidateResult { Parameters = candidate, Performance = performance },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ledger is not null)
                    {
                        await ledger.MarkAsync(unitKey, ExperimentUnitStatus.Failed, ex.Message, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }
            }

            if (plan.MaximumAcceptableDrawdown is decimal cap && performance.MaximumDrawdown > cap)
                continue;

            if (IsBetter(performance, bestTraining))
            {
                bestTraining = performance;
                bestParameters = candidate;
            }
        }

        // Nothing cleared the drawdown cap (or every candidate failed) - fall back to the
        // grid's first combination rather than leaving the fold without a test run.
        bestParameters ??= combinations[0];
        bestTraining ??= ResearchMetrics.Calculate([]);

        ResearchPerformance validation = await EvaluateWindowAsync(
            plan, service, ledger, instrument, strategy, fold.Fold, "validation",
            fold.ValidationFrom, fold.ValidationTo, bestParameters, cancellationToken)
            .ConfigureAwait(false);

        ResearchPerformance test = await EvaluateWindowAsync(
            plan, service, ledger, instrument, strategy, fold.Fold, "test",
            fold.TestFrom, fold.TestTo, bestParameters, cancellationToken)
            .ConfigureAwait(false);

        decimal validationSharpe = validation.Sharpe ?? 0m;
        decimal testSharpe = test.Sharpe ?? 0m;

        return new WalkForwardFoldResult
        {
            Window = fold,
            Instrument = instrument.Value,
            StrategyId = strategy,
            Training = bestTraining,
            Validation = validation,
            Test = test,
            SelectedParameters = bestParameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(CultureInfo.InvariantCulture)),
            ValidationToTestDegradation = validationSharpe - testSharpe
        };
    }

    private static async Task<ResearchPerformance> EvaluateWindowAsync(
        QuantResearchPlan plan,
        BacktestApplicationService service,
        ExperimentLedger? ledger,
        InstrumentKey instrument,
        string strategy,
        int fold,
        string phase,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyDictionary<string, decimal> parameters,
        CancellationToken cancellationToken)
    {
        string unitKey = ExperimentLedger.ComputeKey(
            phase, instrument.Value, strategy, fold, FormatParameters(parameters));

        if (ledger is not null)
        {
            CandidateResult? restored = await ledger
                .TryGetResultAsync<CandidateResult>(unitKey, cancellationToken)
                .ConfigureAwait(false);
            if (restored is not null)
                return restored.Performance;
        }

        BacktestRequest request = BuildRequest(plan, instrument, strategy, from, to, parameters);
        ComparativeSimulationResult result = await service
            .RunToCompletionAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        ResearchPerformance performance = ResearchMetrics.Calculate(MapTrades(result, strategy));

        if (ledger is not null)
        {
            await ledger.MarkCompletedAsync(
                    unitKey,
                    new CandidateResult { Parameters = parameters, Performance = performance },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return performance;
    }

    /// <summary>Strictly-greater comparison so the first-encountered best candidate wins ties.</summary>
    private static bool IsBetter(ResearchPerformance candidate, ResearchPerformance? current)
    {
        if (current is null)
            return true;
        decimal candidateSharpe = candidate.Sharpe ?? decimal.MinValue;
        decimal currentSharpe = current.Sharpe ?? decimal.MinValue;
        if (candidateSharpe != currentSharpe)
            return candidateSharpe > currentSharpe;
        return candidate.ProfitFactor > current.ProfitFactor;
    }

    private static BacktestRequest BuildRequest(
        QuantResearchPlan plan,
        InstrumentKey instrument,
        string strategy,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyDictionary<string, decimal> parameters)
    {
        DateTimeOffset streamFrom = plan.BaselineRuntime.ResolveWarmupFrom(from);
        BacktestRequest request = new()
        {
            Instrument = instrument,
            From = from,
            To = to,
            Strategies = [strategy],
            StartingBalance = plan.StartingBalance,
            Quantity = plan.Quantity,
            OutputDirectory = Path.Combine(plan.OutputDirectory, "runs"),
            JobsDirectory = Path.Combine(plan.OutputDirectory, "jobs"),
            Runtime = plan.BaselineRuntime,
            InlineCandles = plan.InlineCandles is { } master
                ? CandlePrefetchPlanner.SliceForWindow(master, from, to, streamFrom)
                : null
        };
        request = FeatureSwitchMapper.Apply(request, plan.FeatureSwitches);
        return ParameterOverrideApplier.Apply(request, parameters);
    }

    private static IReadOnlyList<ResearchTrade> MapTrades(ComparativeSimulationResult result, string strategy) =>
        ResearchTradeMapper.ToResearchTrades(
            result.Strategies
                .Where(item => item.StrategyId == strategy)
                .SelectMany(item => item.Result.Trades));

    private static string FormatParameters(IReadOnlyDictionary<string, decimal> parameters) =>
        string.Join(',', parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value.ToString(CultureInfo.InvariantCulture)}"));
}
