using Brokers.Models;

namespace LiveTrading.Agents;

/// <summary>
/// Stable identity for one runtime Agent instance. Widened (multi-agent architecture Phase 2)
/// from the original <c>(Instrument, StrategyId)</c> pair to also carry policy-revision identity,
/// so two Agents sharing one <see cref="StrategyId"/> on one <see cref="Instrument"/> - e.g. an
/// "improved, stricter confirmation, executable" instance alongside an "improved, softer
/// confirmation, shadow-only" instance running the same strategy type under two different
/// promoted policy revisions - no longer collide in <c>AgentSupervisor._instances</c>.
/// </summary>
/// <param name="DeploymentId">
/// No multi-deployment concept exists elsewhere in this codebase yet (single live host process
/// per broker account today) - this is a fixed configuration value for now, carried for identity
/// shape parity so a later genuine multi-deployment model does not require a second breaking
/// change to every <c>Dictionary&lt;AgentInstanceKey, ...&gt;</c> call site.
/// </param>
/// <param name="Instrument">The traded instrument this Agent instance is assigned to.</param>
/// <param name="StrategyId">The deployed strategy slot identifier (not the underlying Agent kind).</param>
/// <param name="PolicyBundleId">
/// Matches <c>LiveTradingPolicyBundle.PolicyBundleId</c> exactly - deliberately two fields
/// (<see cref="PolicyBundleId"/> + <see cref="Revision"/>) rather than a single synthetic
/// <c>Guid</c>, since the policy-bundle/revision pair already exists everywhere in this
/// codebase's policy-lineage tracking (<c>LivePolicyRegistry._history</c>,
/// <c>LivePositionRecord.PolicyBundleId</c>/<c>PolicyRevision</c>) and a synthesized hash would
/// have no other purpose.
/// </param>
/// <param name="Revision">Matches <c>LiveTradingPolicyBundle.Revision</c> exactly.</param>
public sealed record AgentInstanceKey(
    string DeploymentId,
    InstrumentKey Instrument,
    string StrategyId,
    Guid PolicyBundleId,
    int Revision)
{
    /// <summary>Fixed value for the single-deployment-per-process model this phase supports.</summary>
    public const string DefaultDeploymentId = "live-practice";
}
