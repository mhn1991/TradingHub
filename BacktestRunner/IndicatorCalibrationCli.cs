using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Strategies.StructuralConfluence;
using Brokers.Abstractions;
using Brokers.Models;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres.Security;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Services;

namespace BacktestRunner;

/// <summary>
/// <c>indicator-calibration &lt;preview|start|status|list|pause|resume|cancel|approve|reject|artifact&gt;</c>
/// subcommands (blueprint §17.3). A thin CLI adapter over
/// <see cref="IndicatorCalibrationApplicationService"/> - the same application-service path the
/// DashboardLive API uses.
/// </summary>
internal static class IndicatorCalibrationCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "indicator-calibration", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            PrintHelp();
            return 1;
        }

        string subcommand = args[1].ToLowerInvariant();
        if (subcommand == "generate-request")
            return GenerateRequest(args);

        string? requestPath = ReadOption(args, "--request");
        string? id = ReadOption(args, "--id");
        Guid? artifactId = ReadOption(args, "--artifact-id") is { } rawArtifactId
            ? Guid.Parse(rawArtifactId)
            : null;
        string artifactsDirectory = ReadOption(args, "--artifacts-directory") ?? Path.Combine(".cache", "calibration-artifacts");
        string ledgerDirectory = ReadOption(args, "--ledger-directory") ?? Path.Combine(".cache", "indicator-calibration-ledgers");

        var artifacts = new FileCalibrationArtifactRepository(artifactsDirectory);
        var ledgers = new FileIndicatorCalibrationLedgerRepository(ledgerDirectory);

        BrokerCredentialDatabaseConfiguration? databaseConfiguration =
            BrokerCredentialDatabaseConfiguration.TryLoad(Directory.GetCurrentDirectory(), out string repositoryRoot);
        IBrokerCredentialStore? credentialStore = databaseConfiguration?.OpenStore(repositoryRoot);

        await using var backtests = new BacktestApplicationService(
            new FileSimulationJobRepository(Path.Combine(".cache", "simulation-jobs")),
            new BacktestApplicationServiceOptions
            {
                MaxConcurrentJobs = 1,
                QueueCapacity = 4,
                CredentialResolver = credentialStore is null
                    ? null
                    : async (backtest, token) =>
                    {
                        string environment = backtest.Environment == BrokerEnvironment.Live ? "LIVE" : "DEMO";
                        string broker = backtest.Runtime.SourceKind == HistoricalDataSourceKind.BinanceCandles ? "BINANCE" : "OANDA";
                        BrokerCredential? credential = await credentialStore.GetAsync(
                            broker, broker == "BINANCE" && environment == "DEMO" ? "TESTNET" : environment,
                            cancellationToken: token).ConfigureAwait(false);
                        return credential is null
                            ? null
                            : new HistoricalBrokerCredentials(
                                credential.AccountId, credential.AccessToken, credential.ApiKey, credential.SecretKey);
                    }
            });

        await using var service = new IndicatorCalibrationApplicationService(
            backtests, artifacts, ledgers,
            [
                new IndicatorConfluenceCalibrationStrategyAdapter(),
                new LiquidityBreakRetestCalibrationStrategyAdapter(),
                new LiquiditySweepReversalCalibrationStrategyAdapter(),
                new SupplyDemandPullbackCalibrationStrategyAdapter()
            ]);

        return subcommand switch
        {
            "preview" => await PreviewAsync(service, requestPath, cancellationToken).ConfigureAwait(false),
            "start" => await StartAsync(service, requestPath, HasFlag(args, "--accept-budget"), cancellationToken).ConfigureAwait(false),
            "status" => await StatusAsync(service, id, cancellationToken).ConfigureAwait(false),
            "list" => await ListAsync(service, cancellationToken).ConfigureAwait(false),
            "pause" => await ApplyAsync(() => service.PauseAsync(id ?? RequireId(), cancellationToken)).ConfigureAwait(false),
            "resume" => await ResumeCommandAsync(service, id ?? RequireId(), cancellationToken).ConfigureAwait(false),
            "cancel" => await ApplyAsync(() => service.CancelAsync(id ?? RequireId(), cancellationToken)).ConfigureAwait(false),
            "approve" => await ApproveAsync(service, args, id, artifactId, cancellationToken).ConfigureAwait(false),
            "reject" => await RejectAsync(service, args, id, artifactId, cancellationToken).ConfigureAwait(false),
            "artifact" => await ArtifactAsync(service, id, cancellationToken).ConfigureAwait(false),
            "pending" => await PendingAsync(service, cancellationToken).ConfigureAwait(false),
            "review" => await ReviewAsync(artifacts, artifactId, cancellationToken).ConfigureAwait(false),
            _ => PrintUnknown(subcommand)
        };
    }

    private static async Task<int> PreviewAsync(
        IIndicatorCalibrationApplicationService service, string? requestPath, CancellationToken cancellationToken)
    {
        IndicatorCalibrationRequest request = await LoadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        CalibrationBudgetPreview preview = await service.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        await PrintAsync(preview, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> StartAsync(
        IIndicatorCalibrationApplicationService service, string? requestPath, bool acceptBudget, CancellationToken cancellationToken)
    {
        IndicatorCalibrationRequest request = await LoadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        CalibrationBudgetPreview preview = await service.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Budget preview:");
        await PrintAsync(preview, cancellationToken).ConfigureAwait(false);

        if (!acceptBudget)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Estimated {preview.TotalPlannedEvaluations} evaluations, each a real backtest run " +
                "(measured cost: roughly 90-110 seconds per evaluation over a multi-week window). " +
                "A full search is typically a multi-hour operation. Re-run with --accept-budget to submit.");
            return 1;
        }

        IndicatorCalibrationRunSummary summary = await service.StartAsync(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Started calibration run {summary.CalibrationId} ({summary.StrategyId} / {summary.Instrument}).");
        return await PollUntilTerminalAsync(service, summary.CalibrationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a paused (or process-lost-track-of) run and keeps this process alive polling until
    /// it reaches a terminal state - mirroring <see cref="StartAsync"/>'s own polling loop. This is
    /// essential, not cosmetic: <see cref="IndicatorCalibrationApplicationService.ResumeAsync"/>
    /// only fires the work as a background <c>Task.Run</c> and returns immediately, so a bare
    /// resume-then-exit CLI invocation (as this used to be) would cancel the resumed work again
    /// almost instantly via the enclosing <c>await using</c> service disposal - a nightly script
    /// calling "resume" would silently do zero additional work.
    /// </summary>
    private static async Task<int> ResumeCommandAsync(
        IIndicatorCalibrationApplicationService service, string calibrationId, CancellationToken cancellationToken)
    {
        try
        {
            await service.ResumeAsync(calibrationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        Console.WriteLine($"Resumed calibration run {calibrationId}.");
        return await PollUntilTerminalAsync(service, calibrationId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> PollUntilTerminalAsync(
        IIndicatorCalibrationApplicationService service, string calibrationId, CancellationToken cancellationToken)
    {
        Console.WriteLine("Polling for completion - this can take a long time; Ctrl+C to stop polling (the run keeps going in this process until it exits).");
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            IndicatorCalibrationRunDetails? details = await service.GetAsync(calibrationId, cancellationToken).ConfigureAwait(false);
            if (details is null)
                continue;
            Console.WriteLine($"[{DateTimeOffset.UtcNow:u}] {details.Summary.State}" +
                (details.Summary.Outcome is { } outcome ? $" - {outcome}" : ""));
            if (details.Summary.State is IndicatorCalibrationRunState.Completed or
                IndicatorCalibrationRunState.Failed or IndicatorCalibrationRunState.Cancelled or
                IndicatorCalibrationRunState.Paused)
            {
                await PrintAsync(details, cancellationToken).ConfigureAwait(false);
                return details.Summary.State == IndicatorCalibrationRunState.Completed ? 0 : 1;
            }
        }
        return 1;
    }

    private static async Task<int> StatusAsync(
        IIndicatorCalibrationApplicationService service, string? id, CancellationToken cancellationToken)
    {
        IndicatorCalibrationRunDetails? details = await service.GetAsync(RequireId(id), cancellationToken).ConfigureAwait(false);
        if (details is null)
        {
            Console.WriteLine($"No calibration run '{id}' is known to this process.");
            return 1;
        }
        await PrintAsync(details, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ListAsync(IIndicatorCalibrationApplicationService service, CancellationToken cancellationToken)
    {
        IReadOnlyList<IndicatorCalibrationRunSummary> runs = await service.ListAsync(cancellationToken).ConfigureAwait(false);
        await PrintAsync(runs, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ApproveAsync(
        IIndicatorCalibrationApplicationService service, string[] args, string? id, Guid? artifactId, CancellationToken cancellationToken)
    {
        try
        {
            if (id is null && artifactId is null)
                throw new ArgumentException("Either --id <calibrationId> or --artifact-id <guid> is required.");
            CalibrationPromotionEvent promotionEvent = await service.ApproveAsync(new IndicatorCalibrationApprovalRequest
            {
                CalibrationId = id,
                ArtifactId = artifactId,
                ApprovedBy = ReadOption(args, "--approved-by") ?? throw new ArgumentException("--approved-by is required."),
                TargetEnvironment = ReadOption(args, "--target-environment") ?? throw new ArgumentException("--target-environment is required."),
                ReviewNotes = ReadOption(args, "--review-notes") ?? string.Empty,
                ReplacesArtifactId = ReadOption(args, "--replaces-artifact-id")
            }, cancellationToken).ConfigureAwait(false);
            await PrintAsync(promotionEvent, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine($"Approve failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RejectAsync(
        IIndicatorCalibrationApplicationService service, string[] args, string? id, Guid? artifactId, CancellationToken cancellationToken)
    {
        try
        {
            if (id is null && artifactId is null)
                throw new ArgumentException("Either --id <calibrationId> or --artifact-id <guid> is required.");
            await service.RejectAsync(new IndicatorCalibrationRejectionRequest
            {
                CalibrationId = id,
                ArtifactId = artifactId,
                RejectedBy = ReadOption(args, "--rejected-by") ?? throw new ArgumentException("--rejected-by is required."),
                Reason = ReadOption(args, "--reason") ?? string.Empty
            }, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Rejected.");
            return 0;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException)
        {
            Console.WriteLine($"Reject failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ArtifactAsync(
        IIndicatorCalibrationApplicationService service, string? id, CancellationToken cancellationToken)
    {
        IndicatorCalibrationRunDetails? details = await service.GetAsync(RequireId(id), cancellationToken).ConfigureAwait(false);
        if (details?.Artifact is null)
        {
            Console.WriteLine($"No artifact is available for calibration run '{id}' yet.");
            return 1;
        }
        await PrintAsync(details.Artifact, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Lists every indicator-calibration artifact currently awaiting review, regardless of which
    /// process (or which night) produced it - this is what makes "what needs my approval" a real
    /// queue rather than something only visible while the originating run's process is still alive.
    /// </summary>
    private static async Task<int> PendingAsync(IIndicatorCalibrationApplicationService service, CancellationToken cancellationToken)
    {
        IReadOnlyList<IndicatorCalibrationPendingApproval> pending = await service.ListPendingApprovalsAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            Console.WriteLine("Nothing is awaiting approval.");
            return 0;
        }

        Console.WriteLine($"{pending.Count} artifact(s) awaiting review:");
        Console.WriteLine();
        foreach (IndicatorCalibrationPendingApproval item in pending.OrderByDescending(entry => entry.Artifact.CreatedAt))
        {
            IndicatorCalibrationArtifact artifact = item.Artifact;
            Console.WriteLine(
                $"  {item.ArtifactId}  {artifact.StrategyId,-32}  {artifact.Instrument,-12}  " +
                $"{artifact.Outcome,-18}  improvement={artifact.Evidence.ImprovementOverBaseline:F3}R  " +
                $"folds={artifact.Evidence.AcceptableFoldPercent:F0}%  {artifact.CreatedAt:u}");
        }
        Console.WriteLine();
        Console.WriteLine("Use 'indicator-calibration review --artifact-id <guid>' for full evidence before approving.");
        return 0;
    }

    /// <summary>
    /// Human-readable review of one artifact - the evidence a reviewer needs to decide, not a raw
    /// JSON dump. Reads directly from the artifact repository, so this works for an artifact from
    /// any prior run, including one from a process that is long gone.
    /// </summary>
    private static async Task<int> ReviewAsync(
        ICalibrationArtifactRepository artifacts, Guid? artifactId, CancellationToken cancellationToken)
    {
        if (artifactId is not { } id)
            throw new ArgumentException("--artifact-id <guid> is required.");
        IndicatorCalibrationArtifact? artifact = await artifacts.GetIndicatorParametersAsync(id, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            Console.WriteLine($"No indicator-calibration artifact '{id}' exists.");
            return 1;
        }

        CalibrationEvidenceSummary evidence = artifact.Evidence;
        Console.WriteLine($"""
            Artifact:            {id}
            Calibration run:     {artifact.CalibrationId}
            Strategy:            {artifact.StrategyId} (impl {artifact.StrategyImplementationVersion}, manifest {artifact.ManifestVersion})
            Instrument:          {artifact.Instrument}
            Outcome:             {artifact.Outcome}
            Promotion status:    {artifact.PromotionStatus}
            Created:             {artifact.CreatedAt:u}

            --- Cross-fold walk-forward evidence ---
            Folds:                        {evidence.AcceptableFoldCount} / {evidence.FoldCount} acceptable ({evidence.AcceptableFoldPercent:F1}%)
            Median validation expectancy:  {evidence.MedianValidationExpectancyR:F3}R
            Median validation drawdown:    {evidence.MedianValidationDrawdownR:F3}R
            Median validation trades:      {evidence.MedianValidationTradeCount}
            Train->validation degradation: {evidence.TrainValidationDegradation:F3}
            Baseline median expectancy:    {evidence.BaselineMedianExpectancyR:F3}R

            --- Final external holdout (never touched during search) ---
            Candidate expectancy:  {evidence.ExternalHoldoutExpectancyR:F3}R
            Candidate drawdown:    {evidence.ExternalHoldoutDrawdownR:F3}R
            Candidate trades:      {evidence.ExternalHoldoutTradeCount}
            Baseline expectancy:   {evidence.ExternalHoldoutBaselineExpectancyR:F3}R
            Improvement over baseline: {evidence.ImprovementOverBaseline:F3}R

            Total candidates evaluated across the whole search: {evidence.TotalCandidatesEvaluated}
            """);

        if (artifact.Overrides.Count == 0)
        {
            Console.WriteLine("No numeric parameter changed from baseline.");
        }
        else
        {
            Console.WriteLine("--- Parameter changes (default -> calibrated) ---");
            foreach (CalibratedParameterOverride item in artifact.Overrides)
            {
                Console.WriteLine(
                    $"  {item.ParameterId,-32} {item.DefaultValue} -> {item.CalibratedValue}  " +
                    $"(fold support {item.FoldSupportPercent:F0}%, plateau width {item.PlateauWidth}, via {item.SelectionStage})");
            }
        }

        if (artifact.AblationOverrides.Count > 0)
        {
            Console.WriteLine("--- Ablation changes ---");
            foreach ((string parameterId, bool value) in artifact.AblationOverrides)
                Console.WriteLine($"  {parameterId,-32} -> {value}");
        }

        Console.WriteLine();
        Console.WriteLine(artifact.Outcome == CalibrationOutcome.Improved
            ? "This artifact CAN be approved: 'indicator-calibration approve --artifact-id " + id + " --approved-by <name> --target-environment <env>'"
            : $"This artifact CANNOT be approved (Outcome = {artifact.Outcome}, not Improved).");
        return 0;
    }

    private static async Task<int> ApplyAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            Console.WriteLine("Done.");
            return 0;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or NotSupportedException or InvalidOperationException)
        {
            Console.WriteLine(exception.Message);
            return exception is NotSupportedException ? 2 : 1;
        }
    }

    /// <summary>
    /// Builds a complete <see cref="IndicatorCalibrationRequest"/> for one instrument/strategy over
    /// a rolling window ending "now" (or an explicit <c>--as-of</c> date) and writes it as JSON -
    /// what a nightly script runs before <c>start --accept-budget</c>, so the operator never
    /// hand-writes the request (in particular <see cref="Brokers.Models.BarInterval"/>'s
    /// <c>{value,unit}</c> JSON shape). Defaults <c>--overflow-policy</c> to
    /// <see cref="CalibrationBudgetOverflowPolicy.ReduceRefinement"/> (not <c>Reject</c>) so an
    /// unattended run self-sizes to fit <c>--hard-runtime-limit-hours</c> instead of refusing to
    /// start - appropriate for a fixed overnight maintenance window.
    /// </summary>
    private static int GenerateRequest(string[] args)
    {
        try
        {
            string strategyName = ReadOption(args, "--strategy")
                ?? throw new ArgumentException(
                    "--strategy <indicator-confluence|liquidity-break-retest|liquidity-sweep-reversal|" +
                    "supply-demand-pullback> is required.");
            string instrumentValue = ReadOption(args, "--instrument")
                ?? throw new ArgumentException("--instrument <key> is required, e.g. FX:EUR/USD.");
            string outputPath = ReadOption(args, "--output")
                ?? throw new ArgumentException("--output <path.json> is required.");

            int learningDays = ReadIntOption(args, "--learning-days", 60);
            int holdoutDays = ReadIntOption(args, "--holdout-days", 14);
            int embargoDays = ReadIntOption(args, "--embargo-days", 2);
            int warmupDays = ReadIntOption(args, "--warmup-days", 10);
            int foldCount = ReadIntOption(args, "--fold-count", 3);
            int hardRuntimeLimitHours = ReadIntOption(args, "--hard-runtime-limit-hours", 8);
            int randomSeed = ReadIntOption(args, "--random-seed", 1);
            // UtcNow.Date is a DateTime with Kind=Unspecified; implicit DateTimeOffset conversion
            // would apply the local offset and fail CalibrationTimeline.Validate() ("must be UTC").
            DateTimeOffset asOf = ReadOption(args, "--as-of") is { } rawAsOf
                ? DateTimeOffset.Parse(rawAsOf).ToUniversalTime()
                : new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            CalibrationBudgetOverflowPolicy overflowPolicy = ReadOption(args, "--overflow-policy") is { } rawPolicy
                ? Enum.Parse<CalibrationBudgetOverflowPolicy>(rawPolicy, ignoreCase: true)
                : CalibrationBudgetOverflowPolicy.ReduceRefinement;
            BarInterval executionInterval = ReadOption(args, "--execution-interval") is { } rawInterval
                ? BarIntervalParser.Parse(rawInterval)
                : BarInterval.Minutes(1);
            // Also the strategy's own trigger/decision interval (Part 2 of this session's timeframe
            // parametrization) - execution precision and decision cadence are the same knob here,
            // there is no reason to annotate/decide at a finer granularity than the strategy uses.
            BarInterval setupInterval = ReadOption(args, "--setup-interval") is { } rawSetup
                ? BarIntervalParser.Parse(rawSetup)
                : StandardTimeframeTopologyFactory.DefaultSetupInterval;
            BarInterval contextInterval = ReadOption(args, "--context-interval") is { } rawContext
                ? BarIntervalParser.Parse(rawContext)
                : StandardTimeframeTopologyFactory.DefaultContextInterval;
            if (BarIntervalParser.CompareDuration(executionInterval, setupInterval) > 0 ||
                BarIntervalParser.CompareDuration(setupInterval, contextInterval) > 0)
            {
                throw new ArgumentException(
                    $"Timeframe stack must satisfy execution-interval ({BarIntervalParser.Format(executionInterval)}) <= " +
                    $"setup-interval ({BarIntervalParser.Format(setupInterval)}) <= " +
                    $"context-interval ({BarIntervalParser.Format(contextInterval)}) - matches " +
                    "StructuralConfluenceStrategyOptions.Validate()'s own TriggerInterval <= SetupInterval <= ContextInterval rule.");
            }

            var instrument = new InstrumentKey(instrumentValue);
            TimeframeTopology topology = StandardTimeframeTopologyFactory.Build(
                executionInterval, warmupDays, setupInterval, contextInterval);
            DateTimeOffset externalHoldoutTo = asOf;
            DateTimeOffset externalHoldoutFrom = externalHoldoutTo.AddDays(-holdoutDays);
            DateTimeOffset learningTo = externalHoldoutFrom.AddDays(-embargoDays);
            DateTimeOffset learningFrom = learningTo.AddDays(-learningDays);

            (string strategyId, string manifestVersion, string baselineHash) = strategyName.ToLowerInvariant() switch
            {
                "indicator-confluence" => ResolveIndicatorConfluenceIdentity(),
                "liquidity-break-retest" => ResolveLiquidityBreakRetestIdentity(),
                "liquidity-sweep-reversal" => ResolveLiquiditySweepReversalIdentity(),
                "supply-demand-pullback" => ResolveSupplyDemandPullbackIdentity(),
                _ => throw new ArgumentException(
                    $"Unknown --strategy '{strategyName}' - expected 'indicator-confluence', " +
                    "'liquidity-break-retest', 'liquidity-sweep-reversal', or 'supply-demand-pullback'.")
            };

            var request = new IndicatorCalibrationRequest
            {
                StrategyId = strategyId,
                ManifestVersion = manifestVersion,
                Instrument = instrument,
                TimeframeTopology = topology,
                Timeline = new CalibrationTimeline
                {
                    LearningFrom = learningFrom,
                    LearningTo = learningTo,
                    ExternalHoldoutFrom = externalHoldoutFrom,
                    ExternalHoldoutTo = externalHoldoutTo,
                    EmbargoDays = embargoDays,
                    WarmupDays = warmupDays
                },
                InternalFoldCount = foldCount,
                RandomSeed = randomSeed,
                Budget = new CalibrationEvaluationBudget
                {
                    WarningEvaluationCount = 200,
                    MaximumEvaluationCount = 5000,
                    MaximumEvaluationsPerFold = 2000,
                    MaximumInteractionCombinationsPerGroup = 25,
                    HardRuntimeLimit = TimeSpan.FromHours(hardRuntimeLimitHours),
                    OverflowPolicy = overflowPolicy
                },
                BaselineConfigurationHash = baselineHash
            };
            request.Validate();

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            File.WriteAllText(outputPath, JsonSerializer.Serialize(request, JsonOptions));
            Console.WriteLine($"Wrote {outputPath}");
            Console.WriteLine($"  Strategy:  {strategyId} / {manifestVersion}");
            Console.WriteLine($"  Instrument: {instrumentValue}");
            Console.WriteLine(
                $"  Timeframe: trigger {BarIntervalParser.Format(executionInterval)} / " +
                $"setup {BarIntervalParser.Format(setupInterval)} / context {BarIntervalParser.Format(contextInterval)}");
            Console.WriteLine($"  Learning:  {learningFrom:yyyy-MM-dd} .. {learningTo:yyyy-MM-dd}");
            Console.WriteLine($"  Holdout:   {externalHoldoutFrom:yyyy-MM-dd} .. {externalHoldoutTo:yyyy-MM-dd}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.WriteLine($"generate-request failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Hashes against <c>new StructuralConfluenceStrategyOptions()</c>'s own sub-object - i.e. the
    /// strategy's declared defaults, not any specific instrument's real configured overrides
    /// (Phase 7's <c>resolveBaselineOptions</c> seam). If the instrument this request targets runs
    /// with real overrides, generate the request against that same effective configuration instead
    /// so <c>BaselineConfigurationHash</c> actually matches what the search will run against.
    /// </summary>
    private static (string StrategyId, string ManifestVersion, string BaselineHash) ResolveIndicatorConfluenceIdentity()
    {
        var manifest = new IndicatorConfluenceCalibrationManifest();
        string hash = IndicatorCalibrationHash.ComputeOfObject(
            IndicatorConfluenceCalibrationStrategyAdapter.DefaultCalibrationBaseline.IndicatorConfluence);
        return (manifest.StrategyId, manifest.ManifestVersion, hash);
    }

    private static (string StrategyId, string ManifestVersion, string BaselineHash) ResolveLiquidityBreakRetestIdentity()
    {
        var manifest = new LiquidityBreakRetestCalibrationManifest();
        string hash = IndicatorCalibrationHash.ComputeOfObject(new StructuralConfluenceStrategyOptions().LiquidityBreakRetest);
        return (manifest.StrategyId, manifest.ManifestVersion, hash);
    }

    private static (string StrategyId, string ManifestVersion, string BaselineHash) ResolveLiquiditySweepReversalIdentity()
    {
        var manifest = new LiquiditySweepReversalCalibrationManifest();
        string hash = IndicatorCalibrationHash.ComputeOfObject(new StructuralConfluenceStrategyOptions().LiquiditySweepReversal);
        return (manifest.StrategyId, manifest.ManifestVersion, hash);
    }

    private static (string StrategyId, string ManifestVersion, string BaselineHash) ResolveSupplyDemandPullbackIdentity()
    {
        var manifest = new SupplyDemandPullbackCalibrationManifest();
        string hash = IndicatorCalibrationHash.ComputeOfObject(new StructuralConfluenceStrategyOptions().SupplyDemandPullback);
        return (manifest.StrategyId, manifest.ManifestVersion, hash);
    }

    private static int ReadIntOption(string[] args, string name, int defaultValue) =>
        ReadOption(args, name) is { } raw ? int.Parse(raw) : defaultValue;

    private static async Task<IndicatorCalibrationRequest> LoadRequestAsync(string? requestPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            throw new ArgumentException("--request <path-to-request.json> is required for this subcommand.");
        await using FileStream stream = File.OpenRead(requestPath);
        IndicatorCalibrationRequest request = await JsonSerializer.DeserializeAsync<IndicatorCalibrationRequest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"'{requestPath}' did not contain a valid IndicatorCalibrationRequest.");
        request.Validate();
        return request;
    }

    private static Task PrintAsync<T>(T value, CancellationToken cancellationToken) =>
        Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));

    private static string? ReadOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    private static string RequireId(string? id = null) =>
        id ?? throw new ArgumentException("--id <calibrationId> is required for this subcommand.");

    private static int PrintUnknown(string subcommand)
    {
        Console.WriteLine($"Unknown indicator-calibration subcommand '{subcommand}'.");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            indicator-calibration <subcommand> [options]

            Subcommands:
              generate-request --strategy <indicator-confluence|liquidity-break-retest|liquidity-sweep-reversal|supply-demand-pullback>
                  --instrument <key> --output <path.json>
                  [--execution-interval 1m|5m|15m|1h|...=1m] (also the strategy's own trigger/decision interval)
                  [--setup-interval 15m|1h|...=15m] [--context-interval 1h|4h|...=1h]
                  (must satisfy execution-interval <= setup-interval <= context-interval)
                  [--learning-days N=60] [--holdout-days N=14]
                  [--embargo-days N=2] [--warmup-days N=10] [--fold-count N=3] [--as-of <date>=today]
                  [--hard-runtime-limit-hours N=8]
                  [--overflow-policy Reject|ReduceRefinement|ReduceStartingPoints|SkipLowerPriorityInteractions=ReduceRefinement]
                  [--random-seed N=1]
              preview  --request <path.json>
              start    --request <path.json> [--accept-budget]
              status   --id <calibrationId>
              list
              pause    --id <calibrationId>
              resume   --id <calibrationId>
              cancel   --id <calibrationId>
              approve  (--id <calibrationId> | --artifact-id <guid>) --approved-by <name> --target-environment <env> [--review-notes <text>] [--replaces-artifact-id <id>]
              reject   (--id <calibrationId> | --artifact-id <guid>) --rejected-by <name> [--reason <text>]
              artifact --id <calibrationId>
              pending
              review   --artifact-id <guid>

            'pending'/'review'/artifact-id-based approve/reject all read directly from the
            artifact repository, so they work for any prior run - including an unattended
            overnight calibration whose process has long since exited.

            Common options:
              --artifacts-directory <path>   (default .cache/calibration-artifacts)
              --ledger-directory <path>      (default .cache/indicator-calibration-ledgers)
            """);
    }
}
