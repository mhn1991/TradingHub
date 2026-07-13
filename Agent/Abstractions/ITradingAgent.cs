using Agent.Models;
using Brokers.Models;

namespace Agent.Abstractions;

public enum AgentExitManagementMode
{
    Bracket,
    ProtectiveStopAndStrategyExit
}

public interface ITradingAgent
{
    string Name { get; }

    IReadOnlySet<BarInterval> RequiredIntervals { get; }

    BarInterval TriggerInterval { get; }

    /// <summary>
    /// Explicit exit model. No default — every strategy must declare whether it is
    /// a bracket (stop+target) or protective-stop + strategy-exit strategy.
    /// </summary>
    AgentExitManagementMode ExitManagementMode { get; }

    Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default);
}
