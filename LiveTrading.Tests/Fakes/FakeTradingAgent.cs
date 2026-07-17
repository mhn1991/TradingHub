using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;

namespace LiveTrading.Tests.Fakes;

/// <summary>Fully controllable <see cref="ITradingAgent"/> test double - returns whatever <see
/// cref="Decide"/> produces for a given evaluation, with a call counter so tests can assert how
/// many times the agent supervisor actually triggered an evaluation.</summary>
public sealed class FakeTradingAgent : ITradingAgent
{
    public string Name { get; init; } = "fake-agent";
    public required IReadOnlySet<BarInterval> RequiredIntervals { get; init; }
    public required BarInterval TriggerInterval { get; init; }
    public AgentExitManagementMode ExitManagementMode { get; init; } = AgentExitManagementMode.Bracket;
    public required Func<AgentMarketContext, AgentDecision> Decide { get; init; }

    public int EvaluationCount { get; private set; }

    public Task<AgentDecision> EvaluateAsync(AgentMarketContext context, CancellationToken cancellationToken = default)
    {
        EvaluationCount++;
        return Task.FromResult(Decide(context));
    }
}
