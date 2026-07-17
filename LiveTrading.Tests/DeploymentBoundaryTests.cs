using Agent.Abstractions;
using LiveTrading.Agents;
using NUnit.Framework;
using PortfolioManager.Risk;
using RiskManager;
using TradeManager;

namespace LiveTrading.Tests;

[TestFixture]
public sealed class DeploymentBoundaryTests
{
    [Test]
    public void CoreTradingAssemblies_DoNotReferenceLiveOrSimulatorHosts()
    {
        Type[] roots =
        [
            typeof(ITradingAgent),
            typeof(IStructureBasedTradeManager),
            typeof(PreTradeRiskManager),
            typeof(PortfolioRiskManager)
        ];

        foreach (Type root in roots)
        {
            string[] references = root.Assembly.GetReferencedAssemblies()
                .Select(name => name.Name ?? string.Empty)
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(references, Does.Not.Contain("LiveTrading"), root.Assembly.GetName().Name);
                Assert.That(references, Does.Not.Contain("LiveTradingHost"), root.Assembly.GetName().Name);
                Assert.That(references, Does.Not.Contain("LiveTrading.Oanda"), root.Assembly.GetName().Name);
                Assert.That(references, Does.Not.Contain("Simulator"), root.Assembly.GetName().Name);
            });
        }
    }

    [Test]
    public void NeutralCandidate_DoesNotCarryDeploymentOrBrokerEnvironment()
    {
        string[] propertyNames = typeof(LiveTradeCandidate).GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(propertyNames.Any(name => name.Contains("Mode", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(propertyNames.Any(name => name.Contains("Environment", StringComparison.OrdinalIgnoreCase)), Is.False);
            Assert.That(propertyNames.Any(name => name.Contains("Broker", StringComparison.OrdinalIgnoreCase)), Is.False);
        });
    }
}
