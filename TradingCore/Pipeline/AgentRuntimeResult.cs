using Agent.Models;

namespace TradingCore.Pipeline;

/// <summary>
/// Thin, environment-neutral envelope around whatever result type an <see cref="IAgentRuntime"/>
/// implementation actually produced. <c>TradingCore</c> cannot reference <c>Simulator.Models</c>
/// or <c>LiveTrading.Agents</c> (both depend on <c>TradingCore</c>, not the reverse), so
/// <see cref="Native"/> is deliberately untyped here - <c>SimulatorAgentRuntime</c> populates it
/// with a <c>Simulator.Models.StrategyFrameResult</c>, <c>LiveAgentRuntime</c> with a
/// <see cref="TradingPipelineResult"/>. Callers that need environment-specific detail cast it
/// themselves; every field a caller needs without knowing which environment produced the result
/// is promoted to a real property below instead.
/// </summary>
public sealed record AgentRuntimeResult
{
    public required AgentInstanceKey Key { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required long SnapshotVersion { get; init; }
    public AgentDecision? Decision { get; init; }
    public Exception? Failure { get; init; }
    public required object Native { get; init; }
}
