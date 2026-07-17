namespace LiveTrading.Configuration;

/// <summary>
/// Deployment-level disposition for a strategy instance. This is intentionally outside the
/// Agent project and is never passed into an Agent, TradeManager, RiskManager, PortfolioManager,
/// or ExecutionManager. Those components receive only normalized data, decisions, and policy.
/// </summary>
public enum StrategyActivationMode
{
    ObserveOnly,
    Shadow,
    ManualApproval,
    Automatic
}
