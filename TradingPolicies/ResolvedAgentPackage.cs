using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Configuration;
using Agent.Factories;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.Engine;
using PortfolioManager.Correlation;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Calibration;
using RiskManager.Conditions;
using RiskManager.Safety;
using Simulator.Calibration;
using TradeManager;
using TradingCore.Pipeline;

namespace TradingPolicies;

/// <summary>
/// Exact, immutable and environment-neutral input shared by simulation, parity and live runtimes.
/// Broker accounts, credentials, execution modes and data-source details deliberately do not
/// belong here.
/// </summary>
public sealed record ResolvedAgentPackage
{
    public required Guid PolicyId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required int Revision { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required AgentDefinition AgentDefinition { get; init; }
    public required RuntimeFeaturePolicy FeaturePolicy { get; init; }
    public required PositionSizingOptions PositionSizing { get; init; }
    public required AdaptiveRiskOptions AdaptiveRisk { get; init; }
    public required PortfolioRiskOptions PortfolioRisk { get; init; }
    public required CorrelationRiskOptions CorrelationRisk { get; init; }
    public required TradingConditionOptions TradingConditions { get; init; }
    public required TradingSafetyOptions AccountSafety { get; init; }
    public required PositionManagementOptions PositionManagement { get; init; }
    public required RegimeManagementOptions RegimeManagement { get; init; }
    public Guid? SetupCalibrationArtifactId { get; init; }
    public Guid? MetaModelArtifactId { get; init; }
    public Guid? ManagementCalibrationArtifactId { get; init; }
    public SetupCalibrationArtifact? SetupCalibration { get; init; }
    public ISetupMetaModel? MetaModel { get; init; }
    public TradeManagementCalibration? ManagementCalibration { get; init; }
    public required IReadOnlySet<BarInterval> RequiredIntervals { get; init; }
    public required string AgentDefinitionHash { get; init; }
    public required string FeatureSchemaHash { get; init; }
    public required string AnnotationProfileHash { get; init; }
    public required string ManagementPolicyHash { get; init; }
    public required string ConfigurationHash { get; init; }
    public required IReadOnlyDictionary<Guid, string> ArtifactContentHashes { get; init; }
    public required string PackageHash { get; init; }
    public required string SourceCommit { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public static ResolvedAgentPackage Create(
        Guid policyRevisionId,
        TradingPolicyProfile profile,
        ITradingAgentCatalog catalog,
        SetupCalibrationArtifact? setupCalibration,
        ISetupMetaModel? metaModel,
        TradeManagementCalibration? managementCalibration,
        IReadOnlyDictionary<Guid, string> artifactContentHashes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);
        profile.Validate();
        AgentDefinition definition = profile.EffectiveGenericAgentDefinition();
        var intervals = catalog.Create(definition).RequiredIntervals.ToFrozenSet();
        PositionManagementOptions management = definition.AgentTypeId switch
        {
            TradingAgentTypeIds.LegacyProgressive => profile.LegacyManagement,
            TradingAgentTypeIds.ImprovedProgressive => profile.ImprovedManagement,
            TradingAgentTypeIds.StructuralConfluence =>
                AlignStructuralManagementInterval(profile.StructuralManagement, definition.ReadStructuralOptions()),
            _ => throw new InvalidOperationException($"Unknown agent type '{definition.AgentTypeId}'.")
        };
        string agentHash = Hash(JsonSerializer.Serialize(definition));
        string managementHash = Hash(JsonSerializer.Serialize(new
        {
            PositionManagement = management,
            profile.RegimeManagement,
            profile.ManagementCalibration,
            profile.ManagementCalibrationArtifactId
        }));
        string artifacts = string.Join('|', artifactContentHashes.OrderBy(x => x.Key)
            .Select(x => $"{x.Key:N}:{x.Value.ToLowerInvariant()}"));
        string packageHash = Hash(string.Join('|', profile.ConfigurationHash, agentHash,
            profile.FeaturePolicy.ComputeHash(), managementHash, artifacts));

        return new ResolvedAgentPackage
        {
            PolicyId = profile.ProfileId,
            PolicyRevisionId = policyRevisionId,
            Revision = profile.Revision,
            StrategyId = profile.StrategyId,
            StrategyVersion = profile.StrategyVersion,
            AgentDefinition = definition,
            FeaturePolicy = profile.FeaturePolicy,
            PositionSizing = profile.PositionSizing,
            AdaptiveRisk = profile.AdaptiveRisk,
            PortfolioRisk = profile.PortfolioRisk,
            CorrelationRisk = profile.CorrelationRisk,
            TradingConditions = profile.TradingConditions,
            AccountSafety = profile.AccountSafety,
            PositionManagement = management,
            RegimeManagement = profile.RegimeManagement,
            SetupCalibrationArtifactId = profile.SetupCalibrationArtifactId,
            MetaModelArtifactId = profile.MetaModelArtifactId,
            ManagementCalibrationArtifactId = profile.ManagementCalibrationArtifactId,
            SetupCalibration = setupCalibration,
            MetaModel = metaModel,
            ManagementCalibration = managementCalibration,
            RequiredIntervals = intervals,
            AgentDefinitionHash = agentHash,
            FeatureSchemaHash = profile.FeaturePolicy.ComputeHash(),
            AnnotationProfileHash = ChartAnnotationOptionsHasher.ComputeHash(profile.FeaturePolicy.AnnotationOptions),
            ManagementPolicyHash = managementHash,
            ConfigurationHash = profile.ConfigurationHash,
            ArtifactContentHashes = artifactContentHashes.ToFrozenDictionary(),
            PackageHash = packageHash,
            SourceCommit = profile.SourceCommit,
            CreatedAt = profile.CreatedAt
        };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Re-pins <see cref="PositionManagementOptions.FastStructureInterval"/>/
    /// <see cref="PositionManagementOptions.MainStructureInterval"/>/
    /// <see cref="PositionManagementOptions.ThesisInterval"/> to the resolved agent's own
    /// <see cref="StructuralConfluenceStrategyOptions.TriggerInterval"/>/<c>SetupInterval</c>/
    /// <c>ContextInterval</c>, keeping every other tuned field on <paramref name="management"/>
    /// untouched. Without this, an agent configured at a non-default cadence would silently inherit
    /// whatever fixed interval values <paramref name="management"/> happened to carry (typically the
    /// 5m/15m/1h historical defaults) instead of the agent it is actually paired with - this is the
    /// single shared resolution path simulation, parity, and live all go through
    /// (<c>LiveTradingPolicyBundle.FromResolvedPackage</c> copies <see cref="PositionManagement"/>
    /// straight through), so fixing it here fixes all three. At the
    /// 5m/15m/1h default this reproduces the input unchanged - zero behavior change for every
    /// deployment that exists today.
    /// </summary>
    private static PositionManagementOptions AlignStructuralManagementInterval(
        PositionManagementOptions management, StructuralConfluenceStrategyOptions structural) =>
        management with
        {
            FastStructureInterval = structural.TriggerInterval,
            MainStructureInterval = structural.SetupInterval,
            ThesisInterval = structural.ContextInterval
        };
}

public interface IAgentPackageResolver
{
    Task<ResolvedAgentPackage> ResolveAsync(Guid policyRevisionId, CancellationToken cancellationToken = default);
}
