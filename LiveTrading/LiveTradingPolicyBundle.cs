using PortfolioManager.Correlation;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Conditions;
using RiskManager.Safety;
using TradeManager;
using TradingCore.Pipeline;
using TradingPolicies;

namespace LiveTrading;

/// <summary>
/// Immutable, versioned declaration of the environment-neutral policy authorized for one
/// strategy deployment. The same policy may originate in simulator/research and be promoted into
/// shadow, manual, or automatic execution without changing Agent, risk, portfolio, condition, or
/// management behaviour. Broker credentials and activation mode are intentionally external.
/// </summary>
public sealed record LiveTradingPolicyBundle
{
    public required Guid PolicyBundleId { get; init; }
    public required int Revision { get; init; }
    public required string StrategyVersion { get; init; }
    public required string FeatureSchemaHash { get; init; }
    public required RuntimeFeaturePolicy FeaturePolicy { get; init; }
    public Guid? SetupCalibrationArtifactId { get; init; }
    public Guid? ManagementCalibrationArtifactId { get; init; }
    public Guid? MetaModelArtifactId { get; init; }
    public required PositionSizingOptions PositionSizing { get; init; }
    public required AdaptiveRiskOptions AdaptiveRisk { get; init; }
    public required PortfolioRiskOptions PortfolioRisk { get; init; }
    public required CorrelationRiskOptions CorrelationRisk { get; init; }
    public required TradingConditionOptions TradingConditions { get; init; }
    public required TradingSafetyOptions AccountSafety { get; init; }
    public required PositionManagementOptions LegacyManagement { get; init; }
    public required PositionManagementOptions ImprovedManagement { get; init; }
    public PositionManagementOptions StructuralManagement { get; init; } = PositionManagementOptions.StructuralDefaults;
    public required RegimeManagementOptions RegimeManagement { get; init; }
    public required string ConfigurationHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Description { get; init; }

    /// <summary>Adapts the canonical environment-neutral package to the live pipeline's legacy
    /// bundle shape. Only the selected management policy is meaningful for the selected Agent;
    /// populating all legacy slots with that same immutable policy prevents a second resolution
    /// path from silently choosing different management settings.</summary>
    public static LiveTradingPolicyBundle FromResolvedPackage(ResolvedAgentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new LiveTradingPolicyBundle
        {
            PolicyBundleId = package.PolicyId,
            Revision = package.Revision,
            StrategyVersion = package.StrategyVersion,
            FeatureSchemaHash = package.FeatureSchemaHash,
            FeaturePolicy = package.FeaturePolicy,
            SetupCalibrationArtifactId = package.SetupCalibrationArtifactId,
            ManagementCalibrationArtifactId = package.ManagementCalibrationArtifactId,
            MetaModelArtifactId = package.MetaModelArtifactId,
            PositionSizing = package.PositionSizing,
            AdaptiveRisk = package.AdaptiveRisk,
            PortfolioRisk = package.PortfolioRisk,
            CorrelationRisk = package.CorrelationRisk,
            TradingConditions = package.TradingConditions,
            AccountSafety = package.AccountSafety,
            LegacyManagement = package.PositionManagement,
            ImprovedManagement = package.PositionManagement,
            StructuralManagement = package.PositionManagement,
            RegimeManagement = package.RegimeManagement,
            ConfigurationHash = package.ConfigurationHash,
            CreatedAt = package.CreatedAt,
            Description = $"Resolved package {package.PackageHash}"
        };
    }

    /// <summary>
    /// Structural validation only. <see cref="FeatureSchemaHash"/> is cryptographically
    /// re-verified against <see cref="FeaturePolicy"/> (the two must always agree - this is the
    /// one field this type can prove is internally consistent). <see cref="ConfigurationHash"/>
    /// is checked only for presence: it identifies the bundle to external systems (calibration
    /// artifacts, journals, later live-host activation records) but nothing in Phase 0 defines a
    /// canonical serialization to recompute it from, so it is not re-derived here.
    /// </summary>
    public void Validate()
    {
        if (PolicyBundleId == Guid.Empty)
            throw new ArgumentException("PolicyBundleId must not be empty.", nameof(PolicyBundleId));
        if (Revision < 1)
            throw new ArgumentOutOfRangeException(nameof(Revision), "Revision must be at least 1.");
        if (string.IsNullOrWhiteSpace(StrategyVersion))
            throw new ArgumentException("StrategyVersion must not be blank.", nameof(StrategyVersion));
        if (string.IsNullOrWhiteSpace(FeatureSchemaHash))
            throw new ArgumentException("FeatureSchemaHash must not be blank.", nameof(FeatureSchemaHash));
        ArgumentNullException.ThrowIfNull(FeaturePolicy);
        if (!string.Equals(FeatureSchemaHash, FeaturePolicy.ComputeHash(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "FeatureSchemaHash does not match the content of FeaturePolicy.",
                nameof(FeatureSchemaHash));
        }
        if (string.IsNullOrWhiteSpace(ConfigurationHash))
            throw new ArgumentException("ConfigurationHash must not be blank.", nameof(ConfigurationHash));
        if (CreatedAt > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(CreatedAt), "CreatedAt must not be in the future.");

        ArgumentNullException.ThrowIfNull(PositionSizing);
        ArgumentNullException.ThrowIfNull(AdaptiveRisk);
        ArgumentNullException.ThrowIfNull(PortfolioRisk);
        ArgumentNullException.ThrowIfNull(CorrelationRisk);
        ArgumentNullException.ThrowIfNull(TradingConditions);
        ArgumentNullException.ThrowIfNull(AccountSafety);
        ArgumentNullException.ThrowIfNull(LegacyManagement);
        ArgumentNullException.ThrowIfNull(ImprovedManagement);
        ArgumentNullException.ThrowIfNull(StructuralManagement);
        ArgumentNullException.ThrowIfNull(RegimeManagement);
        ArgumentNullException.ThrowIfNull(FeaturePolicy.SetupCalibration);

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
        FeaturePolicy.SetupCalibration.Validate();
    }
}
