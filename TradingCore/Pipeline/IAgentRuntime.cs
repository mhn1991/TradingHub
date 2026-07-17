namespace TradingCore.Pipeline;

/// <summary>
/// Shared serialization contract every isolated Agent runtime implements, one per registered
/// <see cref="AgentInstanceKey"/>. Deliberately shallow (foundational decision #5 - no shared
/// concrete class across Simulator/Live): <c>Simulator.Engine.SimulatorAgentRuntime</c> wraps the
/// existing <c>StrategyWorkerHost</c> mailbox unchanged, <c>LiveTrading.Agents.LiveAgentRuntime</c>
/// wraps a per-runtime <c>SemaphoreSlim(1,1)</c> around the evaluation body extracted from
/// <c>AgentSupervisor</c> - the two pipelines they drive (<c>StrategySimulationSession.ProcessFrameAsync</c>
/// vs. <c>SafeTradingPipeline.ProcessAsync</c>) stay untouched. Every implementation guarantees
/// its own state transitions are serialized (single mailbox reader / single-holder semaphore), so
/// a caller can invoke <see cref="EvaluateAsync"/> on many different runtimes concurrently without
/// any one runtime's own state ever observing interleaved calls.
/// </summary>
public interface IAgentRuntime
{
    AgentInstanceKey Key { get; }
    AnalysisProfileKey AnalysisProfile { get; }
    AgentExecutionMode Mode { get; }

    Task<AgentRuntimeResult> EvaluateAsync(
        MarketAnalysisSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
