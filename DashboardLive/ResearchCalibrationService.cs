using System.Collections.Concurrent;
using Agent.Configuration;
using Brokers.Models;
using ChartAnnotator.Engine;
using QuantResearch.Training.Pipeline;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

namespace Dashboard.Live;

/// <summary>
/// HTTP-triggered wrapper around <see cref="CalibrationTrainingPipeline"/>. Runs research
/// calibrations (setup / management / meta-model) through the shared
/// <see cref="IBacktestApplicationService"/> and stores artifacts in the same repository the
/// simulator and live host already load - the calibration logic itself lives in
/// <c>QuantResearch.Training</c> so it isn't duplicated between this service, the CLI, and
/// LiveTradingHost's own background trigger.
/// </summary>
public sealed class ResearchCalibrationService
{
    private readonly CalibrationTrainingPipeline _pipeline;
    private readonly ILogger<ResearchCalibrationService> _logger;
    private readonly ConcurrentDictionary<Guid, ResearchJobSnapshot> _jobs = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _startGate = new();
    private Guid? _activeJobId;

    public ResearchCalibrationService(
        IBacktestApplicationService backtests,
        ICalibrationArtifactRepository artifacts,
        ILogger<ResearchCalibrationService> logger)
    {
        _pipeline = new CalibrationTrainingPipeline(backtests, artifacts);
        _logger = logger;
    }

    public IReadOnlyList<ResearchJobSnapshot> List(int take = 30) =>
        _jobs.Values
            .OrderByDescending(job => job.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToArray();

    public ResearchJobSnapshot? Get(Guid jobId) =>
        _jobs.TryGetValue(jobId, out ResearchJobSnapshot? job) ? job : null;

    public ResearchJobSnapshot Start(ResearchCalibrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string kind = NormalizeKind(request.Kind);
        if (string.IsNullOrWhiteSpace(request.Instrument))
            throw new ArgumentException("Instrument is required.");
        if (string.IsNullOrWhiteSpace(request.Strategy))
            throw new ArgumentException("Strategy is required.");
        string strategy = NormalizeStrategy(request.Strategy);
        if (request.From >= request.To)
            throw new ArgumentException("From must be earlier than To.");
        if (request.StartingBalance <= 0m || request.Quantity <= 0m)
            throw new ArgumentException("Starting balance and quantity must be positive.");
        if (request.WarmupDays < 0)
            throw new ArgumentOutOfRangeException(nameof(request.WarmupDays));
        if (!Enum.TryParse(request.SourceKind, ignoreCase: true, out HistoricalDataSourceKind _))
            throw new ArgumentException($"Unknown source kind '{request.SourceKind}'.");

        var jobId = Guid.NewGuid();
        var snapshot = new ResearchJobSnapshot
        {
            JobId = jobId,
            Kind = kind,
            Status = ResearchJobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            Instrument = request.Instrument.Trim(),
            Strategy = strategy,
            From = request.From,
            To = request.To,
            Message = "Queued behind any active simulation/research work."
        };
        _jobs[jobId] = snapshot;

        _ = Task.Run(async () =>
        {
            await _runGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_startGate)
                    _activeJobId = jobId;
                await ExecuteAsync(jobId, request, kind).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Research calibration job {JobId} failed.", jobId);
                Update(jobId, current => current with
                {
                    Status = ResearchJobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = ex.Message,
                    Message = "Failed."
                });
            }
            finally
            {
                lock (_startGate)
                {
                    if (_activeJobId == jobId)
                        _activeJobId = null;
                }
                _runGate.Release();
            }
        });

