using TradingHub.Application.Orders;
using TradingHub.Application.Portfolio;
using TradingHub.Application.Risk;
using TradingHub.Domain.Trading;

namespace TradingHub.Simulation.Results;

public sealed record SimulationSummary
{
    public required decimal InitialBalance { get; init; }

    public required decimal FinalBalance { get; init; }

    public required decimal FinalEquity { get; init; }

    public required decimal RealizedPnl { get; init; }

    public required decimal FeesPaid { get; init; }

    public required decimal MaximumDrawdownPercent { get; init; }

    public required int IntentCount { get; init; }

    public required int ApprovedIntentCount { get; init; }

    public required int FillCount { get; init; }
}

public sealed record SimulationResult
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    public required SimulationSummary Summary { get; init; }

    public required IReadOnlyList<TradeIntent> Intents { get; init; }

    public required IReadOnlyList<RiskEvaluation> RiskEvaluations { get; init; }

    public required IReadOnlyList<OrderSubmissionRecord> Orders { get; init; }

    public required IReadOnlyList<ExecutionReport> Executions { get; init; }

    public required IReadOnlyList<EquityPoint> EquityCurve { get; init; }
}
