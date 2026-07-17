using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Strategies;
using NUnit.Framework;
using Simulator.Models;
using TradingPolicies;

namespace Simulator.Tests;

[TestFixture]
public sealed class TradingPolicyPromotionTests
{
    // Record-generated Equals() compares IReadOnlyList<T>/array-typed properties by reference
    // (List<T> and arrays do not override Equals), so a JSON-deserialized List<T> can never equal
    // the original compiler-synthesized array default even when every element is identical. Is.EqualTo
    // on these records therefore cannot verify true round-trip content fidelity - compare canonical
    // JSON instead, which serializes actual list contents rather than the backing collection type.
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Canonical<T>(T value) => JsonSerializer.Serialize(value, CanonicalOptions);

    [Test]
    public void SimulatorPolicy_RoundTripsAsEnvironmentNeutralProfile()
    {
        var runtime = new BacktestRuntimeOptions();
        var agentOptions = new ProgressiveStrategyOptions();
        DateTimeOffset createdAt = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

        TradingPolicyProfile profile = TradingPolicyPromotion.CreateProfile(
            runtime,
            "improved",
            "improved-v1",
            ProgressiveAgentKind.Improved,
            agentOptions,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            revision: 1,
            createdAt: createdAt,
            status: TradingPolicyProfileStatus.ApprovedForDemo,
            description: "reviewed simulator policy");

        string json = TradingPolicyProfileJson.Serialize(profile);
        TradingPolicyProfile restored = TradingPolicyProfileJson.Deserialize(json);

        Assert.Multiple(() =>
        {
            Assert.That(restored.ConfigurationHash, Is.EqualTo(profile.ConfigurationHash));
            Assert.That(restored.AgentKind, Is.EqualTo(ProgressiveAgentKind.Improved));
            Assert.That(restored.Status, Is.EqualTo(TradingPolicyProfileStatus.ApprovedForDemo));
            Assert.That(Canonical(restored.AgentOptions), Is.EqualTo(Canonical(agentOptions)));
            Assert.That(Canonical(restored.PositionSizing), Is.EqualTo(Canonical(runtime.PositionSizing)));
            Assert.That(Canonical(restored.PortfolioRisk), Is.EqualTo(Canonical(runtime.PortfolioRisk)));
            Assert.That(Canonical(restored.TradingConditions), Is.EqualTo(Canonical(runtime.TradingConditions)));
            Assert.That(Canonical(restored.ImprovedManagement), Is.EqualTo(Canonical(runtime.ImprovedPositionManagement)));
        });
    }

    [Test]
    public void CreateProfile_ForcesMetaModelEnabled_WhenArtifactIdIsSupplied_RegardlessOfRuntimeToggle()
    {
        // AGENT-02 regression: the auto-train pipeline reuses the *source* backtest's Runtime,
        // whose MetaModel.Enabled reflects that unrelated run rather than the artifact this
        // promotion attaches. CreateProfile must derive Enabled from artifact presence, not trust
        // the runtime's own unrelated consumption toggle - otherwise a live policy profile could
        // carry MetaModelArtifactId set with Enabled=false (or vice versa), which
        // TradingPolicyProfile.Validate() now hard-rejects.
        var runtime = new BacktestRuntimeOptions(); // bare default: MetaModel.Enabled == false
        var agentOptions = new ProgressiveStrategyOptions();

        TradingPolicyProfile profile = TradingPolicyPromotion.CreateProfile(
            runtime,
            "improved",
            "improved-v1",
            ProgressiveAgentKind.Improved,
            agentOptions,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            revision: 1,
            createdAt: new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
            status: TradingPolicyProfileStatus.Research,
            metaModelArtifactId: Guid.Parse("11111111-2222-3333-4444-555555555555"));

        Assert.That(profile.MetaModelPolicy.Enabled, Is.True);
    }

    [Test]
    public void CreateProfile_LeavesMetaModelDisabled_WhenNoArtifactIdSupplied()
    {
        var runtime = new BacktestRuntimeOptions
        {
            // Enabled=true here represents the *source* backtest's own, unrelated meta-model
            // consumption - CreateProfile must not let this leak into a promotion that isn't
            // actually attaching any meta-model artifact.
            MetaModel = new Simulator.Calibration.MetaModelPolicyOptions { Enabled = true },
            MetaModelArtifact = new Simulator.Calibration.MetaModelArtifact
            {
                SchemaVersion = 1,
                CalibrationId = "test-calibration",
                ModelVersion = "test-v1",
                FeatureSchemaHash = RiskManager.Calibration.MetaLabelFeatureFactory.SchemaVersion,
                TrainingFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                TrainingTo = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                DataHash = "test-hash",
                CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                Buckets = []
            }
        };
        var agentOptions = new ProgressiveStrategyOptions();

        TradingPolicyProfile profile = TradingPolicyPromotion.CreateProfile(
            runtime,
            "improved",
            "improved-v1",
            ProgressiveAgentKind.Improved,
            agentOptions,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            revision: 1,
            createdAt: new DateTimeOffset(2026, 7, 17, 12, 0, 0, TimeSpan.Zero),
            status: TradingPolicyProfileStatus.Research);

        Assert.That(profile.MetaModelPolicy.Enabled, Is.False);
    }

    [Test]
    public void Validate_Throws_WhenMetaModelEnabledDisagreesWithArtifactPresence()
    {
        var runtime = new BacktestRuntimeOptions();
        var agentOptions = new ProgressiveStrategyOptions();
        DateTimeOffset createdAt = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

        TradingPolicyProfile validProfile = TradingPolicyPromotion.CreateProfile(
            runtime,
            "improved",
            "improved-v1",
            ProgressiveAgentKind.Improved,
            agentOptions,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            revision: 1,
            createdAt: createdAt,
            status: TradingPolicyProfileStatus.Research,
            metaModelArtifactId: Guid.Parse("11111111-2222-3333-4444-555555555555"));

        // Simulate a stale/leftover mismatch (e.g. hand-edited config, or a future construction
        // path that doesn't go through CreateProfile's derivation) directly on the record.
        TradingPolicyProfile staleArtifactWithoutEnabled = validProfile with
        {
            MetaModelPolicy = validProfile.MetaModelPolicy with { Enabled = false }
        };
        TradingPolicyProfile enabledWithoutArtifact = validProfile with
        {
            MetaModelArtifactId = null
        };

        Assert.Multiple(() =>
        {
            Assert.That(() => staleArtifactWithoutEnabled.Validate(), Throws.ArgumentException);
            Assert.That(() => enabledWithoutArtifact.Validate(), Throws.ArgumentException);
            Assert.That(() => validProfile.Validate(), Throws.Nothing);
        });
    }
}
