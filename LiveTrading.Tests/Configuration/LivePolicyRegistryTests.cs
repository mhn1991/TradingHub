using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Tests.Phase3;
using NUnit.Framework;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Configuration;

/// <summary>
/// Covers policy revision isolation and the Phase 3 hot-swap/position-continuity contract:
/// <see cref="LivePolicyRegistry.Register"/> retains simultaneous exact Agent revisions,
/// <see cref="LivePolicyRegistry.Activate"/> is
/// the separate path that changes what new candidates resolve, and
/// <see cref="LivePolicyRegistry.TryResolveManagementByRevision"/> keeps every prior revision
/// permanently retrievable so an already-open position can keep being managed under its own
/// original policy after a swap.
///
/// AGENT identity note (multi-agent architecture Phase 2): <see cref="AgentInstanceKey"/> now
/// carries PolicyBundleId/Revision directly, so each distinct revision needs its own key value
/// (constructed via <see cref="KeyFor"/>) even though "current" resolution (<see cref="LivePolicyRegistry.Resolve(string,Brokers.Models.InstrumentKey)"/>)
/// is keyed only by the narrower deployment/instrument/strategy slot, independent of revision.
/// </summary>
[TestFixture]
public sealed class LivePolicyRegistryTests
{
    private const string StrategyId = "improved";

    private static AgentInstanceKey KeyFor(LiveTradingPolicyBundle policy) =>
        new(AgentInstanceKey.DefaultDeploymentId, Phase3TestData.Instrument, StrategyId, policy.PolicyBundleId, policy.Revision);

    [Test]
    public void Register_DifferentAgentRevisions_CoexistAndResolveExactly()
    {
        var registry = new LivePolicyRegistry();
        LiveTradingPolicyBundle revisionOne = Phase3TestData.Policy() with { Revision = 1, ConfigurationHash = "hash-r1" };
        LiveTradingPolicyBundle revisionTwo = Phase3TestData.Policy() with { Revision = 2, ConfigurationHash = "hash-r2" };

        registry.Register(KeyFor(revisionOne), revisionOne, StrategyActivationMode.Shadow);

        registry.Register(KeyFor(revisionTwo), revisionTwo, StrategyActivationMode.Shadow);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Snapshot, Has.Count.EqualTo(2));
            Assert.That(registry.Resolve(StrategyId, Phase3TestData.Instrument).Revision, Is.EqualTo(2));
            Assert.That(registry.TryResolveManagementByRevision(
                StrategyId, Phase3TestData.Instrument, revisionOne.PolicyBundleId, 1), Is.Not.Null);
            Assert.That(registry.TryResolveManagementByRevision(
                StrategyId, Phase3TestData.Instrument, revisionTwo.PolicyBundleId, 2), Is.Not.Null);
        });
    }

    [Test]
    public void Register_SameRevisionTwice_IsIdempotent()
    {
        var registry = new LivePolicyRegistry();
        LiveTradingPolicyBundle policy = Phase3TestData.Policy() with { Revision = 1, ConfigurationHash = "hash-r1" };

        registry.Register(KeyFor(policy), policy, StrategyActivationMode.Shadow);
        Assert.DoesNotThrow(() => registry.Register(KeyFor(policy), policy, StrategyActivationMode.Shadow));
    }

    [Test]
    public void Activate_ChangesCurrentResolution_ButKeepsOldRevisionResolvableByRevision()
    {
        var registry = new LivePolicyRegistry();
        LiveTradingPolicyBundle revisionOne = Phase3TestData.Policy() with { Revision = 1, ConfigurationHash = "hash-r1" };
        LiveTradingPolicyBundle revisionTwo = Phase3TestData.Policy() with { Revision = 2, ConfigurationHash = "hash-r2" };
        registry.Register(KeyFor(revisionOne), revisionOne, StrategyActivationMode.Shadow);

        // A position "opened" under revision 1 remembers PolicyBundleId/Revision at this point.
        Guid policyBundleId = revisionOne.PolicyBundleId;

        AgentInstanceKey activatedKey = KeyFor(revisionTwo);
        registry.Activate(activatedKey, new LivePolicyRegistration
        {
            Key = activatedKey,
            Policy = revisionTwo,
            Mode = StrategyActivationMode.Shadow
        });

        // New candidates resolve the newly-activated revision - "current" resolution is keyed by
        // the deployment/instrument/strategy slot only, independent of which revision is active.
        Assert.That(registry.Resolve(StrategyId, Phase3TestData.Instrument).Revision, Is.EqualTo(2));
        Assert.That(registry.ResolveManagement(StrategyId, Phase3TestData.Instrument).Policy.Revision, Is.EqualTo(2));

        // The position's own original revision remains fully resolvable.
        LiveManagementPolicy? own = registry.TryResolveManagementByRevision(
            StrategyId, Phase3TestData.Instrument, policyBundleId, revision: 1);
        Assert.That(own, Is.Not.Null);
        Assert.That(own!.Policy.Revision, Is.EqualTo(1));
        Assert.That(own.Policy.ConfigurationHash, Is.EqualTo("hash-r1"));
    }

    [Test]
    public void TryResolveManagementByRevision_UnknownRevision_ReturnsNullRatherThanThrowing()
    {
        var registry = new LivePolicyRegistry();
        LiveTradingPolicyBundle policy = Phase3TestData.Policy() with { Revision = 1, ConfigurationHash = "hash-r1" };
        registry.Register(KeyFor(policy), policy, StrategyActivationMode.Shadow);

        LiveManagementPolicy? result = registry.TryResolveManagementByRevision(
            StrategyId, Phase3TestData.Instrument, Guid.NewGuid(), revision: 99);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Activate_NeverRemovesEarlierRevisionsFromHistory_AcrossMultipleSwaps()
    {
        var registry = new LivePolicyRegistry();
        LiveTradingPolicyBundle r1 = Phase3TestData.Policy() with { Revision = 1, ConfigurationHash = "hash-r1" };
        LiveTradingPolicyBundle r2 = Phase3TestData.Policy() with { Revision = 2, ConfigurationHash = "hash-r2" };
        LiveTradingPolicyBundle r3 = Phase3TestData.Policy() with { Revision = 3, ConfigurationHash = "hash-r3" };
        registry.Register(KeyFor(r1), r1, StrategyActivationMode.Shadow);
        registry.Activate(KeyFor(r2), new LivePolicyRegistration { Key = KeyFor(r2), Policy = r2, Mode = StrategyActivationMode.Shadow });
        registry.Activate(KeyFor(r3), new LivePolicyRegistration { Key = KeyFor(r3), Policy = r3, Mode = StrategyActivationMode.Shadow });

        Assert.Multiple(() =>
        {
            Assert.That(registry.Resolve(StrategyId, Phase3TestData.Instrument).Revision, Is.EqualTo(3));
            Assert.That(registry.TryResolveManagementByRevision(StrategyId, Phase3TestData.Instrument, r1.PolicyBundleId, 1), Is.Not.Null);
            Assert.That(registry.TryResolveManagementByRevision(StrategyId, Phase3TestData.Instrument, r2.PolicyBundleId, 2), Is.Not.Null);
            Assert.That(registry.TryResolveManagementByRevision(StrategyId, Phase3TestData.Instrument, r3.PolicyBundleId, 3), Is.Not.Null);
        });
    }
}
