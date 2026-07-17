using Agent.Abstractions;

namespace Agent.Strategies;

public enum ProgressiveAgentKind
{
    Legacy,
    Improved
}

/// <summary>
/// Builds the two concrete progressive agent types from one shared options instance - the
/// reusable "same construction, same options" piece a live host will need alongside the
/// simulator, decoupled from any simulator-specific request/assignment shape.
/// </summary>
public static class ProgressiveAgentFactory
{
    public static ITradingAgent Create(ProgressiveAgentKind kind, ProgressiveStrategyOptions options) => kind switch
    {
        ProgressiveAgentKind.Legacy => new LegacyProgressiveAgent(options),
        ProgressiveAgentKind.Improved => new ImprovedProgressiveAgent(options),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
