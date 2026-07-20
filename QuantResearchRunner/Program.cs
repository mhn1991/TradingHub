using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Configuration;
using DBManager.Postgres.Security;
using Microsoft.EntityFrameworkCore;
using QuantResearch.Analysis;
using QuantResearch.Calibration;
using QuantResearch.Models;
using QuantResearch.Validation;
using QuantResearch.Training.Experiments;
using QuantResearch.Training.Mapping;
using QuantResearch.Training.Pipeline;
using QuantResearchRunner.Experiments;
using QuantResearchRunner.Mapping;
using QuantResearchRunner.Models;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Experiments;
using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;
using TradeManager;
using TradingHub.Persistence.Postgres.Bootstrap;
using TradingHub.Persistence.Postgres.Calibration;
using TradingHub.Persistence.Postgres.Simulation;

namespace QuantResearchRunner;

public static class Program
{
    private static StandaloneTradingHubContextFactory? _contextFactory;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            await InitializePersistenceAsync().ConfigureAwait(false);
            return args[0] switch
            {
                "walk-forward" => await RunWalkForwardAsync(args).ConfigureAwait(false),
                "monte-carlo" => await RunMonteCarloAsync(args).ConfigureAwait(false),
                "neo-wave-attribution" => await RunNeoWaveAttributionAsync(args).ConfigureAwait(false),
                "ablation" => await RunAblationAsync(args).ConfigureAwait(false),
                "sensitivity" => await RunSensitivityAsync(args).ConfigureAwait(false),
                "calibrate-setups" => await RunCalibrateSetupsAsync(args).ConfigureAwait(false),
                "calibrate-management" => await RunCalibrateManagementAsync(args).ConfigureAwait(false),
                "calibrate-metamodel" => await RunCalibrateMetaModelAsync(args).ConfigureAwait(false),
                "experiment" => await RunSimulationExperimentAsync(args).ConfigureAwait(false),
                string verb => Unknown(verb)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunWalkForwardAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: walk-forward <plan.json>");
            return 1;
        }

        QuantResearchPlan plan = await LoadPlanAsync(args[1]).ConfigureAwait(false);
        plan.Validate();

        string experimentId = Guid.NewGuid().ToString("N");
        string experimentDirectory = Path.Combine(plan.OutputDirectory, experimentId);
        Directory.CreateDirectory(experimentDirectory);
        QuantResearchPlan scopedPlan = plan with { OutputDirectory = experimentDirectory };

        await WriteJsonAsync(Path.Combine(experimentDirectory, "plan.json"), scopedPlan).ConfigureAwait(false);

