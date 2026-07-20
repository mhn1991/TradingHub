using Agent.Configuration;
using Brokers.Models;
using Simulator.Calibration;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Everything a <see cref="BacktestCandidateEvaluatorFactory{TOptions}"/> needs that stays constant
/// across every fold/window of one calibration run. <see cref="Candles"/> is optional: when
/// supplied, it must be the complete, already loaded candle set spanning every fold's
/// warmup-extended training window through the external holdout - the factory slices it per window
/// rather than re-reading a data source per candidate (used by fast/offline/deterministic tests via
/// <see cref="HistoricalDataSourceKind.InlineTestData"/>). When null, <see cref="RuntimeTemplate"/>'s
/// own <see cref="BacktestRuntimeOptions.SourceKind"/> (typically <see cref="HistoricalDataSourceKind.OandaCandles"/>)
/// is used unchanged and the engine reads candles itself exactly like a normal backtest - the real
/// path a CLI/API-driven production calibration run should use.
/// </summary>
public sealed record BacktestCandidateEvaluatorSettings
{
    public required InstrumentKey Instrument { get; init; }
    public required string StrategyType { get; init; }
    public required BacktestRuntimeOptions RuntimeTemplate { get; init; }
    public IReadOnlyList<Candle>? Candles { get; init; }
    public required int WarmupDays { get; init; }
    public decimal Quantity { get; init; } = 1_000m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;

    /// <summary>
    /// Optional content-addressed cache (blueprint §12/§14). Null (the default) means every
    /// evaluation runs a fresh real backtest, exactly as before this field existed. Pointing a
    /// <see cref="Persistence.FileCalibrationCandidateCache"/> at the same directory across two
    /// separate process runs of the same request is what makes pause/resume actually skip
    /// already-completed work rather than merely reproducing the same decision from scratch.
    /// </summary>
    public ICalibrationCandidateCache? Cache { get; init; }
}

/// <summary>
/// Real-backtest-running <see cref="ICandidateEvaluatorFactory"/> (blueprint §19 Phase 6: "backtest
/// candidate adapter"). Generic over <c>TOptions</c> and parameterized by
/// <paramref name="buildAgentDefinition"/> so the same engine-layer adapter serves every calibrated
/// strategy, not just <c>IndicatorConfluencePlaybook</c> - a later strategy (Phase 8) supplies its
/// own manifest, baseline options, and options-to-agent-definition projection, nothing here changes.
/// </summary>
public sealed class BacktestCandidateEvaluatorFactory<TOptions>(
    IBacktestApplicationService backtests,
    IIndicatorCalibrationManifest<TOptions> manifest,
    TOptions baselineOptions,
    Func<TOptions, TradingAgentDefinition> buildAgentDefinition,
    BacktestCandidateEvaluatorSettings settings) : ICandidateEvaluatorFactory
{
    private readonly IBacktestApplicationService _backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
    private readonly IIndicatorCalibrationManifest<TOptions> _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    private readonly TOptions _baselineOptions = baselineOptions ?? throw new ArgumentNullException(nameof(baselineOptions));
    private readonly Func<TOptions, TradingAgentDefinition> _buildAgentDefinition =
        buildAgentDefinition ?? throw new ArgumentNullException(nameof(buildAgentDefinition));
    private readonly BacktestCandidateEvaluatorSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public ICandidateEvaluator CreateEvaluator(DateTimeOffset from, DateTimeOffset to) =>
        new BacktestCandidateEvaluator<TOptions>(_backtests, _manifest, _baselineOptions, _buildAgentDefinition, _settings, from, to);
}

