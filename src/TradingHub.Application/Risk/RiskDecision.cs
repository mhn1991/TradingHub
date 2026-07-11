namespace TradingHub.Application.Risk;

public sealed record RiskDecision
{
    public required bool IsApproved { get; init; }

    public required string Code { get; init; }

    public required string Reason { get; init; }
}

public sealed record RiskEvaluation
{
    public required Domain.Trading.TradeIntent Intent { get; init; }

    public required RiskDecision Decision { get; init; }
}
