using TradingPolicies;

namespace LiveTradingHost.Configuration;

/// <summary>
/// Deployment binding for one immutable, environment-neutral trading policy. The live host does
/// not expose a second set of Agent, indicator, risk, portfolio, condition, or management options.
/// Those settings are promoted from simulator/research as one <see cref="TradingPolicyProfile"/>
/// and consumed unchanged. Only market assignment and activation mode remain deployment concerns.
/// </summary>
public sealed record LivePolicyBundleOptions
{
    public required TradingPolicyProfile Profile { get; init; }

    /// <summary>
    /// Optional deployment note. It cannot change behaviour and is intentionally excluded from
    /// the profile configuration hash.
    /// </summary>
    public string? DeploymentDescription { get; init; }
}
