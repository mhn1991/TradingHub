using Agent.Configuration;
using Brokers.Models;
using Microsoft.Extensions.Logging;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;
using TradeManager;

namespace QuantResearch.Training.Pipeline;

public sealed record PreRunCalibrationRequest
{
    public required InstrumentKey Instrument { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
    public TradingAgentDefinition? AgentDefinition { get; init; }
    public required DateTimeOffset EvaluationFrom { get; init; }
    public required DateTimeOffset EvaluationTo { get; init; }
    public required BacktestRuntimeOptions Runtime { get; init; }
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Quantity { get; init; } = 1_000m;
    public int TrainMonths { get; init; } = PreRunCalibrationPlanner.DefaultTrainMonths;
    public int EmbargoDays { get; init; } = PreRunCalibrationPlanner.DefaultEmbargoDays;
    public string? Description { get; init; }
}

public sealed record PreRunCalibrationResult
{
    public required PreRunCalibrationWindow Window { get; init; }
    public required SetupCalibrationArtifact Setup { get; init; }
    public required MetaModelArtifact MetaModel { get; init; }
    public required TradeManagementCalibration Management { get; init; }
    public required Guid SetupArtifactId { get; init; }
    public required Guid MetaModelArtifactId { get; init; }
    public required Guid ManagementArtifactId { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
}

/// <summary>
/// Trains setup → meta → management for each requested catalog strategy on a
/// leakage-safe window ending before simulation evaluation, with strategy chains running in
/// parallel and stages remaining sequential within each chain.
/// </summary>
public sealed class PreRunCalibrationService
{
    private readonly CalibrationTrainingPipeline _pipeline;
    private readonly ICalibrationArtifactRepository _artifacts;
    private readonly ILogger<PreRunCalibrationService>? _logger;

    public PreRunCalibrationService(
        CalibrationTrainingPipeline pipeline,
        ICalibrationArtifactRepository artifacts,
        ILogger<PreRunCalibrationService>? logger = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _logger = logger;
    }

    public async Task<PreRunCalibrationResult> TrainAsync(
        PreRunCalibrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Strategies is null || request.Strategies.Count == 0)
            throw new ArgumentException("At least one strategy is required.", nameof(request));
        if (request.EvaluationFrom >= request.EvaluationTo)
            throw new ArgumentException("Evaluation From must be earlier than To.");

        string[] strategies = request.Strategies
            .Select(NormalizeStrategy)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (strategies.Length == 0)
            throw new ArgumentException("At least one strategy is required.", nameof(request));

        PreRunCalibrationWindow window = PreRunCalibrationPlanner.Resolve(
            request.EvaluationFrom,
            request.TrainMonths,
            request.EmbargoDays);

        _logger?.LogInformation(
            "Pre-run calibration for {Instrument}: train {TrainFrom:yyyy-MM-dd}→{TrainTo:yyyy-MM-dd} " +
            "(embargo {EmbargoDays}d) then eval {EvalFrom:yyyy-MM-dd}→{EvalTo:yyyy-MM-dd}; strategies={Strategies}",
            request.Instrument.Value,
            window.TrainFrom,
            window.TrainTo,
            window.EmbargoDays,
            request.EvaluationFrom,
            request.EvaluationTo,
            string.Join(',', strategies));

        // Strip any residual calibration policy so training backtests are unfiltered raw samples.
        BacktestRuntimeOptions trainingRuntime = request.Runtime with
        {
            SetupCalibration = new SetupCalibrationPolicyOptions { Enabled = false },
            SetupCalibrationArtifact = null,
            ManagementCalibration = new TradeManagementCalibrationOptions { Enabled = false },
            ManagementCalibrationArtifact = null,
            MetaModel = new MetaModelPolicyOptions { Enabled = false },
            MetaModelArtifact = null,
            DetailedExcursionTracking = true,
            StrategyExecutionMode = StrategyExecutionMode.Sequential
        };

