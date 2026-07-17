using Agent.Abstractions;

namespace TradingCore.Pipeline;

/// <summary>
/// Identity plus a fully-constructed agent instance. Deliberately agnostic to how the agent was
/// built (options-driven, a test double, or anything else) so this type is equally usable by the
/// simulator today and a live host later.
/// </summary>
public sealed record StrategyRuntimeDefinition
{
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required ITradingAgent Agent { get; init; }
}
