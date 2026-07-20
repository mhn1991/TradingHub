using System.Collections.Concurrent;
using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Experiments.Models;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

namespace Simulator.Experiments;

public sealed record BacktestSimulationExperimentExecutorOptions
{
    /// <summary>
    /// Operational diagnostic output. It does not participate in experiment/profile hashes.
    /// </summary>
    public bool CaptureMarketReplay { get; init; }
}

/// <summary>
/// Evaluation executor for disabled/pre-attached calibration profiles. Fresh fitting is
/// deliberately delegated to a calibration-aware host executor so this layer cannot claim
/// a frozen model when no trainer ran.
/// </summary>
public sealed class BacktestSimulationExperimentExecutor : ISimulationExperimentExecutor
{
    private readonly IBacktestApplicationService _backtests;
    private readonly ICalibrationArtifactRepository _artifacts;
    private readonly BacktestSimulationExperimentExecutorOptions _options;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, byte>> _children = new();

    public BacktestSimulationExperimentExecutor(
        IBacktestApplicationService backtests,
        ICalibrationArtifactRepository artifacts,
        BacktestSimulationExperimentExecutorOptions? options = null)
    {
        _backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _options = options ?? new BacktestSimulationExperimentExecutorOptions();
    }

    public Task<SimulationDatasetPreparationResult> PrepareDatasetsAsync(
        SimulationExperimentExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(new SimulationDatasetPreparationResult
    {
        Status = "PreparedByChildBacktests"
    });

    public async Task<SimulationProfileLearningResult> LearnAsync(
        SimulationExperimentExecutionContext context,
        SimulationExperimentProfileRun profileRun,
        CancellationToken cancellationToken)
    {
        CalibrationExperimentPolicy policy = profileRun.Profile.ResolvedProfile.Calibration;
        if (policy.Mode == ExperimentCalibrationMode.Disabled)
        {
            return new SimulationProfileLearningResult
            {
                Detail = "Calibration disabled; no fitting stage was run."
            };
        }

        if (policy.Mode == ExperimentCalibrationMode.ReuseSpecifiedArtifacts)
        {
            var references = new List<SimulationArtifactReference>(policy.ArtifactIds.Count);
            foreach (Guid artifactId in policy.ArtifactIds)
            {
                CalibrationArtifactMetadata metadata = await _artifacts.GetMetadataAsync(artifactId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Calibration artifact '{artifactId}' does not exist and cannot be reused.");
                references.Add(new SimulationArtifactReference
                {
                    ArtifactId = metadata.Id,
                    Kind = metadata.Type.ToString(),
                    ContentHash = metadata.ContentHash
                });
            }

            if (references.GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("An experiment profile cannot reuse multiple artifacts of the same calibration type.");

            return new SimulationProfileLearningResult
            {
                Artifacts = references,
                Detail = "Using the immutable artifact IDs specified by the profile."
            };
        }

        throw new NotSupportedException(
            "Fresh calibration requires a calibration-aware experiment executor. " +
            "The base simulator executor will not silently evaluate an unfitted model.");
    }

    public async Task<SimulationProfileEvaluationResult> EvaluateAsync(
        SimulationExperimentExecutionContext context,
        SimulationExperimentProfileRun profileRun,
        IReadOnlyList<SimulationArtifactReference> frozenArtifacts,
        CancellationToken cancellationToken)
    {
        SimulationStrategyProfile profile = profileRun.Profile.ResolvedProfile;
        AnalysisWarmupPlan warmup = context.Manifest.EvaluationWarmups[profileRun.ProfileRunId];
        string strategyType = TradingAgentTypeIds.Format(profile.Agent.Kind);
        (decimal quantity, decimal rewardRisk) = ResolveTradeDefaults(profile.Agent);
        FrozenCalibration calibration = await ResolveFrozenCalibrationAsync(
            profile.Calibration.Mode,
            frozenArtifacts,
            cancellationToken).ConfigureAwait(false);
        BacktestRuntimeOptions runtime = profile.Runtime.Options with
        {
            WarmupDays = warmup.ResolvedWarmupDays,
            AnnotationOptions = profile.Analysis,
            LegacyPositionManagement = profile.Management,
            ImprovedPositionManagement = profile.Management,
            MaximumParallelStrategies = 1,
            SetupCalibration = profile.Runtime.Options.SetupCalibration with
            {
                Enabled = calibration.Setup is not null
            },
            SetupCalibrationArtifact = calibration.Setup,
            ManagementCalibration = profile.Runtime.Options.ManagementCalibration with
            {
                Enabled = calibration.Management is not null
            },
            ManagementCalibrationArtifact = calibration.Management,
            MetaModel = profile.Runtime.Options.MetaModel with
            {
                Enabled = calibration.MetaModel is not null
            },
            MetaModelArtifact = calibration.MetaModel
        };
        var request = new BacktestRequest
        {
            Instrument = profile.Instrument,
            From = context.Manifest.Timeline.EvaluationFrom,
            To = context.Manifest.Timeline.EvaluationTo,
            Strategies = [strategyType],
            StrategyAssignments =
            [
                new StrategyInstrumentAssignment
                {
                    Id = profileRun.ProfileRunId.ToString("N"),
                    StrategyType = strategyType,
                    Instrument = profile.Instrument,
                    AgentDefinitionOverride = profile.Agent,
                    AnalysisOptionsOverride = profile.Analysis
                }
            ],
            Quantity = quantity,
            MinimumRewardRisk = rewardRisk,
            CaptureMarketReplay = _options.CaptureMarketReplay,
            Runtime = runtime
        };

        Guid childId = Guid.Empty;
        var progress = new Progress<BacktestProgress>(value =>
        {
            childId = value.SimulationId;
            RegisterChild(context.ExperimentId, value.SimulationId);
            context.ReportProfileProgress(
                profileRun.ProfileRunId,
                value.ProgressPercent,
                value.DataSourceStatus,
                value.SimulationId);
        });
        ComparativeSimulationResult result = await _backtests.RunToCompletionAsync(
            request,
            progress,
            cancellationToken).ConfigureAwait(false);
        childId = result.SimulationId;
        RegisterChild(context.ExperimentId, childId);
        StrategySimulationResult strategy = result.Strategies.Single();
        StrategyPerformanceSnapshot performance = StrategyPerformanceSnapshot.FromTrades(strategy.Result.Trades);
        string[] warnings = performance.TradeCount < 30 ? ["InsufficientTradeSample"] : [];
        return new SimulationProfileEvaluationResult
        {
            ChildJobId = childId,
            Comparison = new SimulationExperimentComparisonRow
            {
                ProfileRunId = profileRun.ProfileRunId,
                ProfileName = profile.Name,
                NetProfit = performance.NetProfit,
                NetR = strategy.Result.Trades.Where(item => item.RMultiple is not null)
                    .Sum(item => item.RMultiple!.Value),
                Expectancy = performance.Expectancy,
                MaximumDrawdown = performance.MaximumDrawdown,
                ProfitFactor = performance.ProfitFactor,
                TradeCount = performance.TradeCount,
                Warnings = warnings
            },
            Detail = $"Completed {result.ProcessedBaseCandles:N0} execution candles."
        };
    }

    public Task PauseChildrenAsync(Guid experimentId, CancellationToken cancellationToken) =>
        ApplyToChildrenAsync(experimentId, _backtests.PauseAsync, cancellationToken);

    public Task ResumeChildrenAsync(Guid experimentId, CancellationToken cancellationToken) =>
        ApplyToChildrenAsync(experimentId, _backtests.ResumeAsync, cancellationToken);

    public Task CancelChildrenAsync(Guid experimentId, CancellationToken cancellationToken) =>
        ApplyToChildrenAsync(experimentId, _backtests.CancelAsync, cancellationToken);

    private async Task ApplyToChildrenAsync(
        Guid experimentId,
        Func<Guid, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (!_children.TryGetValue(experimentId, out ConcurrentDictionary<Guid, byte>? children))
            return;
        foreach (Guid child in children.Keys)
        {
            try
            {
                await operation(child, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A child can finish between enumeration and the operation.
            }
        }
    }

    private void RegisterChild(Guid experimentId, Guid childId)
    {
        if (childId != Guid.Empty)
            _children.GetOrAdd(experimentId, _ => new ConcurrentDictionary<Guid, byte>())[childId] = 0;
    }

    private async Task<FrozenCalibration> ResolveFrozenCalibrationAsync(
        ExperimentCalibrationMode mode,
        IReadOnlyList<SimulationArtifactReference> references,
        CancellationToken cancellationToken)
    {
        if (mode is ExperimentCalibrationMode.Disabled or ExperimentCalibrationMode.TrainFreshPendingReviewOnly)
            return new FrozenCalibration();

        SetupCalibrationArtifact? setup = null;
        TradeManagementCalibration? management = null;
        MetaModelArtifact? metaModel = null;
        foreach (SimulationArtifactReference reference in references)
        {
            CalibrationArtifactMetadata metadata = await _artifacts
                .GetMetadataAsync(reference.ArtifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Frozen calibration artifact '{reference.ArtifactId}' no longer exists.");
            if (!string.Equals(metadata.ContentHash, reference.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Frozen calibration artifact '{reference.ArtifactId}' changed after the learning stage.");
            }

            switch (metadata.Type)
            {
                case CalibrationArtifactType.Setup when setup is null:
                    setup = await _artifacts.GetSetupAsync(reference.ArtifactId, cancellationToken).ConfigureAwait(false)
                        ?? throw MissingArtifactContent(reference.ArtifactId);
                    break;
                case CalibrationArtifactType.Management when management is null:
                    management = await _artifacts.GetManagementAsync(reference.ArtifactId, cancellationToken).ConfigureAwait(false)
                        ?? throw MissingArtifactContent(reference.ArtifactId);
                    break;
                case CalibrationArtifactType.MetaModel when metaModel is null:
                    metaModel = await _artifacts.GetMetaModelAsync(reference.ArtifactId, cancellationToken).ConfigureAwait(false)
                        ?? throw MissingArtifactContent(reference.ArtifactId);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Frozen calibration contains more than one '{metadata.Type}' artifact.");
            }
        }

        return new FrozenCalibration(setup, management, metaModel);
    }

    private static InvalidOperationException MissingArtifactContent(Guid artifactId) =>
        new($"Calibration artifact '{artifactId}' has metadata but its immutable content is missing.");

    private sealed record FrozenCalibration(
        SetupCalibrationArtifact? Setup = null,
        TradeManagementCalibration? Management = null,
        MetaModelArtifact? MetaModel = null);

    private static (decimal Quantity, decimal MinimumRewardRisk) ResolveTradeDefaults(
        TradingAgentDefinition definition) => definition.Kind switch
    {
        TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive =>
            (definition.Progressive!.Quantity, definition.Progressive.MinimumRewardRisk),
        TradingAgentKind.StructuralConfluence =>
            (definition.StructuralConfluence!.Quantity, definition.StructuralConfluence.MinimumRewardRisk),
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };
}
