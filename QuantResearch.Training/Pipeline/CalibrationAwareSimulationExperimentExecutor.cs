using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Experiments;
using Simulator.Experiments.Models;
using Simulator.Models;
using TradeManager;

namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Host-level experiment executor that adds leakage-safe fresh calibration to the simulator's
/// backtest executor. Each profile is trained independently over the manifest's learning range;
/// the resulting immutable artifact hashes are frozen before held-out evaluation starts.
/// </summary>
public sealed class CalibrationAwareSimulationExperimentExecutor : ISimulationExperimentExecutor
{
    private readonly BacktestSimulationExperimentExecutor _backtests;
    private readonly CalibrationTrainingPipeline _training;
    private readonly ICalibrationArtifactRepository _artifacts;

    public CalibrationAwareSimulationExperimentExecutor(
        BacktestSimulationExperimentExecutor backtests,
        CalibrationTrainingPipeline training,
        ICalibrationArtifactRepository artifacts)
    {
        _backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
        _training = training ?? throw new ArgumentNullException(nameof(training));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
    }

    public Task<SimulationDatasetPreparationResult> PrepareDatasetsAsync(
        SimulationExperimentExecutionContext context,
        CancellationToken cancellationToken) => _backtests.PrepareDatasetsAsync(context, cancellationToken);

    public async Task<SimulationProfileLearningResult> LearnAsync(
        SimulationExperimentExecutionContext context,
        SimulationExperimentProfileRun profileRun,
        CancellationToken cancellationToken)
    {
        SimulationStrategyProfile profile = profileRun.Profile.ResolvedProfile;
        if (profile.Calibration.Mode is ExperimentCalibrationMode.Disabled or
            ExperimentCalibrationMode.ReuseSpecifiedArtifacts)
        {
            return await _backtests.LearnAsync(context, profileRun, cancellationToken).ConfigureAwait(false);
        }

        AnalysisWarmupPlan warmup = context.Manifest.TrainingWarmups[profileRun.ProfileRunId];
        (decimal quantity, decimal rewardRisk, PriceActionConfirmationMode confirmation,
            decimal minimumConfidence, bool rejectOpposition) = ResolveTradeSettings(profile.Agent);
        BacktestRuntimeOptions runtime = profile.Runtime.Options with
        {
            WarmupDays = warmup.ResolvedWarmupDays,
            AnnotationOptions = profile.Analysis,
            LegacyPositionManagement = profile.Management,
            ImprovedPositionManagement = profile.Management,
            MaximumParallelStrategies = 1,
            SetupCalibration = profile.Runtime.Options.SetupCalibration with { Enabled = false },
            SetupCalibrationArtifact = null,
            ManagementCalibration = profile.Runtime.Options.ManagementCalibration with { Enabled = false },
            ManagementCalibrationArtifact = null,
            MetaModel = profile.Runtime.Options.MetaModel with { Enabled = false },
            MetaModelArtifact = null
        };
        string strategyType = TradingAgentTypeIds.Format(profile.Agent.Kind);
        var request = new CalibrationTrainingRequest
        {
            Instruments = [profile.Instrument],
            Strategies = [strategyType],
            AgentDefinition = profile.Agent,
            From = context.Manifest.Timeline.LearningFrom,
            To = context.Manifest.Timeline.LearningTo,
            Runtime = runtime,
            Quantity = quantity,
            MinimumRewardRisk = rewardRisk,
            PriceActionConfirmation = confirmation,
            MinimumPriceActionConfidence = minimumConfidence,
            RejectStrongOpposingPriceAction = rejectOpposition,
            Folds = profile.Calibration.InternalFolds,
            Embargo = TimeSpan.FromHours(profile.Calibration.InternalEmbargoHours),
            Description = $"Experiment {context.ExperimentId:N}; profile {profileRun.ProfileRunId:N}; " +
                $"learning [{context.Manifest.Timeline.LearningFrom:O}, {context.Manifest.Timeline.LearningTo:O})"
        };

        var progress = new Progress<CalibrationTrainingSnapshot>(snapshot =>
        {
            decimal completed = Count(snapshot, CalibrationStageStatus.Completed) +
                Count(snapshot, CalibrationStageStatus.Skipped);
            decimal running = Count(snapshot, CalibrationStageStatus.Running) * 0.5m;
            context.ReportProfileProgress(
                profileRun.ProfileRunId,
                Math.Min(99m, (completed + running) / 3m * 100m),
                snapshot.Message ?? CurrentStageMessage(snapshot),
                snapshot.RunId);
        });
        CalibrationTrainingResult result = await _training
            .RunAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success || result.SetupArtifactId is null || result.MetaModelArtifactId is null ||
            result.ManagementArtifactId is null)
        {
            throw new InvalidOperationException(
                result.FailureReason ?? "Fresh calibration did not produce a complete three-artifact bundle.");
        }

