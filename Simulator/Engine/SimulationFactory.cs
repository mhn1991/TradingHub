using Agent.Abstractions;
using ExecutionManager;
using TradingJournal;
using RiskManager.Safety;
using TradingCore.MarketData;
using Brokers.Models;
using Brokers.Safety;
using RiskManager;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using Simulator.Abstractions;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Engine;

public sealed record SimulationSession(
    SimulationRunner Runner,
    SimulatedBrokerClient Broker,
    IChartAnnotator Annotator,
    IAdjustableSimulationClock Clock) : IAsyncDisposable
{
    public ITradingSafetyController? Safety { get; init; }
    public IMarketDataQualityGate? DataQuality { get; init; }
    public ITradeJournal? Journal { get; init; }

    public ValueTask DisposeAsync() => Broker.DisposeAsync();
}

public static class SimulationFactory
{
    public static SimulationSession CreateHistorical(
        InstrumentKey instrument,
        IEnumerable<Candle> baseCandles,
        IEnumerable<BarInterval> analysisIntervals,
        ITradingAgent agent,
        SimulationOptions? simulationOptions = null,
        ChartAnnotationOptions? annotationOptions = null,
        IExecutionCoordinator? executionCoordinator = null,
        IMarketDataQualityGate? dataQuality = null,
        ITradingSafetyController? safety = null,
        ITradeJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(baseCandles);
        ArgumentNullException.ThrowIfNull(analysisIntervals);
        ArgumentNullException.ThrowIfNull(agent);

        SimulationOptions options = simulationOptions ?? new SimulationOptions();
        var clock = new HistoricalSimulationClock();
        var broker = new SimulatedBrokerClient(options, clock);
        try
        {
            var annotator = new ChartAnnotationEngine(annotationOptions);
            var aggregator = new MultiTimeframeAggregator(
                instrument,
                analysisIntervals,
                options.CandleCapacity,
                options.BaseCandleGapPolicy);
            var source = new EnumerableCandleSource(baseCandles);
            IMarketDataQualityGate qualityGate = dataQuality ?? new MarketDataQualityGate();
            ITradingSafetyController safetyController = safety ?? new TradingSafetyController();
            // Not options.LedgerCapacity: the journal is a much higher-frequency stream (once
            // per signal evaluation) than the financial ledger that capacity was sized for -
            // see the matching comment in StrategySimulationSession.Create.
            ITradeJournal tradeJournal = journal ?? new InMemoryTradeJournal(Math.Max(options.LedgerCapacity, 200_000));
            IExecutionCoordinator execution = executionCoordinator ?? new ExecutionCoordinator(
                safety: safetyController,
                journal: tradeJournal);
            var runner = new SimulationRunner(
                source,
                clock,
                aggregator,
                annotator,
                agent,
                execution,
                broker,
                qualityGate,
                safetyController,
                tradeJournal);

            return new SimulationSession(runner, broker, annotator, clock)
            {
                Safety = safetyController,
                DataQuality = qualityGate,
                Journal = tradeJournal
            };
        }
        catch
        {
            // SimulatedBrokerClient disposal is synchronous today; do not introduce
            // sync-over-async into this synchronous compatibility factory.
            _ = broker.DisposeAsync();
            throw;
        }
    }


    /// <summary>
    /// Creates a conservative simulation while respecting how the strategy exits.
    /// Bracket strategies must provide both stop and target; reverse-exit strategies
    /// must provide a protective stop but may intentionally omit a fixed target.
    /// </summary>
    public static SimulationSession CreateStrategyAwareHistorical(
        InstrumentKey instrument,
        IEnumerable<Candle> baseCandles,
        IEnumerable<BarInterval> analysisIntervals,
        ITradingAgent agent,
        SimulationOptions? simulationOptions = null,
        ChartAnnotationOptions? annotationOptions = null,
        TradingSafetyOptions? safetyOptions = null,
        MarketDataQualityOptions? dataQualityOptions = null,
        ExecutionOptions? executionOptions = null,
        BrokerExecutionSafetyOptions? brokerSafetyOptions = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        PreTradeRiskOptions riskOptions = agent.ExitManagementMode switch
        {
            AgentExitManagementMode.ProtectiveStopAndStrategyExit => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = false,
                MinimumRewardRiskRatio = null,
                MaximumOpenPositions = 1,
                MaximumLossPercentageOfBalance = null,
                MaximumOpenRiskPercentOfEquity = null,
                AllowPyramiding = false
            },
            AgentExitManagementMode.Bracket => new PreTradeRiskOptions
            {
                RequireStopLoss = true,
                RequireTakeProfit = true,
                MinimumRewardRiskRatio = PreTradeRiskOptions.PhaseOneSafeDefaults.MinimumRewardRiskRatio,
                MaximumOpenPositions = 1,
                MaximumLossPercentageOfBalance = null,
                MaximumOpenRiskPercentOfEquity = null,
                AllowPyramiding = false
            },
            _ => PreTradeRiskOptions.PhaseOneSafeDefaults with
            {
                MaximumLossPercentageOfBalance = null,
                MaximumOpenRiskPercentOfEquity = null
            }
        };

        return CreatePhaseOneSafeHistorical(
            instrument,
            baseCandles,
            analysisIntervals,
            agent,
            simulationOptions,
            annotationOptions,
            safetyOptions,
            dataQualityOptions,
            executionOptions,
            riskOptions,
            brokerSafetyOptions);
    }

    /// <summary>
    /// Creates a simulation with conservative Phase 1 entry controls enabled.
    /// Strategies used with this factory must provide reference, stop-loss, and take-profit prices.
    /// </summary>
    public static SimulationSession CreatePhaseOneSafeHistorical(
        InstrumentKey instrument,
        IEnumerable<Candle> baseCandles,
        IEnumerable<BarInterval> analysisIntervals,
        ITradingAgent agent,
        SimulationOptions? simulationOptions = null,
        ChartAnnotationOptions? annotationOptions = null,
        TradingSafetyOptions? safetyOptions = null,
        MarketDataQualityOptions? dataQualityOptions = null,
        ExecutionOptions? executionOptions = null,
        PreTradeRiskOptions? riskOptions = null,
        BrokerExecutionSafetyOptions? brokerSafetyOptions = null)
    {
        SimulationOptions resolvedSimulationOptions = simulationOptions ?? new SimulationOptions();
        TradingSafetyOptions resolvedSafetyOptions = safetyOptions ?? new TradingSafetyOptions
        {
            MaximumDailyLoss = resolvedSimulationOptions.StartingBalance * 0.01m,
            MaximumWeeklyLoss = resolvedSimulationOptions.StartingBalance * 0.03m,
            MaximumConsecutiveLosses = 3
        };
        MarketDataQualityOptions resolvedDataQualityOptions = dataQualityOptions ??
            new MarketDataQualityOptions { RequireIndicatorsReady = true };
        var safety = new TradingSafetyController(resolvedSafetyOptions);
        var journal = new InMemoryTradeJournal(Math.Max(resolvedSimulationOptions.LedgerCapacity, 200_000));
        var dataQuality = new MarketDataQualityGate(resolvedDataQualityOptions);
        var execution = new ExecutionCoordinator(
            executionOptions,
            new PreTradeRiskManager(riskOptions ?? PreTradeRiskOptions.PhaseOneSafeDefaults),
            new BrokerExecutionSafety(brokerSafetyOptions),
            safety,
            journal);

        return CreateHistorical(
            instrument,
            baseCandles,
            analysisIntervals,
            agent,
            resolvedSimulationOptions,
            annotationOptions,
            execution,
            dataQuality,
            safety,
            journal);
    }
}
