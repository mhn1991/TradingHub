using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public required Guid ProfileId { get; init; }
    public required int Revision { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required TradingPolicyProfileStatus Status { get; init; }
    public required ProgressiveAgentKind AgentKind { get; init; }
    public required ProgressiveStrategyOptions AgentOptions { get; init; }
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
    public RegimeManagementOptions RegimeManagement { get; init; } = new();
    public TradeManagementCalibrationOptions ManagementCalibration { get; init; } = new();

    public required DateTimeOffset CreatedAt { get; init; }
    public string? Description { get; init; }
    public required string ConfigurationHash { get; init; }

    public void Validate()
    {
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

        ArgumentNullException.ThrowIfNull(AgentOptions);
        ArgumentNullException.ThrowIfNull(FeaturePolicy);
        AgentOptions.Validate();
        FeaturePolicy.SetupCalibration.Validate();
        PositionSizing.Validate();
        AdaptiveRisk.Validate();
        PortfolioRisk.Validate();
        CorrelationRisk.Validate();
        TradingConditions.Validate();
        AccountSafety.Validate();
        LegacyManagement.Validate();
        ImprovedManagement.Validate();
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
        RegimeManagementOptions regimeManagement,
        TradeManagementCalibrationOptions managementCalibration,
        MetaModelPolicyOptions metaModelPolicy,
        DateTimeOffset createdAt,
        Guid? setupCalibrationArtifactId = null,
        Guid? managementCalibrationArtifactId = null,
        Guid? metaModelArtifactId = null,
        string? description = null)
    {
        var draft = new TradingPolicyProfile
        {
            ProfileId = profileId,
            Revision = revision,
            StrategyId = strategyId,
            StrategyVersion = strategyVersion,
            AgentKind = agentKind,
            AgentOptions = agentOptions,
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
        string canonical = JsonSerializer.Serialize(new
        {
            profile.StrategyId,
            profile.StrategyVersion,
            profile.AgentKind,
            profile.AgentOptions,
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
            profile.RegimeManagement,
            profile.ManagementCalibration
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
