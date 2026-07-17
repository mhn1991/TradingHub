using LiveTrading;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;
using TradingPolicies;

namespace LiveTradingHost.Configuration;

/// <summary>Everything the live strategy factory needs after resolving one promoted policy.</summary>
public sealed record ResolvedPolicyBundle
{
    public required LiveTradingPolicyBundle Bundle { get; init; }
    public SetupCalibrationArtifact? SetupCalibration { get; init; }
    public ISetupMetaModel? MetaModel { get; init; }
    public TradeManagementCalibration? ManagementCalibration { get; init; }
    public TradeManagementCalibrationOptions ManagementCalibrationOptions { get; init; } = new();
    public required TradingPolicyProfile Profile { get; init; }
}

/// <summary>
/// Resolves calibration artifacts for an immutable profile promoted from research. No live-only
/// copy of strategy or risk settings is accepted here, preventing simulator/demo policy drift.
/// </summary>
public sealed class LivePolicyBundleFactory(
    ICalibrationArtifactRepository artifacts,
    TimeProvider timeProvider)
{
    public async Task<ResolvedPolicyBundle> BuildAsync(
        string bundleKey,
        LivePolicyBundleOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleKey);
        ArgumentNullException.ThrowIfNull(options);
        TradingPolicyProfile profile = options.Profile ?? throw new InvalidOperationException(
            $"Policy bundle '{bundleKey}' requires a promoted TradingPolicyProfile.");
        profile.Validate();

        SetupCalibrationArtifact? setupArtifact = profile.SetupCalibrationArtifactId is { } setupId
            ? await artifacts.GetSetupAsync(setupId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Policy bundle '{bundleKey}': setup calibration artifact {setupId} was not found.")
            : null;

        TradeManagementCalibration? managementCalibration = profile.ManagementCalibrationArtifactId is { } managementId
            ? await artifacts.GetManagementAsync(managementId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Policy bundle '{bundleKey}': management calibration artifact {managementId} was not found.")
            : null;
        profile.ManagementCalibration.Validate();
        if (profile.ManagementCalibration.Enabled && managementCalibration is null)
        {
            throw new InvalidOperationException(
                $"Policy bundle '{bundleKey}' enables management calibration without an artifact.");
        }

        // AGENT-02: gate on Enabled too, matching Simulator's construction (which correctly
        // checks runtime.MetaModel.Enabled). TradingPolicyProfile.Validate() above already
        // guarantees these two agree for any profile that reaches this point, but the explicit
        // Enabled check keeps this construction path self-evidently correct even if that
        // invariant is ever loosened.
        ISetupMetaModel? metaModel = null;
        if (profile.MetaModelPolicy.Enabled && profile.MetaModelArtifactId is { } metaModelId)
        {
            MetaModelArtifact artifact = await artifacts.GetMetaModelAsync(metaModelId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Policy bundle '{bundleKey}': meta-model artifact {metaModelId} was not found.");
            metaModel = new CalibratedSetupMetaModel(artifact, profile.MetaModelPolicy);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (profile.CreatedAt > now)
            throw new InvalidOperationException($"Policy bundle '{bundleKey}' has a future CreatedAt value.");

        var bundle = new LiveTradingPolicyBundle
        {
            PolicyBundleId = profile.ProfileId,
            Revision = profile.Revision,
            StrategyVersion = profile.StrategyVersion,
            FeatureSchemaHash = profile.FeaturePolicy.ComputeHash(),
            FeaturePolicy = profile.FeaturePolicy,
            SetupCalibrationArtifactId = profile.SetupCalibrationArtifactId,
            ManagementCalibrationArtifactId = profile.ManagementCalibrationArtifactId,
            MetaModelArtifactId = profile.MetaModelArtifactId,
            PositionSizing = profile.PositionSizing,
            AdaptiveRisk = profile.AdaptiveRisk,
            PortfolioRisk = profile.PortfolioRisk,
            CorrelationRisk = profile.CorrelationRisk,
            TradingConditions = profile.TradingConditions,
            AccountSafety = profile.AccountSafety,
            LegacyManagement = profile.LegacyManagement,
            ImprovedManagement = profile.ImprovedManagement,
            RegimeManagement = profile.RegimeManagement,
            ConfigurationHash = profile.ConfigurationHash,
            CreatedAt = profile.CreatedAt,
            Description = options.DeploymentDescription ?? profile.Description
        };
        bundle.Validate();

        return new ResolvedPolicyBundle
        {
            Bundle = bundle,
            SetupCalibration = setupArtifact,
            MetaModel = metaModel,
            ManagementCalibration = managementCalibration,
            ManagementCalibrationOptions = profile.ManagementCalibration,
            Profile = profile
        };
    }
}
