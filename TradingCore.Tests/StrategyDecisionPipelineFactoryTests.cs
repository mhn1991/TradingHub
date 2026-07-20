using System.Reflection;
using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.Value;
using ExecutionManager;
using NUnit.Framework;
using PortfolioManager.CrossMarket;
using RiskManager.Calibration;
using RiskManager.Safety;
using Simulator.Calibration;
using TradingCore.Pipeline;
using TradingJournal;

namespace TradingCore.Tests;

[TestFixture]
public sealed class StrategyDecisionPipelineFactoryTests
{
    [Test]
    public void Create_ReturnsPipelineWrappingTheGivenAgent()
    {
        var agent = new FakeTradingAgent();
        var factory = new StrategyDecisionPipelineFactory(new FakeExecutionCoordinator());

        StrategyDecisionRuntime runtime = factory.Create(
            Strategy(agent),
            DefaultPolicy(),
            setupCalibration: null,
            metaModel: null,
            new TradingSafetyOptions());

        Assert.Multiple(() =>
        {
            Assert.That(runtime.Pipeline, Is.Not.Null);
            Assert.That(runtime.Agent, Is.SameAs(agent));
            Assert.That(runtime.StrategyVersion, Is.EqualTo("v1"));
            Assert.That(runtime.SetupCalibrationId, Is.Null);
            Assert.That(runtime.MetaModelVersion, Is.Null);
        });
    }

