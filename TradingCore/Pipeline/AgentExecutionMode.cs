namespace TradingCore.Pipeline;

/// <summary>
/// Shared, environment-neutral execution disposition exposed by <see cref="IAgentRuntime"/>.
/// Deliberately narrower than <c>LiveTrading.Configuration.StrategyActivationMode</c> (which
/// distinguishes <c>ObserveOnly</c>/<c>Shadow</c>/<c>ManualApproval</c>/<c>Automatic</c> for the
/// live host's own approval workflow) - this type only needs to answer the one question every
/// portfolio-admission caller on either side actually asks: "can this runtime's candidates ever
/// compete for capital." <c>LiveAgentRuntime</c> maps its richer live mode down to this one;
/// <c>Simulator.Models.StrategyInstrumentAssignment.Mode</c> (Phase 5) is declared directly in
/// these terms, since the simulator has no separate approval-workflow concept.
/// </summary>
public enum AgentExecutionMode
{
    /// <summary>Produces candidates for observability/comparison only - never eligible for
    /// portfolio admission.</summary>
    Shadow,

    /// <summary>Eligible for portfolio admission. At most one <c>Executable</c> runtime may be
    /// registered per instrument (mirrors the live host's existing "one owner per instrument"
    /// rule) - actual admission wiring is Phase 7, deferred.</summary>
    Executable
}