        // Strategy chains are independent: run full setup→meta→management in parallel.
        CalibrationTrainingResult[] results = await Task.WhenAll(strategies.Select(strategy =>
        {
            var training = new CalibrationTrainingRequest
            {
                Instruments = [request.Instrument],
                Strategies = [strategy],
                AgentDefinition = request.AgentDefinition,
                From = window.TrainFrom,
                To = window.TrainTo,
                Runtime = trainingRuntime,
                StartingBalance = request.StartingBalance,
                Quantity = request.Quantity,
                // Prefer fewer folds so modest trade counts still form a reserved test + CV folds.
                // WithAdaptiveFolds further shrinks this when samples are sparse.
                Folds = 3,
                Embargo = TimeSpan.FromHours(12),
                RunSetupStage = true,
                RunMetaModelStage = true,
                RunManagementStage = true,
                Description = request.Description ??
                    $"pre-run auto-cal · {request.Instrument.Value} · {strategy} · " +
                    $"{window.TrainFrom:yyyy-MM-dd}→{window.TrainTo:yyyy-MM-dd}"
            };
            return _pipeline.RunAsync(training, progress: null, cancellationToken);
        })).ConfigureAwait(false);

        for (int i = 0; i < strategies.Length; i++)
        {
            CalibrationTrainingResult result = results[i];
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Pre-run calibration failed for strategy '{strategies[i]}' on train window " +
                    $"{window.TrainFrom:yyyy-MM-dd}→{window.TrainTo:yyyy-MM-dd} " +
                    $"(eval starts {request.EvaluationFrom:yyyy-MM-dd} after {window.EmbargoDays}d gap). " +
                    $"{result.FailureReason ?? "Unknown failure."} " +
                    "Widen the evaluation From date (which expands the train window), or disable " +
                    "auto-calibration and attach manual artifact IDs.");
            }

            if (result.SetupArtifactId is null ||
                result.MetaModelArtifactId is null ||
                result.ManagementArtifactId is null)
            {
                throw new InvalidOperationException(
                    $"Pre-run calibration for '{strategies[i]}' completed without all three artifact ids.");
            }
        }

        var setups = new List<SetupCalibrationArtifact>(strategies.Length);
        var metas = new List<MetaModelArtifact>(strategies.Length);
        var managements = new List<TradeManagementCalibration>(strategies.Length);
        for (int i = 0; i < strategies.Length; i++)
        {
            Guid setupId = results[i].SetupArtifactId!.Value;
            Guid metaId = results[i].MetaModelArtifactId!.Value;
            Guid managementId = results[i].ManagementArtifactId!.Value;
            setups.Add(await _artifacts.GetSetupAsync(setupId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Missing setup artifact {setupId}."));
            metas.Add(await _artifacts.GetMetaModelAsync(metaId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Missing meta-model artifact {metaId}."));
            managements.Add(await _artifacts.GetManagementAsync(managementId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Missing management artifact {managementId}."));
        }

        string mergeId = $"prerun-{Guid.NewGuid():N}";
        SetupCalibrationArtifact mergedSetup = CalibrationArtifactMerger.MergeSetup(setups, $"{mergeId}-setup");
        MetaModelArtifact mergedMeta = CalibrationArtifactMerger.MergeMetaModel(metas, $"{mergeId}-meta");
        TradeManagementCalibration mergedManagement =
            CalibrationArtifactMerger.MergeManagement(managements, $"{mergeId}-mgmt");

        string description = request.Description ??
            $"pre-run auto-cal merged · {request.Instrument.Value} · {string.Join(',', strategies)} · " +
            $"{window.TrainFrom:yyyy-MM-dd}→{window.TrainTo:yyyy-MM-dd}";

        CalibrationArtifactMetadata setupMeta = await _artifacts
            .StoreSetupAsync(mergedSetup, description, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        CalibrationArtifactMetadata metaMeta = await _artifacts
            .StoreMetaModelAsync(mergedMeta, description, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        CalibrationArtifactMetadata managementMeta = await _artifacts
            .StoreManagementAsync(mergedManagement, description, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new PreRunCalibrationResult
        {
            Window = window,
            Setup = mergedSetup,
            MetaModel = mergedMeta,
            Management = mergedManagement,
            SetupArtifactId = setupMeta.Id,
            MetaModelArtifactId = metaMeta.Id,
            ManagementArtifactId = managementMeta.Id,
            Strategies = strategies
        };
    }

    private static string NormalizeStrategy(string strategy)
        => TradingAgentTypeIds.Format(TradingAgentTypeIds.Parse(strategy));
}
