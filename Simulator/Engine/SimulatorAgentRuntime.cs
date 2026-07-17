using Simulator.Models;
using TradingCore.Pipeline;

namespace Simulator.Engine;

/// <summary>
/// Thin <see cref="IAgentRuntime"/> wrapper around an existing, unmodified
/// <see cref="StrategyWorkerHost"/> mailbox - no new isolation mechanism, since
/// <see cref="StrategyWorkerHost"/> already matches the interface's serialization guarantee
/// (single persistent reader task, sequential frame processing).
/// </summary>
/// <remarks>
/// <see cref="EvaluateAsync"/> deliberately throws: a simulator strategy evaluation needs the raw
/// execution-interval <c>MarketCandle</c> for fill simulation (spread/slippage/OCO resolution),
/// which - unlike the live host, where a real broker owns fills and <see cref="MarketAnalysisSnapshot"/>'s
/// per-timeframe <c>AnalysisSnapshot</c>s are everything <c>SafeTradingPipeline</c> needs -
/// <see cref="MarketAnalysisSnapshot"/> never carries by design (ChartAnnotator only ever sees
/// completed analysis candles, never the raw sub-analysis-interval execution stream). The
/// simulator's real, fully-functional per-frame path is <see cref="EnqueueFrameAsync"/>, which
/// delegates to the wrapped <see cref="StrategyWorkerHost"/> unchanged - this is what
/// <c>StreamingComparativeEngine</c> uses today and continues to use through Phase 6. This
/// runtime exists so the simulator side has one place to expose <see cref="IAgentRuntime"/>'s
/// Key/AnalysisProfile/Mode surface uniformly with the live side, not because the shared
/// analysis-snapshot-only evaluation shape is a good fit for simulated fills.
/// </remarks>
public sealed class SimulatorAgentRuntime : IAgentRuntime
{
    private readonly StrategyWorkerHost _host;

    public SimulatorAgentRuntime(StrategyWorkerHost host, AnalysisProfileKey analysisProfile, AgentExecutionMode mode)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        AnalysisProfile = analysisProfile ?? throw new ArgumentNullException(nameof(analysisProfile));
        Mode = mode;
    }

    public AgentInstanceKey Key => _host.Key;
    public AnalysisProfileKey AnalysisProfile { get; }
    public AgentExecutionMode Mode { get; }

    public Task<AgentRuntimeResult> EvaluateAsync(
        MarketAnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            $"{nameof(SimulatorAgentRuntime)} cannot evaluate from a {nameof(MarketAnalysisSnapshot)} alone - " +
            "simulated fills need the raw execution-interval candle, which is deliberately outside " +
            $"{nameof(MarketAnalysisSnapshot)}'s scope. Use {nameof(EnqueueFrameAsync)} with a real " +
            $"{nameof(MarketFrame)} instead - the same path {nameof(StreamingComparativeEngine)} already drives.");
    }

    /// <summary>The simulator's real per-frame evaluation path - delegates to the wrapped,
    /// unmodified <see cref="StrategyWorkerHost.EnqueueAsync"/>.</summary>
    public ValueTask<Task<StrategyFrameResult>> EnqueueFrameAsync(
        MarketFrame frame, CancellationToken cancellationToken = default) =>
        _host.EnqueueAsync(frame, cancellationToken);
}
