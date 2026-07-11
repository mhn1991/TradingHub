using TradingHub.Application.Trading;
using TradingHub.Abstractions.MarketData;
using TradingHub.Domain.Markets;
using TradingHub.Simulation.Broker;
using TradingHub.Simulation.Results;

namespace TradingHub.Simulation.Engine;

public sealed class SimulationEngine
{
    private const int ExecutionPhase = 100;
    private const int StrategyPhase = 200;

    private readonly SimulationEventQueue _eventQueue;
    private readonly TradingSession _session;
    private readonly SimulatedBroker _broker;

    public SimulationEngine(
        SimulationEventQueue eventQueue,
        TradingSession session,
        SimulatedBroker broker)
    {
        _eventQueue = eventQueue;
        _session = session;
        _broker = broker;
    }

    public async Task<SimulationResult> RunAsync(
        string runId,
        IMarketDataSource marketDataSource,
        CancellationToken cancellationToken = default)
    {
        var bars = await LoadBarsAsync(marketDataSource, cancellationToken);
        if (bars.Count == 0)
        {
            throw new InvalidOperationException("The simulation data source returned no bars.");
        }

        ScheduleBars(bars);
        await _eventQueue.RunAsync(cancellationToken);
        return CreateResult(runId, bars);
    }

    private static async Task<IReadOnlyList<PriceBar>> LoadBarsAsync(
        IMarketDataSource marketDataSource,
        CancellationToken cancellationToken)
    {
        var bars = new List<PriceBar>();
        await foreach (var bar in marketDataSource.ReadAsync(cancellationToken))
        {
            bars.Add(bar);
        }

        return bars;
    }

    private void ScheduleBars(IReadOnlyList<PriceBar> bars)
    {
        foreach (var bar in bars)
        {
            _eventQueue.Schedule(
                bar.CloseTime,
                ExecutionPhase,
                $"execute:{bar.InstrumentId}",
                cancellationToken => ProcessExecutionsAsync(bar, cancellationToken));
            _eventQueue.Schedule(
                bar.CloseTime,
                StrategyPhase,
                $"strategy:{bar.InstrumentId}",
                cancellationToken => _session.OnBarClosedAsync(bar, cancellationToken));
        }
    }

    private Task ProcessExecutionsAsync(PriceBar bar, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reports = _broker.ProcessBar(bar);
        _session.ApplyExecutionReports(reports);
        return Task.CompletedTask;
    }

    private SimulationResult CreateResult(string runId, IReadOnlyList<PriceBar> bars)
    {
        var initialBalance = _session.EquityCurve.First().Balance;
        var finalPoint = _session.EquityCurve.Last();
        return new SimulationResult
        {
            RunId = runId,
            StartedAt = bars.Min(bar => bar.OpenTime),
            EndedAt = bars.Max(bar => bar.CloseTime),
            Summary = new SimulationSummary
            {
                InitialBalance = initialBalance,
                FinalBalance = finalPoint.Balance,
                FinalEquity = finalPoint.Equity,
                RealizedPnl = _session.Portfolio.RealizedPnl,
                FeesPaid = _session.Portfolio.FeesPaid,
                MaximumDrawdownPercent = CalculateMaximumDrawdown(_session.EquityCurve),
                IntentCount = _session.Intents.Count,
                ApprovedIntentCount = _session.RiskEvaluations.Count(item => item.Decision.IsApproved),
                FillCount = _session.Executions.Count(item => item.LastFillQuantity > 0m)
            },
            Intents = _session.Intents.ToArray(),
            RiskEvaluations = _session.RiskEvaluations.ToArray(),
            Orders = _session.Orders.ToArray(),
            Executions = _session.Executions.ToArray(),
            EquityCurve = _session.EquityCurve.ToArray()
        };
    }

    private static decimal CalculateMaximumDrawdown(IReadOnlyList<Application.Portfolio.EquityPoint> curve)
    {
        var peak = 0m;
        var maximumDrawdown = 0m;
        foreach (var point in curve)
        {
            peak = Math.Max(peak, point.Equity);
            if (peak > 0m)
            {
                maximumDrawdown = Math.Max(maximumDrawdown, (peak - point.Equity) / peak * 100m);
            }
        }

        return maximumDrawdown;
    }
}