        return snapshot;
    }

    private async Task ExecuteAsync(Guid jobId, ResearchCalibrationRequest request, string kind)
    {
        Update(jobId, current => current with
        {
            Status = ResearchJobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            Message = "Running backtest for calibration window…"
        });

        if (!Enum.TryParse(request.SourceKind, ignoreCase: true, out HistoricalDataSourceKind sourceKind))
            throw new ArgumentException($"Unknown source kind '{request.SourceKind}'.");

        BacktestRuntimeOptions runtime = new()
        {
            SourceKind = sourceKind,
            BaseInterval = BarInterval.Minutes(1),
            ExecutionInterval = BarInterval.Minutes(1),
            AnalysisIntervals = RecommendedSimulationDefaults.AnalysisIntervals,
            StrategyTimeframes = RecommendedSimulationDefaults.StrategyTimeframes,
            WarmupDays = request.WarmupDays,
            AnnotationOptions = new ChartAnnotationOptions(),
            PrefetchCapacity = 50_000,
            PrefetchLowWatermark = 5_000,
            SourcePageSize = 5_000,
            StrategyExecutionMode = StrategyExecutionMode.Sequential,
            ProgressPublishIntervalMilliseconds = 250,
            ReplayChunkSize = 200
        };

        var instrument = new InstrumentKey(request.Instrument.Trim());
        string strategy = NormalizeStrategy(request.Strategy);
        string description = request.Description?.Trim() is { Length: > 0 } text
            ? text
            : $"{kind} calibration · {request.Instrument} · {strategy} · {request.From:yyyy-MM-dd}→{request.To:yyyy-MM-dd}";

        var trainingRequest = new CalibrationTrainingRequest
        {
            Instruments = [instrument],
            Strategies = [strategy],
            From = request.From,
            To = request.To,
            Runtime = runtime,
            StartingBalance = request.StartingBalance,
            Quantity = request.Quantity,
            Description = description,
            RunSetupStage = kind == "setup",
            RunMetaModelStage = kind == "metamodel",
            RunManagementStage = kind == "management",
            // The single-kind HTTP surface asks for one artifact at a time; management still
            // needs a frozen setup+meta-model pair to build its trade population against, so
            // fall back to whatever the caller already has approved for this strategy/instrument
            // if this exact job doesn't also (re)train those stages itself.
            ExistingSetupArtifactId = kind == "management" ? request.ExistingSetupArtifactId : null,
            ExistingMetaModelArtifactId = kind == "management" ? request.ExistingMetaModelArtifactId : null
        };

        var progress = new Progress<CalibrationTrainingSnapshot>(snapshot =>
        {
            CalibrationStageSnapshot stage = kind switch
            {
                "setup" => snapshot.SetupStage,
                "metamodel" => snapshot.MetaModelStage,
                _ => snapshot.ManagementStage
            };
            if (stage.Message is { Length: > 0 } message)
                Update(jobId, current => current with { Message = message });
        });

        CalibrationTrainingResult result = await _pipeline.RunAsync(trainingRequest, progress).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.FailureReason ?? "Calibration training failed.");

        Guid artifactId = kind switch
        {
            "setup" => result.SetupArtifactId!.Value,
            "metamodel" => result.MetaModelArtifactId!.Value,
            _ => result.ManagementArtifactId!.Value
        };
        string artifactType = kind switch
        {
            "setup" => "Setup",
            "metamodel" => "MetaModel",
            _ => "Management"
        };

        Update(jobId, current => current with
        {
            Status = ResearchJobStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            Message = "Completed.",
            ArtifactId = artifactId,
            ArtifactType = artifactType
        });
    }

    private void Update(Guid jobId, Func<ResearchJobSnapshot, ResearchJobSnapshot> mutator)
    {
        _jobs.AddOrUpdate(
            jobId,
            _ => throw new InvalidOperationException($"Unknown research job {jobId}."),
            (_, current) => mutator(current));
    }

    private static string NormalizeKind(string kind)
    {
        string normalized = kind.Trim().ToLowerInvariant();
        return normalized switch
        {
            "setup" or "setups" or "calibrate-setups" => "setup",
            "management" or "manage" or "calibrate-management" => "management",
            "metamodel" or "meta" or "meta-model" or "calibrate-metamodel" => "metamodel",
            _ => throw new ArgumentException(
                "Kind must be one of: setup, management, metamodel.")
        };
    }

    private static string NormalizeStrategy(string strategy) =>
        TradingAgentTypeIds.Format(TradingAgentTypeIds.Parse(strategy));
}