    [Test]
    public void Create_WithSharedSafetyController_PipelineUsesTheExactSameInstance()
    {
        var sharedSafety = new TradingSafetyController(new TradingSafetyOptions());
        var factory = new StrategyDecisionPipelineFactory(
            new FakeExecutionCoordinator(), sharedSafetyController: sharedSafety);

        StrategyDecisionRuntime runtime = factory.Create(
            Strategy(new FakeTradingAgent()),
            DefaultPolicy(),
            setupCalibration: null,
            metaModel: null,
            new TradingSafetyOptions());

        // SafeTradingPipeline does not expose its safety controller publicly - reflection is the
        // only way to prove reference identity, which is exactly the regression this test guards:
        // a naive factory building a second TradingSafetyController from options would silently
        // desynchronize trip state from whatever the caller already tracks.
        FieldInfo? field = typeof(SafeTradingPipeline).GetField(
            "_safety", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        Assert.That(field!.GetValue(runtime.Pipeline), Is.SameAs(sharedSafety));
    }

    [Test]
    public void Create_WithoutSharedSafetyController_BuildsItsOwnFromOptions()
    {
        var factory = new StrategyDecisionPipelineFactory(new FakeExecutionCoordinator());

        StrategyDecisionRuntime runtime = factory.Create(
            Strategy(new FakeTradingAgent()),
            DefaultPolicy(),
            setupCalibration: null,
            metaModel: null,
            new TradingSafetyOptions());

        FieldInfo field = typeof(SafeTradingPipeline).GetField(
            "_safety", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.That(field.GetValue(runtime.Pipeline), Is.InstanceOf<TradingSafetyController>());
    }

    [Test]
    public void Create_PopulatesSetupCalibrationIdAndMetaModelVersion_WhenSupplied()
    {
        SetupCalibrationArtifact setupArtifact = SetupArtifact();
        CalibratedSetupMetaModel metaModel = MetaModel();
        var factory = new StrategyDecisionPipelineFactory(new FakeExecutionCoordinator());

        RuntimeFeaturePolicy policy = DefaultPolicy() with
        {
            SetupCalibration = new SetupCalibrationPolicyOptions { Enabled = true }
        };

        StrategyDecisionRuntime runtime = factory.Create(
            Strategy(new FakeTradingAgent()),
            policy,
            setupArtifact,
            metaModel,
            new TradingSafetyOptions());

        Assert.Multiple(() =>
        {
            Assert.That(runtime.SetupCalibrationId, Is.EqualTo(setupArtifact.CalibrationId));
            Assert.That(runtime.MetaModelVersion, Is.EqualTo(metaModel.ModelVersion));
        });
    }

    [Test]
    public void Create_RejectsLegacyMetaFeatureSchema_WithRetrainingInstruction()
    {
        SetupCalibrationArtifact legacyArtifact = SetupArtifact() with
        {
            FeatureSchemaHash = "tradinghub-meta-v1"
        };
        RuntimeFeaturePolicy policy = DefaultPolicy() with
        {
            SetupCalibration = new SetupCalibrationPolicyOptions { Enabled = true }
        };
        var factory = new StrategyDecisionPipelineFactory(new FakeExecutionCoordinator());

        ArgumentException? exception = Assert.Throws<ArgumentException>(() => factory.Create(
            Strategy(new FakeTradingAgent()),
            policy,
            legacyArtifact,
            metaModel: null,
            new TradingSafetyOptions()));

        Assert.That(exception!.Message, Does.Contain("Retraining is required"));
    }

    [Test]
    public void Create_AcceptsCurrentMetaSchema_IndependentOfPolicyContentHash()
    {
        RuntimeFeaturePolicy changedPolicy = DefaultPolicy() with
        {
            DmiConfirmationEnabled = false,
            SetupCalibration = new SetupCalibrationPolicyOptions { Enabled = true }
        };
        var factory = new StrategyDecisionPipelineFactory(new FakeExecutionCoordinator());

        Assert.That(
            () => factory.Create(
                Strategy(new FakeTradingAgent()),
                changedPolicy,
                SetupArtifact(),
                metaModel: null,
                new TradingSafetyOptions()),
            Throws.Nothing);
    }

    [Test]
    public void FeaturePolicyHash_IsDeterministic_AndChangesWithContent()
    {
        RuntimeFeaturePolicy a = DefaultPolicy();
        RuntimeFeaturePolicy b = DefaultPolicy();
        RuntimeFeaturePolicy changed = DefaultPolicy() with { DmiConfirmationEnabled = false };

        Assert.Multiple(() =>
        {
            Assert.That(a.ComputeHash(), Is.EqualTo(b.ComputeHash()));
            Assert.That(a.ComputeHash(), Is.Not.EqualTo(changed.ComputeHash()));
        });
    }

    [Test]
    public void FeaturePolicyHash_MatchesExplicitCanonicalizationIncludingNeoWaveEvidence()
    {
        // Reconstruct the complete canonical identity independently so new decision features,
        // including NEoWave evidence, cannot be omitted from policy provenance by accident.
        RuntimeFeaturePolicy policy = DefaultPolicy() with
        {
            MarketRegimeRouting = new MarketRegimePolicyOptions { Enabled = true }
        };

        ChartAnnotationOptions canonicalAnnotation = policy.AnnotationOptions with
        {
            PriceActionSetups = policy.AnnotationOptions.PriceActionSetups with { EnabledSetups = [] }
        };
        string expectedCanonical =
            $"annotation:{canonicalAnnotation}|" +
            $"enabled-setups:[{string.Join(',', policy.AnnotationOptions.PriceActionSetups.EnabledSetups.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal))}]|" +
            $"regime:{policy.MarketRegimeRouting.Enabled},{policy.MarketRegimeRouting.MinimumRegimeConfidence}," +
            $"[{string.Join(';', policy.MarketRegimeRouting.Policies.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}:{entry.Value.AllowNewEntries},{entry.Value.MinimumConfidenceAdjustment},{entry.Value.RiskMultiplier},{entry.Value.EntryProfileId},{entry.Value.ManagementProfileId}"))}]|" +
            $"value-location:{policy.ValueLocationEvidence}|" +
            $"currency-strength-evidence:{policy.CurrencyStrengthEvidence}|" +
            $"rsi-bollinger:{policy.RsiBollingerSignals}|" +
            $"dmi:{policy.DmiConfirmationEnabled}|" +
            $"currency-strength:{policy.CurrencyStrength.Enabled},{policy.CurrencyStrength.Interval},{policy.CurrencyStrength.ReturnLookbackBars}," +
            $"{policy.CurrencyStrength.VolatilityLookbackBars},{policy.CurrencyStrength.MinimumCurrencyCoveragePercent}," +
            $"[{string.Join(';', policy.CurrencyStrength.Baskets.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}:{string.Join(',', entry.Value.Select(i => i.Value))}"))}]|" +
            $"setup-calibration:{policy.SetupCalibration}|" +
            $"neowave-evidence:{policy.NeoWaveEvidence}|" +
            $"schema:{RiskManager.Calibration.MetaLabelFeatureFactory.SchemaVersion}";
        byte[] expectedHashBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(expectedCanonical));
        string expectedHash = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

        Assert.That(policy.ComputeHash(), Is.EqualTo(expectedHash));
    }

    [Test]
    public void FeaturePolicyHash_ChangesWithMarketRegimeRoutingContent()
    {
        // AGENT-05: the hash is genuinely content-sensitive (proven here for the audit's own
        // example scenario, regime-policy drift), so it is trustworthy provenance even though
        // StrategyDecisionPipelineFactory.Create deliberately does not compare it against
        // anything today - see the comment at that call site. This test exists so a future
        // enforcement wire-up has a hash already proven correct to build on.
        RuntimeFeaturePolicy baseline = DefaultPolicy();
        RuntimeFeaturePolicy regimeChanged = DefaultPolicy() with
        {
            MarketRegimeRouting = new MarketRegimePolicyOptions { Enabled = true }
        };

        Assert.That(baseline.ComputeHash(), Is.Not.EqualTo(regimeChanged.ComputeHash()));
    }

    private static StrategyRuntimeDefinition Strategy(ITradingAgent agent) => new()
    {
        StrategyId = "test-strategy",
        StrategyVersion = "v1",
        Agent = agent
    };

    private static RuntimeFeaturePolicy DefaultPolicy() => new()
    {
        AnnotationOptions = new ChartAnnotationOptions(),
        MarketRegimeRouting = new(),
        ValueLocationEvidence = new ValueLocationEvidenceOptions(),
        CurrencyStrengthEvidence = new CurrencyStrengthEvidenceOptions(),
        RsiBollingerSignals = new RsiBollingerSignalOptions(),
        DmiConfirmationEnabled = true,
        CurrencyStrength = new CurrencyStrengthOptions(),
        SetupCalibration = new SetupCalibrationPolicyOptions()
    };

    private static SetupCalibrationArtifact SetupArtifact() => new()
    {
        SchemaVersion = 1,
        CalibrationId = "cal-1",
        TrainingFrom = DateTimeOffset.UtcNow.AddDays(-30),
        TrainingTo = DateTimeOffset.UtcNow,
        Instruments = ["FX:EUR/USD"],
        StrategyVersion = "v1",
        FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
        Parameters = new Dictionary<string, string>(),
        TotalSamples = 100,
        CreatedAt = DateTimeOffset.UtcNow,
        DataHash = "hash",
        Buckets = []
    };

    private static CalibratedSetupMetaModel MetaModel()
    {
        var artifact = new MetaModelArtifact
        {
            SchemaVersion = 1,
            CalibrationId = "meta-1",
            ModelVersion = "meta-v1",
            FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
            TrainingFrom = DateTimeOffset.UtcNow.AddDays(-30),
            TrainingTo = DateTimeOffset.UtcNow,
            DataHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
            Buckets = []
        };
        return new CalibratedSetupMetaModel(artifact, new MetaModelPolicyOptions());
    }

    private sealed class FakeTradingAgent : ITradingAgent
    {
        public string Name => "fake";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { BarInterval.Minutes(5) };
        public BarInterval TriggerInterval => BarInterval.Minutes(5);
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by these wiring/construction tests.");
    }

    private sealed class FakeExecutionCoordinator : IExecutionCoordinator
    {
        public Task<OrderSubmission?> ProcessAsync(
            AgentDecision decision, ITradingBrokerClient broker, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderSubmission?>(null);

        public Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
            ProtectiveStopAmendmentCommand command, ITradingBrokerClient broker, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("Not exercised by these wiring/construction tests.");
    }
}
