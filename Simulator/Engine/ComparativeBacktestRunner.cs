using Agent.Abstractions;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;
using RiskManager.Safety;
using TradingCore.MarketData;

namespace Simulator.Engine;

public sealed record StrategyBacktestResult(string StrategyName, SimulationResult Result);

/// <summary>Runs strategies in isolated broker/account sessions against the exact same candle array.</summary>
public static class ComparativeBacktestRunner
{
    public static async Task<IReadOnlyList<Candle>> LoadOnceAsync(
        IHistoricalCandleSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var candles = new List<Candle>();
        await foreach (Candle candle in source.ReadAsync(cancellationToken).ConfigureAwait(false))
            candles.Add(candle);
        return candles.OrderBy(candle => candle.OpenTime).ToArray();
    }

    public static async Task<IReadOnlyList<StrategyBacktestResult>> RunAsync(
        InstrumentKey instrument,
        IReadOnlyList<Candle> baseCandles,
        IReadOnlyList<BarInterval> analysisIntervals,
        IEnumerable<ITradingAgent> agents,
        SimulationOptions? simulationOptions = null,
        TradingSafetyOptions? safetyOptions = null,
        MarketDataQualityOptions? dataQualityOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseCandles);
        ArgumentNullException.ThrowIfNull(analysisIntervals);
        ArgumentNullException.ThrowIfNull(agents);
        var results = new List<StrategyBacktestResult>();
        foreach (ITradingAgent agent in agents)
        {
            await using SimulationSession session = SimulationFactory.CreateStrategyAwareHistorical(
                instrument,
                baseCandles,
                analysisIntervals,
                agent,
                simulationOptions,
                safetyOptions: safetyOptions,
                dataQualityOptions: dataQualityOptions);
            SimulationResult result = await session.Runner.RunAsync(cancellationToken).ConfigureAwait(false);
            results.Add(new StrategyBacktestResult(agent.Name, result));
        }
        return results;
    }
}
