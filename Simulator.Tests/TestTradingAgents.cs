using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;

namespace Simulator.Tests;

internal sealed class RecordingTradingAgent(
    IEnumerable<BarInterval> requiredIntervals,
    BarInterval triggerInterval,
    Func<AgentMarketContext, AgentDecision>? decide = null) : ITradingAgent
{
    private readonly Func<AgentMarketContext, AgentDecision> _decide = decide ?? Observe;

    public string Name => "Recording test agent";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; } = requiredIntervals.ToHashSet();
    public BarInterval TriggerInterval { get; } = triggerInterval;
    public AgentExitManagementMode ExitManagementMode { get; init; } = AgentExitManagementMode.Bracket;
    public List<AgentMarketContext> Contexts { get; } = [];

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Contexts.Add(context);
        return Task.FromResult(_decide(context));
    }

    private static AgentDecision Observe(AgentMarketContext context) => new()
    {
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = "Observe"
    };
}