        await using var service = new BacktestApplicationService(
            CreateSimulationJobRepository(),
            new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 4 });
        var ledger = new ExperimentLedger(experimentDirectory);

        IReadOnlyList<WalkForwardFoldResult> results = await WalkForwardExperimentRunner
            .RunAsync(scopedPlan, service, ledger)
            .ConfigureAwait(false);

        await WriteJsonAsync(Path.Combine(experimentDirectory, "folds.json"), results).ConfigureAwait(false);
        foreach (WalkForwardFoldResult result in results)
        {
            string safeInstrument = result.Instrument.Replace('/', '_').Replace(':', '_');
            string foldDirectory = Path.Combine(
                experimentDirectory,
                $"fold-{result.Window.Fold}-{safeInstrument}-{result.StrategyId}");
            Directory.CreateDirectory(foldDirectory);
            await WriteJsonAsync(Path.Combine(foldDirectory, "training.json"), result.Training).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(foldDirectory, "validation.json"), result.Validation).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(foldDirectory, "test.json"), result.Test).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(foldDirectory, "selected-parameters.json"), result.SelectedParameters)
                .ConfigureAwait(false);
        }

        await WriteJsonAsync(Path.Combine(experimentDirectory, "manifest.json"), new
        {
            ExperimentId = experimentId,
            plan.From,
            plan.To,
            FoldCount = results.Count,
            CreatedAt = DateTimeOffset.UtcNow
        }).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(experimentDirectory, "COMPLETE"),
            DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);

        Console.WriteLine($"Wrote {results.Count} fold result(s) to {experimentDirectory}");
        return 0;
    }

    private static async Task<int> RunMonteCarloAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out Guid simulationId))
        {
            Console.Error.WriteLine("Usage: monte-carlo <simulation-id> [--jobs-directory dir] [--output dir]");
            return 1;
        }

        string outputDirectory = ReadOption(args, "--output")
            ?? Path.Combine("research", "monte-carlo", simulationId.ToString("N"));

        ISimulationJobRepository repository = CreateSimulationJobRepository();
        SimulationJobSnapshot? snapshot = await repository.GetAsync(simulationId).ConfigureAwait(false);
        if (snapshot?.OutputDirectory is not string simulationOutputDirectory)
        {
            Console.Error.WriteLine($"No completed simulation found for id '{simulationId}' in PostgreSQL.");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        foreach (StrategyProgressSnapshot strategy in snapshot.Strategies)
        {
            IReadOnlyList<SimulatedTradeRecord> trades = await PersistedTradeReader
                .ReadAsync(simulationOutputDirectory, strategy.StrategyId)
                .ConfigureAwait(false);
            IReadOnlyList<ResearchTrade> researchTrades = ResearchTradeMapper.ToResearchTrades(trades);
            MonteCarloReport report = MonteCarloSimulator.Run(researchTrades);
            await WriteJsonAsync(Path.Combine(outputDirectory, $"{strategy.StrategyId}.json"), report)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"Wrote Monte Carlo report(s) to {outputDirectory}");
        return 0;
    }

    private static async Task<int> RunNeoWaveAttributionAsync(string[] args)
    {
        if (args.Length < 2 || !Guid.TryParse(args[1], out Guid simulationId))
        {
            Console.Error.WriteLine(
                "Usage: neo-wave-attribution <simulation-id> [--jobs-directory dir] [--output dir] " +
                "[--minimum-samples n]");
            return 1;
        }

        string outputDirectory = ReadOption(args, "--output") ??
            Path.Combine("research", "neo-wave-attribution", simulationId.ToString("N"));
        int minimumSamples = int.TryParse(ReadOption(args, "--minimum-samples"), out int parsedMinimum)
            ? parsedMinimum
            : 5;

        var options = new NeoWaveAttributionOptions { MinimumCohortSamples = minimumSamples };
        options.Validate();

        ISimulationJobRepository repository = CreateSimulationJobRepository();
        SimulationJobSnapshot? snapshot = await repository.GetAsync(simulationId).ConfigureAwait(false);
        if (snapshot?.OutputDirectory is not string simulationOutputDirectory)
        {
            Console.Error.WriteLine(
                $"No completed simulation found for id '{simulationId}' in PostgreSQL.");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        foreach (StrategyProgressSnapshot strategy in snapshot.Strategies)
        {
            IReadOnlyList<SimulatedTradeRecord> trades = await PersistedTradeReader
                .ReadAsync(simulationOutputDirectory, strategy.StrategyId)
                .ConfigureAwait(false);
            IReadOnlyList<ResearchTrade> researchTrades =
                ResearchTradeMapper.ToResearchTrades(trades);
            NeoWaveAttributionReport report =
                NeoWaveAttribution.Analyze(researchTrades, options);
            await WriteJsonAsync(
                    Path.Combine(outputDirectory, $"{strategy.StrategyId}.json"),
                    report)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"Wrote NEoWave attribution report(s) to {outputDirectory}");
        return 0;
    }

    private static async Task<int> RunAblationAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ablation <plan.json>");
            return 1;
        }

        (QuantResearchPlan plan, string experimentDirectory) = await LoadScopedPlanAsync(args[1]).ConfigureAwait(false);

        await using var service = BuildService(experimentDirectory);
        Func<FeatureSwitches, CancellationToken, Task<ResearchPerformance>> evaluate =
            ExperimentEvaluators.BuildAblationEvaluator(plan, service);
        IReadOnlyList<FeatureAblationResult> results = await FeatureAblationRunner
            .RunAsync(plan.FeatureSwitches, evaluate)
            .ConfigureAwait(false);

        await WriteJsonAsync(Path.Combine(experimentDirectory, "ablation.json"), results).ConfigureAwait(false);
        await MarkCompleteAsync(experimentDirectory).ConfigureAwait(false);
        Console.WriteLine($"Wrote ablation results ({results.Count} feature(s)) to {experimentDirectory}");
        return 0;
    }

    private static async Task<int> RunSensitivityAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: sensitivity <plan.json>");
            return 1;
        }

        (QuantResearchPlan plan, string experimentDirectory) = await LoadScopedPlanAsync(args[1]).ConfigureAwait(false);

        await using var service = BuildService(experimentDirectory);
        Func<IReadOnlyDictionary<string, decimal>, CancellationToken, Task<ResearchPerformance>> evaluate =
            ExperimentEvaluators.BuildSensitivityEvaluator(plan, service);
        IReadOnlyList<ParameterSensitivityPoint> results = await ParameterSensitivityRunner
            .RunAsync(plan.ParameterGrid, evaluate)
            .ConfigureAwait(false);

        await WriteJsonAsync(Path.Combine(experimentDirectory, "sensitivity.json"), results).ConfigureAwait(false);
        await MarkCompleteAsync(experimentDirectory).ConfigureAwait(false);
        Console.WriteLine($"Wrote sensitivity results ({results.Count} combination(s)) to {experimentDirectory}");
        return 0;
    }

    private static async Task<int> RunCalibrateSetupsAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: calibrate-setups <plan.json> [--artifacts-directory dir]");
            return 1;
        }

        (QuantResearchPlan plan, string experimentDirectory) = await LoadScopedPlanAsync(args[1]).ConfigureAwait(false);
        CalibrationTrainingResult result = await RunCalibrationPipelineAsync(
            plan, experimentDirectory, args,
            runSetup: true, runMetaModel: false, runManagement: false).ConfigureAwait(false);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Setup calibration failed: {result.FailureReason}");
            return 1;
        }

        await MarkCompleteAsync(experimentDirectory).ConfigureAwait(false);
        Console.WriteLine($"Stored setup calibration artifact {result.SetupArtifactId} to {experimentDirectory}");
        return 0;
    }

    private static async Task<int> RunCalibrateManagementAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "Usage: calibrate-management <plan.json> [--artifacts-directory dir] " +
                "[--setup-artifact-id id --metamodel-artifact-id id]");
            return 1;
        }

        (QuantResearchPlan plan, string experimentDirectory) = await LoadScopedPlanAsync(args[1]).ConfigureAwait(false);
        string? setupId = ReadOption(args, "--setup-artifact-id");
        string? metaModelId = ReadOption(args, "--metamodel-artifact-id");
        bool freezeExisting = setupId is not null && metaModelId is not null;

        CalibrationTrainingResult result = await RunCalibrationPipelineAsync(
            plan, experimentDirectory, args,
            runSetup: !freezeExisting, runMetaModel: !freezeExisting, runManagement: true,
            existingSetupArtifactId: freezeExisting ? Guid.Parse(setupId!) : null,
            existingMetaModelArtifactId: freezeExisting ? Guid.Parse(metaModelId!) : null).ConfigureAwait(false);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Management calibration failed: {result.FailureReason}");
            return 1;
        }

        await MarkCompleteAsync(experimentDirectory).ConfigureAwait(false);
        Console.WriteLine(
            $"Stored management calibration artifact {result.ManagementArtifactId} to {experimentDirectory}" +
            (freezeExisting ? "" : $" (also (re)trained setup={result.SetupArtifactId}, metamodel={result.MetaModelArtifactId} - " +
                "management calibration always freezes a setup+meta-model entry policy first; pass " +
                "--setup-artifact-id/--metamodel-artifact-id to reuse existing ones instead of retraining them)"));
        return 0;
    }

    private static async Task<int> RunCalibrateMetaModelAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: calibrate-metamodel <plan.json> [--artifacts-directory dir]");
            return 1;
        }

        (QuantResearchPlan plan, string experimentDirectory) = await LoadScopedPlanAsync(args[1]).ConfigureAwait(false);
        CalibrationTrainingResult result = await RunCalibrationPipelineAsync(
            plan, experimentDirectory, args,
            runSetup: true, runMetaModel: true, runManagement: false).ConfigureAwait(false);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Meta-model calibration failed: {result.FailureReason}");
            return 1;
        }

        await MarkCompleteAsync(experimentDirectory).ConfigureAwait(false);
        Console.WriteLine(
            $"Stored meta-model artifact {result.MetaModelArtifactId} to {experimentDirectory} " +
            $"(also (re)trained setup={result.SetupArtifactId} - meta-model calibration always trains on stage 1's " +
            "out-of-fold setup scores, so it cannot run standalone)");
        return 0;
    }

    /// <summary>
    /// Shared entry point for all three calibrate-* verbs: builds a leakage-safe
    /// <see cref="CalibrationTrainingRequest"/> from a <see cref="QuantResearchPlan"/> and runs
    /// it through <see cref="CalibrationTrainingPipeline"/> - see that type for the purged
    /// cross-validation / out-of-fold / reserved-test-window mechanics this replaces the old
    /// single-window fit with.
    /// </summary>
    private static async Task<CalibrationTrainingResult> RunCalibrationPipelineAsync(
        QuantResearchPlan plan,
        string experimentDirectory,
        string[] args,
        bool runSetup,
        bool runMetaModel,
        bool runManagement,
        Guid? existingSetupArtifactId = null,
        Guid? existingMetaModelArtifactId = null)
    {
        string experimentId = Path.GetFileName(experimentDirectory);
        var repository = new PostgresCalibrationArtifactRepository(
            _contextFactory ?? throw new InvalidOperationException("PostgreSQL persistence has not been initialized."),
            TimeProvider.System);
        await using BacktestApplicationService service = BuildService(experimentDirectory);
        var pipeline = new CalibrationTrainingPipeline(service, repository);

        var request = new CalibrationTrainingRequest
        {
            Instruments = plan.Instruments,
            Strategies = plan.Strategies,
            From = plan.From,
            To = plan.To,
            Runtime = plan.BaselineRuntime,
            StartingBalance = plan.StartingBalance,
            Quantity = plan.Quantity,
            Description = $"walk-forward plan {experimentId}",
            RunSetupStage = runSetup,
            RunMetaModelStage = runMetaModel,
            RunManagementStage = runManagement,
            ExistingSetupArtifactId = existingSetupArtifactId,
            ExistingMetaModelArtifactId = existingMetaModelArtifactId
        };

        var progress = new Progress<CalibrationTrainingSnapshot>(snapshot =>
        {
            if (snapshot.Message is { Length: > 0 } message)
                Console.WriteLine(message);
        });

        return await pipeline.RunAsync(request, progress).ConfigureAwait(false);
    }

    private static async Task<int> RunSimulationExperimentAsync(string[] args)
    {
        string experimentsDirectory = ReadOption(args, "--experiments-directory") ??
            Path.Combine(".cache", "simulation-experiments");
        string? outputPath = ReadOption(args, "--output");
        string? resumeValue = ReadOption(args, "--resume");

        SimulationExperimentRequest? request = null;
        Guid? resumeId = null;
        if (resumeValue is not null)
        {
            if (!Guid.TryParse(resumeValue, out Guid parsed))
                throw new ArgumentException("--resume requires an experiment GUID.");
            resumeId = parsed;
        }
        else
        {
            string? planPath = ReadOption(args, "--plan") ??
                (args.Length > 1 && !args[1].StartsWith("-", StringComparison.Ordinal) ? args[1] : null);
            if (planPath is null || !File.Exists(planPath))
                throw new ArgumentException("Usage: experiment --plan experiment.json [options], or experiment --resume id [options].");
            await using FileStream stream = File.OpenRead(planPath);
            request = await JsonSerializer.DeserializeAsync<SimulationExperimentRequest>(stream, JsonOptions)
                .ConfigureAwait(false)
                ?? throw new ArgumentException($"Could not parse experiment plan '{planPath}'.");
            request.Validate();
        }

        StandaloneTradingHubContextFactory contextFactory = _contextFactory
            ?? throw new InvalidOperationException("PostgreSQL persistence has not been initialized.");
        var experimentRepository = new PostgresSimulationExperimentRepository(contextFactory);
        SimulationExperimentParallelism parallelism = request?.Parallelism ??
            (await experimentRepository.GetAsync(resumeId!.Value).ConfigureAwait(false))?.Manifest.Parallelism ??
            throw new KeyNotFoundException($"Experiment '{resumeId:N}' was not found.");
        var profileStore = new PostgresSimulationStrategyProfileStore(contextFactory);
        var artifactRepository = new PostgresCalibrationArtifactRepository(contextFactory, TimeProvider.System);
        await using var backtests = new BacktestApplicationService(
            CreateSimulationJobRepository(),
            new BacktestApplicationServiceOptions
            {
                MaxConcurrentJobs = Math.Max(1, parallelism.MaxProfileGroups),
                QueueCapacity = Math.Max(4, parallelism.MaxProfileGroups * 2)
            });
        using var governor = new SimulationResourceGovernor(new SimulationResourceGovernorOptions
        {
            MaxConcurrentExperiments = 1,
            MaxConcurrentProfileGroups = parallelism.MaxProfileGroups,
            MaxHistoricalDownloadsPerBroker = parallelism.MaxHistoricalDownloadsPerBroker,
            MaxTotalStrategyWorkers = parallelism.MaxTotalStrategyWorkers
        });
        var baseExecutor = new BacktestSimulationExperimentExecutor(backtests, artifactRepository);
        var training = new CalibrationTrainingPipeline(backtests, artifactRepository);
        var executor = new CalibrationAwareSimulationExperimentExecutor(baseExecutor, training, artifactRepository);
        await using var experiments = new SimulationExperimentApplicationService(
            experimentRepository,
            profileStore,
            executor,
            governor,
            new SimulationExperimentApplicationServiceOptions { DispatcherCount = 1, QueueCapacity = 2 });

        Guid experimentId;
        if (resumeId is { } existingId)
        {
            await experiments.ResumeAsync(existingId).ConfigureAwait(false);
            experimentId = existingId;
            Console.WriteLine($"Resumed experiment {experimentId:N}.");
        }
        else
        {
            SimulationExperimentHandle handle = await experiments.StartAsync(request!).ConfigureAwait(false);
            experimentId = handle.Id;
            Console.WriteLine($"Accepted experiment {experimentId:N}.");
        }

        int lastRevision = -1;
        SimulationExperimentSnapshot final;
        while (true)
        {
            final = await experiments.GetAsync(experimentId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Experiment '{experimentId:N}' disappeared.");
            if (final.Revision != lastRevision)
            {
                Console.WriteLine($"[{final.Revision}] {final.State}/{final.Stage}");
                lastRevision = final.Revision;
            }
            if (final.IsTerminal)
                break;
            await Task.Delay(250).ConfigureAwait(false);
        }

        outputPath ??= Path.Combine(experimentsDirectory, experimentId.ToString("N"), "cli-result.json");
        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        await WriteJsonAsync(outputPath, final).ConfigureAwait(false);
        Console.WriteLine($"Wrote resolved experiment snapshot to {outputPath}");
        if (final.State == SimulationExperimentState.Completed)
            return 0;
        Console.Error.WriteLine(final.FailureReason ?? $"Experiment ended in state {final.State}.");
        return 1;
    }


    private static async Task<(QuantResearchPlan Plan, string ExperimentDirectory)> LoadScopedPlanAsync(string planPath)
    {
        QuantResearchPlan plan = await LoadPlanAsync(planPath).ConfigureAwait(false);
        plan.Validate();
        string experimentDirectory = Path.Combine(plan.OutputDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(experimentDirectory);
        QuantResearchPlan scopedPlan = plan with { OutputDirectory = experimentDirectory };
        await WriteJsonAsync(Path.Combine(experimentDirectory, "plan.json"), scopedPlan).ConfigureAwait(false);
        return (scopedPlan, experimentDirectory);
    }

    private static BacktestApplicationService BuildService(string experimentDirectory) => new(
        CreateSimulationJobRepository(),
        new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 4 });

    private static ISimulationJobRepository CreateSimulationJobRepository() =>
        new PostgresSimulationJobRepository(
            _contextFactory ?? throw new InvalidOperationException("PostgreSQL persistence has not been initialized."),
            TimeProvider.System);

    private static async Task InitializePersistenceAsync()
    {
        BrokerCredentialDatabaseConfiguration? configuration =
            BrokerCredentialDatabaseConfiguration.TryLoad(Directory.GetCurrentDirectory(), out _);
        if (configuration is null)
            throw new InvalidOperationException(
                "PostgreSQL configuration is required at .state/tradinghub.database.json.");

        var contextFactory = new StandaloneTradingHubContextFactory(configuration.ConnectionString);
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        if (!await context.Database.CanConnectAsync().ConfigureAwait(false))
            throw new InvalidOperationException("TradingHub PostgreSQL is unavailable.");
        string[] pending = (await context.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToArray();
        if (pending.Length != 0)
            throw new InvalidOperationException(
                $"TradingHub PostgreSQL has {pending.Length} pending migration(s); apply them before running research.");
        _contextFactory = contextFactory;
    }

    private static Task MarkCompleteAsync(string experimentDirectory) =>
        File.WriteAllTextAsync(Path.Combine(experimentDirectory, "COMPLETE"), DateTimeOffset.UtcNow.ToString("O"));

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task<QuantResearchPlan> LoadPlanAsync(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"Plan file '{path}' does not exist.");
        await using FileStream stream = File.OpenRead(path);
        QuantResearchPlan? plan = await JsonSerializer
            .DeserializeAsync<QuantResearchPlan>(stream, JsonOptions)
            .ConfigureAwait(false);
        if (plan is null)
            throw new ArgumentException($"Could not parse plan file '{path}'.");
        if (plan.Strategies is null)
            throw new ArgumentException("Plan strategies are required.");
        return plan with
        {
            Strategies = plan.Strategies
                .Select(strategy => TradingAgentTypeIds.Format(TradingAgentTypeIds.Parse(strategy)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions).ConfigureAwait(false);
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown verb '{verb}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            QuantResearchRunner - operational research workflows over real backtests (audit Section 14-17).

            Usage:
              dotnet run --project QuantResearchRunner -- walk-forward plan.json
              dotnet run --project QuantResearchRunner -- monte-carlo simulation-id [--jobs-directory dir] [--output dir]
              dotnet run --project QuantResearchRunner -- neo-wave-attribution simulation-id [--jobs-directory dir] [--output dir] [--minimum-samples n]
              dotnet run --project QuantResearchRunner -- ablation plan.json
              dotnet run --project QuantResearchRunner -- sensitivity plan.json
              dotnet run --project QuantResearchRunner -- calibrate-setups plan.json [--artifacts-directory dir]
              dotnet run --project QuantResearchRunner -- calibrate-management plan.json [--artifacts-directory dir]
              dotnet run --project QuantResearchRunner -- calibrate-metamodel plan.json [--artifacts-directory dir]
              dotnet run --project QuantResearchRunner -- experiment --plan experiment.json [--profiles-directory dir] [--output file]
              dotnet run --project QuantResearchRunner -- experiment --resume experiment-id [--experiments-directory dir] [--output file]
            """);
    }
}
