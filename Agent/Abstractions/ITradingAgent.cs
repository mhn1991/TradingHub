using Agent.Models;
using Brokers.Models;

namespace Agent.Abstractions;

public interface ITradingAgent
{
    string Name { get; }
    IReadOnlySet<BarInterval> RequiredIntervals { get; }
    BarInterval TriggerInterval { get; }

    Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default);
}
