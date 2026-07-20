using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Configuration;
using Agent.Strategies;
using PortfolioManager.Correlation;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Conditions;
using RiskManager.Safety;
using Simulator.Calibration;
using TradeManager;
using TradingCore.Pipeline;

namespace TradingPolicies;

public enum TradingPolicyProfileStatus
{
    Research,
    Reviewed,
    ApprovedForDemo,
    Retired
}

/// <summary>
/// Environment-neutral, immutable strategy policy produced by research and consumed unchanged by
/// the simulator, shadow runtime, manual demo runtime, or later certified live runtime. Broker
/// credentials, historical-data source, fill simulation, host URLs, and execution activation mode
/// are intentionally excluded because they belong to adapters/deployment, not trading behaviour.
/// </summary>
public sealed record TradingPolicyProfile
{
    public int SchemaVersion { get; init; } = 3;
    public required Guid ProfileId { get; init; }
    public required int Revision { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required TradingPolicyProfileStatus Status { get; init; }
    [System.Text.Json.Serialization.JsonConverter(typeof(AgentDefinitionCompatibilityJsonConverter))]
    public AgentDefinition? AgentDefinition { get; init; }
    public ProgressiveAgentKind? AgentKind { get; init; }
    public ProgressiveStrategyOptions? AgentOptions { get; init; }
    public required RuntimeFeaturePolicy FeaturePolicy { get; init; }

    public Guid? SetupCalibrationArtifactId { get; init; }
    public Guid? ManagementCalibrationArtifactId { get; init; }
    public Guid? MetaModelArtifactId { get; init; }
    public MetaModelPolicyOptions MetaModelPolicy { get; init; } = new();

    public PositionSizingOptions PositionSizing { get; init; } = new();
    public AdaptiveRiskOptions AdaptiveRisk { get; init; } = new();
    public PortfolioRiskOptions PortfolioRisk { get; init; } = new();
    public CorrelationRiskOptions CorrelationRisk { get; init; } = new();
    public TradingConditionOptions TradingConditions { get; init; } = new();
    public TradingSafetyOptions AccountSafety { get; init; } = new();
    public PositionManagementOptions LegacyManagement { get; init; } = PositionManagementOptions.LegacyDefaults;
    public PositionManagementOptions ImprovedManagement { get; init; } = PositionManagementOptions.ImprovedDefaults;
    public PositionManagementOptions StructuralManagement { get; init; } = PositionManagementOptions.StructuralDefaults;
    public RegimeManagementOptions RegimeManagement { get; init; } = new();
    public TradeManagementCalibrationOptions ManagementCalibration { get; init; } = new();

    public required DateTimeOffset CreatedAt { get; init; }
    public string SourceCommit { get; init; } = "unknown";
    public string? Description { get; init; }
    public required string ConfigurationHash { get; init; }

    public TradingAgentDefinition EffectiveAgentDefinition()
    {
        bool hasLegacyKind = AgentKind.HasValue;
        bool hasLegacyOptions = AgentOptions is not null;
        if (hasLegacyKind != hasLegacyOptions)
            throw new ArgumentException("Legacy agent fields must be supplied together.");

        TradingAgentDefinition? legacy = hasLegacyKind
            ? new TradingAgentDefinition
            {
                Kind = AgentKind == ProgressiveAgentKind.Legacy
                    ? TradingAgentKind.LegacyProgressive
                    : TradingAgentKind.ImprovedProgressive,
                Progressive = AgentOptions
            }
            : null;

        if (AgentDefinition is null && legacy is null)
            throw new ArgumentException("An agent definition is required.");
        AgentDefinition?.Validate();
        legacy?.Validate();

        if (AgentDefinition is not null && legacy is not null)
        {
            string current = JsonSerializer.Serialize(AgentDefinition);
            string compatibility = JsonSerializer.Serialize(legacy.ToAgentDefinition());
            if (!string.Equals(current, compatibility, StringComparison.Ordinal))
                throw new ArgumentException("AgentDefinition conflicts with legacy AgentKind/AgentOptions.");
        }

        return AgentDefinition is not null
            ? TradingAgentDefinition.FromAgentDefinition(AgentDefinition)
            : legacy!;
    }

    public AgentDefinition EffectiveGenericAgentDefinition() =>
        AgentDefinition ?? EffectiveAgentDefinition().ToAgentDefinition();

    public void Validate()
    {
        if (SchemaVersion is < 2 or > 3)
            throw new NotSupportedException($"Trading policy schema {SchemaVersion} is not supported.");
        if (ProfileId == Guid.Empty)
            throw new ArgumentException("ProfileId must not be empty.", nameof(ProfileId));
        if (Revision < 1)
            throw new ArgumentOutOfRangeException(nameof(Revision));
        if (string.IsNullOrWhiteSpace(StrategyId) || string.IsNullOrWhiteSpace(StrategyVersion))
            throw new ArgumentException("StrategyId and StrategyVersion are required.");
        if (!Enum.IsDefined(Status))
            throw new ArgumentOutOfRangeException(nameof(Status));
        if (CreatedAt > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(CreatedAt));

        ArgumentNullException.ThrowIfNull(FeaturePolicy);
        TradingAgentDefinition effectiveAgent = EffectiveAgentDefinition();
        FeaturePolicy.SetupCalibration.Validate();
        FeaturePolicy.NeoWaveEvidence.Validate();
        FeaturePolicy.AnnotationOptions.NeoWave.Validate();
        ProgressiveStrategyOptions? progressive = effectiveAgent.Progressive;
        if (progressive is not null && progressive.NeoWaveEvidence != FeaturePolicy.NeoWaveEvidence)
        {
            throw new ArgumentException(
                "AgentOptions.NeoWaveEvidence must match FeaturePolicy.NeoWaveEvidence.",
                nameof(FeaturePolicy));
        }
        if (progressive?.NeoWaveEvidence.Enabled == true && !FeaturePolicy.AnnotationOptions.NeoWave.Enabled)
        {
            throw new ArgumentException(
                "NEoWave Agent evidence requires NEoWave chart analysis to be enabled.",
                nameof(FeaturePolicy));
        }
        PositionSizing.Validate();
        AdaptiveRisk.Validate();
        PortfolioRisk.Validate();
        CorrelationRisk.Validate();
        TradingConditions.Validate();
        AccountSafety.Validate();
        LegacyManagement.Validate();
        ImprovedManagement.Validate();
        StructuralManagement.Validate();
        RegimeManagement.Validate();
        ManagementCalibration.Validate();
        MetaModelPolicy.Validate();
        // AGENT-02: a stale/leftover MetaModelArtifactId with Enabled=false (or vice versa) must
        // fail fast at construction, not silently diverge between simulator and live construction.
        if (MetaModelArtifactId.HasValue != MetaModelPolicy.Enabled)
        {
            throw new ArgumentException(
                "MetaModelPolicy.Enabled must agree with whether MetaModelArtifactId is set.",
                nameof(MetaModelPolicy));
        }

        string computed = ComputeConfigurationHash(this with { ConfigurationHash = string.Empty });
        if (!string.Equals(ConfigurationHash, computed, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ConfigurationHash does not match the environment-neutral policy content.",
                nameof(ConfigurationHash));
        }
    }

    public static TradingPolicyProfile Create(
        Guid profileId,
        int revision,
        string strategyId,
        string strategyVersion,
        ProgressiveAgentKind agentKind,
        ProgressiveStrategyOptions agentOptions,
        TradingPolicyProfileStatus status,
        RuntimeFeaturePolicy featurePolicy,
        PositionSizingOptions positionSizing,
        AdaptiveRiskOptions adaptiveRisk,
        PortfolioRiskOptions portfolioRisk,
        CorrelationRiskOptions correlationRisk,
        TradingConditionOptions tradingConditions,
        TradingSafetyOptions accountSafety,
        PositionManagementOptions legacyManagement,
        PositionManagementOptions improvedManagement,
        PositionManagementOptions structuralManagement,
        RegimeManagementOptions regimeManagement,
        TradeManagementCalibrationOptions managementCalibration,
        MetaModelPolicyOptions metaModelPolicy,
        DateTimeOffset createdAt,
        Guid? setupCalibrationArtifactId = null,
        Guid? managementCalibrationArtifactId = null,
        Guid? metaModelArtifactId = null,
        string? description = null)
        => Create(
            profileId,
            revision,
            strategyId,
            strategyVersion,
            new TradingAgentDefinition
            {
                Kind = agentKind == ProgressiveAgentKind.Legacy
                    ? TradingAgentKind.LegacyProgressive
                    : TradingAgentKind.ImprovedProgressive,
                Progressive = agentOptions
            },
            status,
            featurePolicy,
            positionSizing,
            adaptiveRisk,
            portfolioRisk,
            correlationRisk,
            tradingConditions,
            accountSafety,
            legacyManagement,
            improvedManagement,
            structuralManagement,
            regimeManagement,
            managementCalibration,
            metaModelPolicy,
            createdAt,
            setupCalibrationArtifactId,
            managementCalibrationArtifactId,
            metaModelArtifactId,
            description,
            includeProgressiveCompatibilityFields: false);

    public static TradingPolicyProfile Create(
        Guid profileId,
        int revision,
        string strategyId,
        string strategyVersion,
        TradingAgentDefinition agentDefinition,
        TradingPolicyProfileStatus status,
        RuntimeFeaturePolicy featurePolicy,
        PositionSizingOptions positionSizing,
        AdaptiveRiskOptions adaptiveRisk,
        PortfolioRiskOptions portfolioRisk,
        CorrelationRiskOptions correlationRisk,
        TradingConditionOptions tradingConditions,
        TradingSafetyOptions accountSafety,
        PositionManagementOptions legacyManagement,
        PositionManagementOptions improvedManagement,
        PositionManagementOptions structuralManagement,
        RegimeManagementOptions regimeManagement,
        TradeManagementCalibrationOptions managementCalibration,
        MetaModelPolicyOptions metaModelPolicy,
        DateTimeOffset createdAt,
        Guid? setupCalibrationArtifactId = null,
        Guid? managementCalibrationArtifactId = null,
        Guid? metaModelArtifactId = null,
        string? description = null,
        bool includeProgressiveCompatibilityFields = false)
    {
        ArgumentNullException.ThrowIfNull(agentDefinition);
        agentDefinition.Validate();
        ProgressiveAgentKind? compatibilityKind = agentDefinition.Kind switch
        {
            TradingAgentKind.LegacyProgressive => ProgressiveAgentKind.Legacy,
            TradingAgentKind.ImprovedProgressive => ProgressiveAgentKind.Improved,
            _ => null
        };
        var draft = new TradingPolicyProfile
        {
            SchemaVersion = 3,
            ProfileId = profileId,
            Revision = revision,
            StrategyId = strategyId,
            StrategyVersion = strategyVersion,
            AgentDefinition = agentDefinition.ToAgentDefinition(),
            AgentKind = includeProgressiveCompatibilityFields ? compatibilityKind : null,
            AgentOptions = includeProgressiveCompatibilityFields ? agentDefinition.Progressive : null,
            Status = status,
            FeaturePolicy = featurePolicy,
            SetupCalibrationArtifactId = setupCalibrationArtifactId,
            ManagementCalibrationArtifactId = managementCalibrationArtifactId,
            MetaModelArtifactId = metaModelArtifactId,
            MetaModelPolicy = metaModelPolicy,
            PositionSizing = positionSizing,
            AdaptiveRisk = adaptiveRisk,
            PortfolioRisk = portfolioRisk,
            CorrelationRisk = correlationRisk,
            TradingConditions = tradingConditions,
            AccountSafety = accountSafety,
            LegacyManagement = legacyManagement,
            ImprovedManagement = improvedManagement,
            StructuralManagement = structuralManagement,
            RegimeManagement = regimeManagement,
            ManagementCalibration = managementCalibration,
            CreatedAt = createdAt,
            Description = description,
            ConfigurationHash = string.Empty
        };
        TradingPolicyProfile result = draft with { ConfigurationHash = ComputeConfigurationHash(draft) };
        result.Validate();
        return result;
    }

    private static string ComputeConfigurationHash(TradingPolicyProfile profile)
    {
        object effectiveAgent = profile.SchemaVersion >= 3
            ? profile.EffectiveGenericAgentDefinition()
            : profile.EffectiveAgentDefinition();
        string canonical = JsonSerializer.Serialize(new
        {
            profile.StrategyId,
            profile.StrategyVersion,
            Agent = effectiveAgent,
            FeatureSchemaHash = profile.FeaturePolicy.ComputeHash(),
            profile.FeaturePolicy,
            profile.SetupCalibrationArtifactId,
            profile.ManagementCalibrationArtifactId,
            profile.MetaModelArtifactId,
            profile.MetaModelPolicy,
            profile.PositionSizing,
            profile.AdaptiveRisk,
            profile.PortfolioRisk,
            profile.CorrelationRisk,
            profile.TradingConditions,
            profile.AccountSafety,
            profile.LegacyManagement,
            profile.ImprovedManagement,
            profile.StructuralManagement,
            profile.RegimeManagement,
            profile.ManagementCalibration
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