        SimulationArtifactReference[] references = await FreezeAsync(
            [result.SetupArtifactId.Value, result.MetaModelArtifactId.Value, result.ManagementArtifactId.Value],
            cancellationToken).ConfigureAwait(false);
        string detail = profile.Calibration.Mode == ExperimentCalibrationMode.TrainFreshPendingReviewOnly
            ? "Fresh artifacts were trained and frozen as pending review; held-out evaluation will run without them."
            : "Fresh profile-isolated artifacts were trained and frozen for held-out evaluation.";
        return new SimulationProfileLearningResult
        {
            ChildJobId = result.RunId,
            Artifacts = references,
            Detail = detail
        };
    }

    public Task<SimulationProfileEvaluationResult> EvaluateAsync(
        SimulationExperimentExecutionContext context,
        SimulationExperimentProfileRun profileRun,
        IReadOnlyList<SimulationArtifactReference> frozenArtifacts,
        CancellationToken cancellationToken) =>
        _backtests.EvaluateAsync(context, profileRun, frozenArtifacts, cancellationToken);

    public Task PauseChildrenAsync(Guid experimentId, CancellationToken cancellationToken) =>
        _backtests.PauseChildrenAsync(experimentId, cancellationToken);

    public Task ResumeChildrenAsync(Guid experimentId, CancellationToken cancellationToken) =>
        _backtests.ResumeChildrenAsync(experimentId, cancellationToken);

    public Task CancelChildrenAsync(Guid experimentId, CancellationToken cancellationToken) =>
        _backtests.CancelChildrenAsync(experimentId, cancellationToken);

    private async Task<SimulationArtifactReference[]> FreezeAsync(
        IReadOnlyList<Guid> artifactIds,
        CancellationToken cancellationToken)
    {
        var references = new List<SimulationArtifactReference>(artifactIds.Count);
        foreach (Guid artifactId in artifactIds)
        {
            CalibrationArtifactMetadata metadata = await _artifacts
                .GetMetadataAsync(artifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Fresh calibration artifact '{artifactId}' was not persisted.");
            references.Add(new SimulationArtifactReference
            {
                ArtifactId = metadata.Id,
                Kind = metadata.Type.ToString(),
                ContentHash = metadata.ContentHash
            });
        }

        if (references.Select(item => item.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new InvalidOperationException("Fresh calibration did not produce one setup, meta-model, and management artifact.");
        return references.ToArray();
    }

    private static decimal Count(CalibrationTrainingSnapshot snapshot, CalibrationStageStatus status) =>
        new[] { snapshot.SetupStage, snapshot.MetaModelStage, snapshot.ManagementStage }
            .Count(stage => stage.Status == status);

    private static string? CurrentStageMessage(CalibrationTrainingSnapshot snapshot) =>
        new[] { snapshot.SetupStage, snapshot.MetaModelStage, snapshot.ManagementStage }
            .FirstOrDefault(stage => stage.Status == CalibrationStageStatus.Running)?.Message;

    private static (decimal Quantity, decimal MinimumRewardRisk,
        PriceActionConfirmationMode Confirmation, decimal MinimumConfidence, bool RejectOpposition)
        ResolveTradeSettings(TradingAgentDefinition definition)
    {
        if (definition.Progressive is ProgressiveStrategyOptions progressive)
        {
            return (progressive.Quantity, progressive.MinimumRewardRisk,
                progressive.PriceActionConfirmation, progressive.MinimumPriceActionConfidence,
                progressive.RejectStrongOpposingPriceAction);
        }

        StructuralConfluenceStrategyOptions structural = definition.StructuralConfluence
            ?? throw new ArgumentException("The agent definition has no strategy options.", nameof(definition));
        return (structural.Quantity, structural.MinimumRewardRisk,
            PriceActionConfirmationMode.Soft, 55m, true);
    }
}
