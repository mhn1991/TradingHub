using Agent.Models;
using Brokers.Models;
using PortfolioManager.Risk;
using RiskManager;
using LiveTrading.Agents;

namespace LiveTrading.Portfolio;

public sealed record LiveRiskBudgetAudit
{
    public required decimal BaseRiskAmount { get; init; }
    public required decimal SetupMultiplier { get; init; }
    public required decimal MetaLabelMultiplier { get; init; }
    public decimal NeoWaveMultiplier { get; init; } = 1m;
    public required decimal RegimeMultiplier { get; init; }
    public required decimal TradingConditionMultiplier { get; init; }
    public required decimal DrawdownMultiplier { get; init; }
    public required decimal VolatilityMultiplier { get; init; }
    public required decimal LiquidityMultiplier { get; init; }
    public required decimal CorrelationMultiplier { get; init; }
    public required decimal StrategyMultiplier { get; init; }
    public required decimal EquityProtectionMultiplier { get; init; }
    public required decimal CombinedMultiplier { get; init; }
    public required decimal FinalRiskAmount { get; init; }
    public required string ReasonCode { get; init; }
}

public sealed record LivePositionSizingAudit
{
    public required decimal RequestedRisk { get; init; }
    public required decimal RawQuantity { get; init; }
    public required decimal BrokerNormalizedQuantity { get; init; }
    public required decimal EstimatedStopLoss { get; init; }
    public required decimal EstimatedMargin { get; init; }
    public required decimal ExpectedCosts { get; init; }
    public required string QuoteConversionPath { get; init; }
    public required string ReasonCode { get; init; }
}

public sealed record LivePortfolioSnapshot
{
    public required AccountSnapshot Account { get; init; }
    public required decimal Equity { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    public required IReadOnlyList<BrokerOrder> Orders { get; init; }
    public required PortfolioReservationSnapshot Reservations { get; init; }
    public decimal CurrentOpenRiskAccountCurrency { get; init; }
    public IReadOnlyDictionary<string, decimal> OpenStrategyRisk { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
    public IReadOnlyDictionary<InstrumentKey, decimal> OpenInstrumentRisk { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
    public IReadOnlyDictionary<string, decimal> OpenCurrencyRisk { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}

public sealed record LiveOpportunityEvaluationContext
{
    public required LivePortfolioSnapshot Portfolio { get; init; }
    public required Func<LiveTradeCandidate, LiveTradingPolicyBundle> PolicyResolver { get; init; }
    public IReadOnlyDictionary<InstrumentKey, decimal> QuoteToAccountCurrencyRates { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
    public IReadOnlyDictionary<InstrumentKey, InstrumentRiskSpec> InstrumentRiskSpecs { get; init; } =
        new Dictionary<InstrumentKey, InstrumentRiskSpec>();
    public IReadOnlyDictionary<InstrumentKey, InstrumentTradingMetadata> InstrumentMetadata { get; init; } =
        new Dictionary<InstrumentKey, InstrumentTradingMetadata>();
    public bool RequireInstrumentMetadata { get; init; }
    public decimal DrawdownPercent { get; init; }
    public IReadOnlyDictionary<InstrumentKey, decimal> VolatilityPercentiles { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
    public IReadOnlyDictionary<InstrumentKey, decimal> LiquidityMultipliers { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
    public IReadOnlyDictionary<InstrumentKey, decimal> CorrelationMultipliers { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
    public IReadOnlyDictionary<string, decimal> StrategyMultipliers { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
    public IReadOnlyDictionary<InstrumentKey, decimal> EquityProtectionMultipliers { get; init; } =
        new Dictionary<InstrumentKey, decimal>();
}

public sealed record PortfolioApprovedDecision
{
    public required LiveTradeCandidate Candidate { get; init; }
    public required AgentDecision Decision { get; init; }
    public required string ReservationId { get; init; }
    public required decimal PortfolioScore { get; init; }
    public required LiveRiskBudgetAudit RiskBudget { get; init; }
    public required LivePositionSizingAudit PositionSizing { get; init; }
    public required Guid PolicyBundleId { get; init; }
    public required int PolicyRevision { get; init; }
    public required string ConfigurationHash { get; init; }
}

public sealed record PortfolioDecision
{
    public required LiveTradeCandidate Candidate { get; init; }
    public required bool Approved { get; init; }
    public PortfolioApprovedDecision? ApprovedDecision { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

public interface ILiveOpportunityCoordinator
{
    Task<IReadOnlyList<PortfolioDecision>> EvaluateEpochAsync(
        IReadOnlyList<LiveTradeCandidate> candidates,
        LiveOpportunityEvaluationContext context,
        CancellationToken cancellationToken);
}