/// <summary>
/// One fold/window's real-backtest evaluator. The candle stream fed to the engine starts
/// <see cref="BacktestCandidateEvaluatorSettings.WarmupDays"/> before <paramref name="attributionFrom"/>
/// so indicators are warmed up by the time the attribution window begins, but only trades whose
/// entry falls inside [<paramref name="attributionFrom"/>, <paramref name="attributionTo"/>) count
/// toward the returned score - warmup-period trades and trades opened by a neighbouring
/// fold/window's own stream never leak into this window's evaluation.
/// </summary>
public sealed class BacktestCandidateEvaluator<TOptions>(
    IBacktestApplicationService backtests,
    IIndicatorCalibrationManifest<TOptions> manifest,
    TOptions baselineOptions,
    Func<TOptions, TradingAgentDefinition> buildAgentDefinition,
    BacktestCandidateEvaluatorSettings settings,
    DateTimeOffset attributionFrom,
    DateTimeOffset attributionTo) : ICandidateEvaluator
{
    public async Task<BacktestEvaluationResult> EvaluateAsync(
        CalibrationCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        TOptions effective = candidate.ApplyTo(baselineOptions, manifest);
        TradingAgentDefinition agentDefinition = buildAgentDefinition(effective);
        agentDefinition.Validate();

        BacktestEvaluationIdentity? identity = settings.Cache is null
            ? null
            : BuildIdentity(effective, agentDefinition);
        if (identity is not null && settings.Cache!.TryGet(identity, out BacktestEvaluationResult? cached))
            return cached!;

        DateTimeOffset streamFrom = attributionFrom.AddDays(-settings.WarmupDays);
        Candle[]? windowCandles = settings.Candles?
            .Where(candle => candle.OpenTime >= streamFrom && candle.OpenTime < attributionTo)
            .ToArray();

        var request = new BacktestRequest
        {
            Instrument = settings.Instrument,
            From = streamFrom,
            To = attributionTo,
            Strategies = [settings.StrategyType],
            StrategyAssignments =
            [
                new StrategyInstrumentAssignment
                {
                    Id = "candidate",
                    StrategyType = settings.StrategyType,
                    Instrument = settings.Instrument,
                    AgentDefinitionOverride = agentDefinition
                }
            ],
            Quantity = settings.Quantity,
            MinimumRewardRisk = settings.MinimumRewardRisk,
            CaptureMarketReplay = false,
            InlineCandles = windowCandles,
            Runtime = windowCandles is not null
                ? settings.RuntimeTemplate with { SourceKind = HistoricalDataSourceKind.InlineTestData, WarmupDays = 0 }
                : settings.RuntimeTemplate
        };

        ComparativeSimulationResult result = await backtests
            .RunToCompletionAsync(request, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        StrategySimulationResult strategy = result.Strategies.Single();
        BacktestEvaluationResult evaluated = ToBacktestEvaluationResult(strategy, attributionFrom, attributionTo);
        if (identity is not null)
            settings.Cache!.Set(identity, evaluated);
        return evaluated;
    }

    /// <summary>
    /// Builds the cache-key identity for this evaluation. Several fields are documented,
    /// deliberately simple proxies rather than the literal blueprint §12 concept, since no
    /// separate execution-model/broker-cost-model/data-quality-policy versioning system exists yet
    /// in this codebase - they are still real, stable, and deterministic (a genuine change in any
    /// of them changes the resulting hash), just not independently tracked elsewhere. Real
    /// backtests replay historical candles deterministically (no Monte Carlo sampling), so
    /// <see cref="BacktestEvaluationIdentity.RandomSeed"/> is fixed at 0 here.
    /// </summary>
    private BacktestEvaluationIdentity BuildIdentity(TOptions effective, TradingAgentDefinition agentDefinition)
    {
        DateTimeOffset streamFrom = attributionFrom.AddDays(-settings.WarmupDays);
        return new BacktestEvaluationIdentity
        {
            StrategyId = settings.StrategyType,
            StrategyImplementationHash = "n/a",
            EffectiveAgentDefinitionHash = IndicatorCalibrationHash.ComputeOfObject(agentDefinition),
            CompleteOptionsHash = IndicatorCalibrationHash.ComputeOfObject(effective),
            FeatureSwitchHash = IndicatorCalibrationHash.ComputeOfObject(new
            {
                candidateAblations = true // ablation values are already part of CompleteOptionsHash; kept as a documented, always-present field.
            }),
            Instrument = settings.Instrument.Value,
            CandleDataHash = IndicatorCalibrationHash.ComputeOfObject(new
            {
                settings.Instrument.Value,
                streamFrom,
                attributionTo
            }),
            PriceComponent = "Mid",
            WindowStart = attributionFrom,
            WindowEnd = attributionTo,
            WarmupStart = streamFrom,
            TimeframeTopologyHash = IndicatorCalibrationHash.ComputeOfObject(settings.RuntimeTemplate),
            ExecutionModelVersion = "v1",
            BrokerCostModelHash = IndicatorCalibrationHash.ComputeOfObject(new { settings.Quantity, settings.MinimumRewardRisk }),
            DataQualityPolicyVersion = "v1",
            RandomSeed = 0
        };
    }

    private static BacktestEvaluationResult ToBacktestEvaluationResult(
        StrategySimulationResult strategy, DateTimeOffset attributionFrom, DateTimeOffset attributionTo)
    {
        SimulatedTradeRecord[] attributed = strategy.Result.Trades
            .Where(trade => trade.ClosedAt is not null && trade.OpenedAt is not null &&
                trade.OpenedAt.Value >= attributionFrom && trade.OpenedAt.Value < attributionTo)
            .OrderBy(trade => trade.ClosedAt)
            .ToArray();

        decimal[] rValues = attributed
            .Where(trade => trade.RMultiple is not null)
            .Select(trade => trade.RMultiple!.Value)
            .ToArray();

        decimal grossWinsR = rValues.Where(value => value > 0m).Sum();
        decimal grossLossesR = Math.Abs(rValues.Where(value => value < 0m).Sum());
        // No losing trades in the window is not "infinite edge" - cap at a large finite sentinel so
        // the eligibility-gate comparison against MinimumProfitFactor stays well-defined.
        const decimal noLossesProfitFactorSentinel = 100m;
        decimal profitFactor = rValues.Length == 0
            ? 0m
            : grossLossesR > 0m ? grossWinsR / grossLossesR : noLossesProfitFactorSentinel;

        bool dataQualityValid = strategy.IsComplete && strategy.FailureMessage is null && strategy.FailedAtSequence is null;

        return new BacktestEvaluationResult
        {
            TradeCount = attributed.Length,
            MedianExpectancyR = Median(rValues),
            MaximumDrawdownR = MaximumDrawdown(attributed),
            ProfitFactor = profitFactor,
            DataQualityValid = dataQualityValid
        };
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
            return 0m;
        decimal[] sorted = values.OrderBy(value => value).ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
    }

    /// <summary>Peak-to-trough over the cumulative R sequence in close order - the same convention as <c>QuantResearch.Validation.MonteCarloSimulator</c>.</summary>
    private static decimal MaximumDrawdown(IReadOnlyList<SimulatedTradeRecord> orderedByClose)
    {
        decimal running = 0m, peak = 0m, drawdown = 0m;
        foreach (SimulatedTradeRecord trade in orderedByClose)
        {
            running += trade.RMultiple ?? 0m;
            peak = Math.Max(peak, running);
            drawdown = Math.Max(drawdown, peak - running);
        }
        return drawdown;
    }
}
