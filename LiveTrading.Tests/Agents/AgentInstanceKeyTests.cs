using Brokers.Models;
using LiveTrading.Agents;
using NUnit.Framework;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class AgentInstanceKeyTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly Guid PolicyBundleId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Test]
    public void KeysDifferingOnlyInRevision_AreUnequal_AndHashDifferently()
    {
        // The key-level version of "same strategy type, different policy revisions remains
        // isolated" - the confirmed gap this widening exists to close (multi-agent architecture
        // Phase 2): before this, AgentInstanceKey was (Instrument, StrategyId) only, so two
        // Agents sharing a StrategyId but running different policy revisions could not coexist
        // in AgentSupervisor._instances.
        var revisionOne = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved", PolicyBundleId, 1);
        var revisionTwo = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved", PolicyBundleId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(revisionOne, Is.Not.EqualTo(revisionTwo));
            Assert.That(revisionOne.GetHashCode(), Is.Not.EqualTo(revisionTwo.GetHashCode()));
        });
    }

    [Test]
    public void KeysDifferingOnlyInPolicyBundleId_AreUnequal()
    {
        var a = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved", PolicyBundleId, 1);
        var b = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved", Guid.NewGuid(), 1);

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void IdenticalFields_AreEqual_SupportingDictionaryUseAsAKey()
    {
        var a = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved", PolicyBundleId, 1);
        var b = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved", PolicyBundleId, 1);
        var dictionary = new Dictionary<AgentInstanceKey, string> { [a] = "value" };

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b));
            Assert.That(dictionary.ContainsKey(b), Is.True);
        });
    }

    [Test]
    public void DifferentStrategyId_SameRevision_AreUnequal()
    {
        var a = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved-strict", PolicyBundleId, 1);
        var b = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "improved-soft", PolicyBundleId, 1);

        Assert.That(a, Is.Not.EqualTo(b));
    }
}
