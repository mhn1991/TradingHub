using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;
using TradeManager;
using TradingPolicies;

namespace QuantResearch.Training.Pipeline;

/// <summary>Outcome of <see cref="CalibrationBundleWorkflow.RunAndProposeAsync"/>.</summary>
public sealed record CalibrationBundlePromotionResult
{
    public required CalibrationTrainingResult Training { get; init; }
    /// <summary>Null when training failed, or when the three trained artifacts turned out to be mutually incompatible.</summary>
    public CalibrationBundleCandidate? Candidate { get; init; }
    public string? IncompatibilityReason { get; init; }
}

/// <summary>
/// Ties <see cref="CalibrationTrainingPipeline"/> to the approval workflow: runs training, and
/// on success, checks the three artifacts are actually compatible with each other before ever
/// proposing them as one bundle - never auto-promotes anything to
/// <see cref="TradingPolicyProfileStatus.ApprovedForDemo"/> itself. That only happens through
/// <see cref="ICalibrationBundleApprovalStore.ApproveAsync"/>, a separate, explicit, human action.
/// </summary>
public sealed class CalibrationBundleWorkflow(
    CalibrationTrainingPipeline pipeline,
    ICalibrationArtifactRepository artifacts,
    ICalibrationBundleApprovalStore approvals)
{
    public async Task<CalibrationBundlePromotionResult> RunAndProposeAsync(
        CalibrationBundlePromotionRequest request,
        IProgress<CalibrationTrainingSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        CalibrationTrainingResult trainingResult = await pipeline
            .RunAsync(request.Training, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!trainingResult.Success)
            return new CalibrationBundlePromotionResult { Training = trainingResult };

        Guid setupId = trainingResult.SetupArtifactId!.Value;
        Guid metaModelId = trainingResult.MetaModelArtifactId!.Value;
        Guid managementId = trainingResult.ManagementArtifactId!.Value;

        SetupCalibrationArtifact setupArtifact = await artifacts.GetSetupAsync(setupId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Setup artifact {setupId} vanished immediately after being stored.");
        MetaModelArtifact metaModelArtifact = await artifacts.GetMetaModelAsync(metaModelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Meta-model artifact {metaModelId} vanished immediately after being stored.");
        TradeManagementCalibration managementArtifact = await artifacts.GetManagementAsync(managementId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Management artifact {managementId} vanished immediately after being stored.");

        string? incompatibility = CheckCompatibility(setupArtifact, metaModelArtifact, request);
        if (incompatibility is not null)
            return new CalibrationBundlePromotionResult { Training = trainingResult, IncompatibilityReason = incompatibility };

        string strategyId = request.Training.Strategies[0];
        // AGENT-01: derived from the exact Runtime/RR/price-action settings that produced the
        // training data, not a caller-supplied value that could silently drift from it.
        Agent.Strategies.ProgressiveStrategyOptions agentOptions = request.Training.Runtime
            .ResolveProgressiveStrategyOptions(
                request.Training.Quantity,
                request.Training.MinimumRewardRisk,
                request.Training.PriceActionConfirmation,
                request.Training.MinimumPriceActionConfidence,
                request.Training.RejectStrongOpposingPriceAction);
        TradingPolicyProfile draftProfile = TradingPolicyPromotion.CreateProfile(
            request.Training.Runtime,
            strategyId,
            request.StrategyVersion,
            request.AgentKind,
            agentOptions,
            request.ProfileId,
            revision: 1,
            createdAt: DateTimeOffset.UtcNow,
            status: TradingPolicyProfileStatus.Research,
            setupCalibrationArtifactId: setupId,
            managementCalibrationArtifactId: managementId,
            metaModelArtifactId: metaModelId,
            description: request.Training.Description);

        CalibrationBundleCandidate candidate = await approvals
            .AddAsync(setupId, metaModelId, managementId, draftProfile, cancellationToken)
            .ConfigureAwait(false);

        return new CalibrationBundlePromotionResult { Training = trainingResult, Candidate = candidate };
    }

    /// <summary>
    /// Nothing in <see cref="ICalibrationArtifactRepository"/> stops attaching a setup artifact
    /// trained for one strategy to a meta-model trained for another, so this check exists
    /// specifically to catch that before a candidate is ever created - "never mix incompatible
    /// artifact revisions."
    /// </summary>
    private static string? CheckCompatibility(
        SetupCalibrationArtifact setup, MetaModelArtifact metaModel, CalibrationBundlePromotionRequest request)
    {
        if (!string.Equals(setup.FeatureSchemaHash, metaModel.FeatureSchemaHash, StringComparison.Ordinal))
            return $"Setup artifact feature schema '{setup.FeatureSchemaHash}' does not match meta-model artifact schema '{metaModel.FeatureSchemaHash}'.";
        // SetupCalibrationArtifact.StrategyVersion records the joined strategy ids the training
        // backtest actually covered (the same convention QuantResearchRunner's CLI has always
        // used) - not the human-supplied semantic StrategyVersion on the promotion request, which
        // is a different, unrelated field. Compatibility means "trained for the strategy set this
        // bundle is being promoted for."
        string expectedStrategyScope = string.Join(",", request.Training.Strategies);
        if (!string.Equals(setup.StrategyVersion, expectedStrategyScope, StringComparison.OrdinalIgnoreCase))
            return $"Setup artifact was trained for strategy scope '{setup.StrategyVersion}', not the requested '{expectedStrategyScope}'.";
        if (setup.TotalSamples <= 0 || setup.Buckets.Count == 0)
            return "Setup artifact has no calibration buckets.";
        if (metaModel.Buckets.Count == 0)
            return "Meta-model artifact has no calibration buckets.";
        return null;
    }
}
