using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agent.Configuration;
using Agent.Factories;
using Agent.Strategies;
using NUnit.Framework;
using Simulator.Models;
using TradingPolicies;

namespace Simulator.Tests;

[TestFixture]
public sealed class AgentCatalogueArchitectureTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void DefaultCatalogue_ResolvesEveryCanonicalAgentType()
    {
        TradingAgentCatalog catalog = TradingAgentCatalog.CreateDefault();
        AgentDefinition[] definitions =
        [
            AgentDefinition.FromProgressive(ProgressiveAgentKind.Legacy, new ProgressiveStrategyOptions()),
            AgentDefinition.FromProgressive(ProgressiveAgentKind.Improved, new ProgressiveStrategyOptions()),
            AgentDefinition.FromStructuralConfluence(new())
        ];

        Assert.Multiple(() =>
        {
            Assert.That(catalog.List().Select(item => item.AgentTypeId), Is.EquivalentTo(new[]
            {
                TradingAgentTypeIds.LegacyProgressive,
                TradingAgentTypeIds.ImprovedProgressive,
                TradingAgentTypeIds.StructuralConfluence
            }));
            foreach (AgentDefinition definition in definitions)
            {
                Assert.That(catalog.Get(definition.AgentTypeId).AgentTypeId, Is.EqualTo(definition.AgentTypeId));
                Assert.That(catalog.Create(definition).RequiredIntervals, Is.Not.Empty);
            }
        });
    }

    [Test]
    public void Catalogue_RejectsDuplicateUnknownAndSchemaIncompatibleDefinitions()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new TradingAgentCatalog(new ITradingAgentBuilder[]
                {
                    new ProgressiveTradingAgentBuilder(ProgressiveAgentKind.Legacy),
                    new ProgressiveTradingAgentBuilder(ProgressiveAgentKind.Legacy)
                }),
                Throws.InvalidOperationException.With.Message.Contains("Duplicate Agent builder"));
            Assert.That(
                () => TradingAgentCatalog.CreateDefault().Get("unknown-agent"),
                Throws.ArgumentException.With.Message.Contains("Unknown trading agent"));
            Assert.That(
                () => TradingAgentCatalog.CreateDefault().Create(new AgentDefinition
                {
                    AgentTypeId = TradingAgentTypeIds.ImprovedProgressive,
                    SchemaVersion = AgentDefinition.CurrentSchemaVersion + 1,
                    Options = JsonSerializer.SerializeToElement(new ProgressiveStrategyOptions())
                }),
                Throws.TypeOf<NotSupportedException>());
        });
    }

    [Test]
    public void LegacyTypedAgentDefinitionJson_MigratesDeterministicallyToGenericDefinition()
    {
        TradingPolicyProfile profile = CreateProfile(new ProgressiveStrategyOptions());
        JsonNode document = JsonNode.Parse(TradingPolicyProfileJson.Serialize(profile))!;
        document["agentDefinition"] = JsonSerializer.SerializeToNode(
            TradingAgentDefinition.FromAgentDefinition(profile.EffectiveGenericAgentDefinition()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            });

        TradingPolicyProfile first = TradingPolicyProfileJson.Deserialize(document.ToJsonString());
        TradingPolicyProfile second = TradingPolicyProfileJson.Deserialize(document.ToJsonString());
        AgentDefinition firstDefinition = first.EffectiveGenericAgentDefinition();
        AgentDefinition secondDefinition = second.EffectiveGenericAgentDefinition();

        Assert.Multiple(() =>
        {
            Assert.That(firstDefinition.AgentTypeId, Is.EqualTo(secondDefinition.AgentTypeId));
            Assert.That(firstDefinition.SchemaVersion, Is.EqualTo(secondDefinition.SchemaVersion));
            Assert.That(firstDefinition.Options.GetRawText(), Is.EqualTo(secondDefinition.Options.GetRawText()));
            Assert.That(first.ConfigurationHash, Is.EqualTo(profile.ConfigurationHash));
            Assert.That(TradingPolicyProfileJson.Serialize(first), Is.EqualTo(TradingPolicyProfileJson.Serialize(second)));
        });
    }

    [Test]
    public void PackageHash_ChangesForBehaviourButIgnoresDisplayOnlyDescription()
    {
        TradingPolicyProfile baseline = CreateProfile(new ProgressiveStrategyOptions(), "first display note");
        TradingPolicyProfile renamed = baseline with { Description = "different display note" };
        ResolvedAgentPackage baselinePackage = Resolve(baseline);
        ResolvedAgentPackage renamedPackage = Resolve(renamed);
        ResolvedAgentPackage quantityChanged = Resolve(CreateProfile(
            new ProgressiveStrategyOptions { Quantity = 2_000m }));
        ResolvedAgentPackage thresholdChanged = Resolve(CreateProfile(
            new ProgressiveStrategyOptions { MinimumEntryConfidence = 60m }));
        ResolvedAgentPackage geometryChanged = Resolve(CreateProfile(
            new ProgressiveStrategyOptions { SupplyDemandStructuralStopsEnabled = true }));

        Assert.Multiple(() =>
        {
            Assert.That(renamed.ConfigurationHash, Is.EqualTo(baseline.ConfigurationHash));
            Assert.That(renamedPackage.PackageHash, Is.EqualTo(baselinePackage.PackageHash));
            Assert.That(new[]
            {
                baselinePackage.PackageHash,
                quantityChanged.PackageHash,
                thresholdChanged.PackageHash,
                geometryChanged.PackageHash
            }, Is.Unique);
        });
    }

    [Test]
    public void AgentProject_HasNoRuntimeHostSimulatorDatabaseOrConcreteBrokerDependencies()
    {
        string agentDirectory = FindAgentDirectory();
        string[] sourceFiles = Directory.GetFiles(agentDirectory, "*.cs", SearchOption.AllDirectories);
        string source = string.Join('\n', sourceFiles.Select(File.ReadAllText));
        string[] forbiddenReferences =
        [
            "using LiveTrading", "using LiveTradingHost", "using Simulator", "using DBManager",
            "Brokers.Oanda", "BrokerClientFactory", "OandaBrokerClient", "IgBrokerClient",
            "BinanceBrokerClient", "EnvironmentMode", "IsSimulation", "IsLiveEnvironment"
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                typeof(AgentDefinition).Assembly.GetReferencedAssemblies().Select(item => item.Name),
                Has.None.Matches<string>(name => name is "LiveTrading" or "LiveTradingHost" or "Simulator" or
                    "DBManager" or "DBManager.Postgres"));
            foreach (string forbidden in forbiddenReferences)
                Assert.That(source, Does.Not.Contain(forbidden), $"Agent source must not reference '{forbidden}'.");
        });
    }

    private static TradingPolicyProfile CreateProfile(
        ProgressiveStrategyOptions options,
        string? description = null) =>
        TradingPolicyPromotion.CreateProfile(
            new BacktestRuntimeOptions(),
            "improved-progressive",
            "1.0.0",
            ProgressiveAgentKind.Improved,
            options,
            Guid.Parse("7b9d60b2-816a-4862-a88b-9aba8854c29d"),
            revision: 1,
            createdAt: CreatedAt,
            status: TradingPolicyProfileStatus.ApprovedForDemo,
            description: description);

    private static ResolvedAgentPackage Resolve(TradingPolicyProfile profile) =>
        ResolvedAgentPackage.Create(
            Guid.Parse("be0ca836-eaf9-42c1-860d-37b48f0a330f"),
            profile,
            TradingAgentCatalog.CreateDefault(),
            setupCalibration: null,
            metaModel: null,
            managementCalibration: null,
            artifactContentHashes: new Dictionary<Guid, string>());

    private static string FindAgentDirectory()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "Agent");
            if (File.Exists(Path.Combine(candidate, "Agent.csproj")))
                return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Agent project for architecture checks.");
    }
}
