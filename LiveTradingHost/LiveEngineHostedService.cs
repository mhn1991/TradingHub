using System.Threading.Channels;
using Agent.Factories;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using LiveTrading.Account;
using LiveTrading.AccountLease;
using LiveTrading.Actors;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Feed;
using LiveTrading.Management;
using LiveTrading.MarketData;
using LiveTrading.Persistence;
using LiveTrading.Reconciliation;
using LiveTrading.Runtime;
using LiveTrading.Shadow.Outcomes;
using LiveTradingHost.Api;
using LiveTradingHost.Configuration;
using LiveTradingHost.Deployment;
using LiveTradingHost.Observability;
using LiveTradingHost.Status;
using Microsoft.Extensions.Options;
using QuantResearch.Training.Pipeline;
using RiskManager.Calibration;
using RiskManager.Safety;
using TradingCore.MarketData;
using TradingCore.Pipeline;
using TradingObservability.Abstractions;
using TradingPolicies;

namespace LiveTradingHost;

/// <summary>
/// Owns the complete Practice-account live lifecycle: account lease, durable recovery,
/// transaction stream, periodic REST reconciliation, completed-candle market actors, Agent
/// evaluation, serialized portfolio admission/manual or autonomous demo execution, and live
/// position management. The Agent itself still produces intent only.
/// </summary>
public sealed class LiveEngineHostedService(
    ITradingAccountLease lease,
    IOptions<LiveOandaOptions> oandaOptions,
    IOptions<LiveMarketUniverseOptions> markets,
    IOptions<AccountLeaseOptions> leaseOptions,
    LiveHostRuntimeOptions hostOptions,
    IServiceProvider services,
    LiveEngineState state,
    LiveStatusRealtimePublisher publisher,
    LiveRuntimeObservability observability,
    LiveAnalysisProfileRegistry analysisProfiles,
    HostInstanceIdentity hostIdentity,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private const int WarmupCandles = 250;

    private readonly string _instanceId = hostIdentity.Value;
    private readonly ILogger<LiveEngineHostedService> _logger = loggerFactory.CreateLogger<LiveEngineHostedService>();
    private IBrokerClient _broker = null!;
    private ILiveQuoteStream _quoteStream = null!;
    private ICompletedCandleProvider _candleProvider = null!;
    private AgentSupervisor _agentSupervisor = null!;
    private LiveDecisionEpochCoordinator _epochCoordinator = null!;
    private ILiveTradingRuntimeCoordinator _runtime = null!;
    private ILiveTradingPersistence _persistence = null!;
    private ILiveBrokerEventProcessor _brokerEvents = null!;
    private ILiveAccountStateService _account = null!;
    private ILivePositionManagementService _positionManagement = null!;
    private ILivePolicyRegistry _policies = null!;
    private ITradingSafetyController _safety = null!;
    private ILiveCalibrationTrainingScheduler _calibrationScheduler = null!;
    private ILiveShadowOutcomeService _shadowOutcomes = null!;
    private readonly LiveAnalysisProfileRegistry _analysisProfiles = analysisProfiles;
    private readonly Dictionary<InstrumentKey, List<AnalysisProfileKey>> _profilesByMarket = new();
    private volatile bool _ready;

    public bool IsReady => _ready;

    public bool CanServeAnalysis(InstrumentKey instrument, AnalysisProfileKey profile) =>
        _ready && _profilesByMarket.TryGetValue(instrument, out List<AnalysisProfileKey>? profiles)
            && profiles.Contains(profile);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!oandaOptions.Value.IsConfigured)
        {
            const string message = "OANDA is not configured (Oanda:Enabled/AccountId/AccessToken). " +
                "Import an enabled OANDA credential into the broker credential database.";
            _logger.LogCritical("{Message}", message);
            state.SetFaulted(message);
            await PublishAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        try
        {
            ResolveServices();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The live trading dependency graph could not be constructed.");
            state.SetFaulted(ex.Message);
            await PublishAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        AccountLeaseResult acquire = await lease.TryAcquireAsync(
            "OANDA",
            oandaOptions.Value.AccountId,
            _instanceId,
            stoppingToken).ConfigureAwait(false);
        if (acquire.Outcome != AccountLeaseOutcome.Acquired)
        {
            string message = $"Account lease not acquired: {acquire.Outcome}. " +
                $"Held by instance {acquire.CurrentHolder?.InstanceId ?? "unknown"}.";
            _logger.LogCritical("{Message}", message);
            state.SetLeaseState(acquire.Outcome.ToString());
            state.SetFaulted(message);
            await PublishAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        state.SetLeaseState("Acquired");
        using var persistenceCts = new CancellationTokenSource();
        Task persistenceTask = _persistence.RunAsync(persistenceCts.Token);
        using CancellationTokenSource renewalCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task renewalLoop = RunLeaseRenewalLoopAsync(renewalCts.Token);
        RuntimeSessionStatus terminalStatus = RuntimeSessionStatus.Completed;
        string? terminalReason = null;

        try
        {
            if (!await RegisterAgentsAsync(stoppingToken).ConfigureAwait(false))
            {
                await PublishAsync(stoppingToken).ConfigureAwait(false);
                return;
            }

            await observability.StartAsync(
                _agentSupervisor.Instances.ToArray(), _instanceId, stoppingToken).ConfigureAwait(false);

            await _runtime.RecoverAsync(stoppingToken).ConfigureAwait(false);
            state.SetRestReachable(true);
            RefreshRuntimeStatus();

            // Detached, not part of workerTasks: that list's Task.WhenAny below treats ANY
            // completion (including success) as host-fatal, but a calibration training run is
            // finite by design and must not be able to bring the host down either by failing or
            // by simply finishing. It is an isolated QuantResearch job, not part of the live
            // decision loop - it only ever produces a PendingReview candidate for a human to
            // review later; it never touches ObserveOnly/Shadow/ManualApproval/Automatic behavior.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _calibrationScheduler.RunDueRetrainingAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Automatic calibration retraining scheduler failed unexpectedly.");
                }
            }, stoppingToken);

            using var engineCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            CancellationToken engineToken = engineCts.Token;
            var quoteManagementChannel = Channel.CreateBounded<LiveQuoteSnapshot>(
                new BoundedChannelOptions(hostOptions.ManagementQuoteQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });

            var workerTasks = new List<Task>
            {
                ProcessClosedEpochsAsync(engineToken),
                _brokerEvents.RunAsync(engineToken),
                RunPeriodicReconciliationAsync(engineToken),
                RunPeriodicCheckpointAsync(engineToken),
                PumpManagementQuotesAsync(quoteManagementChannel.Reader, engineToken)
            };
            var perMarketInputs = new Dictionary<InstrumentKey, Channel<LiveMarketEvent>>();

            foreach (LiveMarketDefinition market in markets.Value.Markets.Where(market => market.Enabled))
            {
                var eventChannel = Channel.CreateBounded<LiveMarketEvent>(new BoundedChannelOptions(1_000)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                perMarketInputs[market.Instrument] = eventChannel;

                var outputChannel = Channel.CreateBounded<MarketAnalysisUpdate>(new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
                MarketAnalysisActor actor = BuildActor(market);
                workerTasks.Add(actor.RunAsync(eventChannel.Reader, outputChannel.Writer, engineToken));
                workerTasks.Add(PumpAnalysisAsync(market, actor, outputChannel.Reader, engineToken));

                var candleChannel = Channel.CreateBounded<Candle>(new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
                DateTimeOffset coordinatorCursor = timeProvider.GetUtcNow() - TimeSpan.FromSeconds(
                    BarIntervalParser.ApproximateSeconds(market.AnalysisBaseInterval) * WarmupCandles);
                var coordinator = new CompletedCandleCoordinator(
                    market.Instrument,
                    market.AnalysisBaseInterval,
                    _candleProvider,
                    timeProvider,
                    new CompletedCandleCoordinatorOptions(),
                    loggerFactory.CreateLogger<CompletedCandleCoordinator>());
                workerTasks.Add(coordinator.RunAsync(candleChannel.Writer, coordinatorCursor, engineToken));
                workerTasks.Add(ForwardCandlesAsync(
                    market.Instrument,
                    market.AnalysisBaseInterval,
                    candleChannel.Reader,
                    eventChannel.Writer,
                    engineToken));
            }

            var reconnectingFeed = new ReconnectingQuoteFeed(
                _quoteStream,
                timeProvider,
                new ReconnectingQuoteFeedOptions(),
                loggerFactory.CreateLogger<ReconnectingQuoteFeed>());
            IReadOnlyList<InstrumentKey> instruments = markets.Value.Markets
                .Where(market => market.Enabled)
                .Select(market => market.Instrument)
                .ToList();
            IReadOnlyDictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>> perMarketWriters =
                perMarketInputs.ToDictionary(
                    item => item.Key,
                    item => (ChannelWriter<LiveMarketEvent>)item.Value.Writer);
            workerTasks.Add(MonitorQuoteFeedAsync(
                reconnectingFeed,
                instruments,
                perMarketWriters,
                quoteManagementChannel.Writer,
                engineToken));

            state.SetRunning();
            _ready = true;
            await PublishAsync(stoppingToken).ConfigureAwait(false);

            Task first = await Task.WhenAny(workerTasks.Append(persistenceTask)).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
            {
                // Every worker is designed to run for the engine lifetime. A normal completion is
                // therefore also a fault because it silently removes a safety or data boundary.
                engineCts.Cancel();
                Exception? workerFailure = null;
                try
                {
                    await first.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    workerFailure = ex;
                }
                await SafeAwaitAsync(Task.WhenAll(workerTasks)).ConfigureAwait(false);
                throw new IOException(
                    first == persistenceTask
                        ? "The live persistence writer stopped unexpectedly."
                        : "A live engine worker stopped unexpectedly.",
                    workerFailure);
            }
            engineCts.Cancel();
            await SafeAwaitAsync(Task.WhenAll(workerTasks)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            terminalStatus = RuntimeSessionStatus.Cancelled;
            terminalReason = "Host shutdown requested.";
        }
        catch (Exception ex)
        {
            terminalStatus = RuntimeSessionStatus.Failed;
            terminalReason = ex.Message;
            _logger.LogCritical(ex, "The live engine faulted. Existing broker-side stops are left intact.");
            _safety.Trip(
                SafetyTripReason.External,
                $"Live engine faulted: {ex.Message}",
                timeProvider.GetUtcNow());
            state.SetFaulted(ex.Message);
            await PublishIgnoringCancellationAsync().ConfigureAwait(false);
        }
        finally
        {
            _ready = false;
            renewalCts.Cancel();
            await SafeAwaitAsync(renewalLoop).ConfigureAwait(false);
            try
            {
                await _runtime.SaveCheckpointAsync(cleanShutdown: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "The clean-shutdown checkpoint could not be written.");
            }
            if (observability.Started)
            {
                try
                {
                    await observability.CompleteAsync(terminalStatus, terminalReason, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "The live runtime observability session could not be finalized.");
                }
            }
            persistenceCts.Cancel();
            await SafeAwaitAsync(persistenceTask).ConfigureAwait(false);
        }
    }

    private void ResolveServices()
    {
        _broker = services.GetRequiredService<IBrokerClient>();
        _quoteStream = services.GetRequiredService<ILiveQuoteStream>();
        _candleProvider = services.GetRequiredService<ICompletedCandleProvider>();
        _agentSupervisor = services.GetRequiredService<AgentSupervisor>();
        _epochCoordinator = services.GetRequiredService<LiveDecisionEpochCoordinator>();
        _runtime = services.GetRequiredService<ILiveTradingRuntimeCoordinator>();
        _persistence = services.GetRequiredService<ILiveTradingPersistence>();
        _brokerEvents = services.GetRequiredService<ILiveBrokerEventProcessor>();
        _account = services.GetRequiredService<ILiveAccountStateService>();
        _positionManagement = services.GetRequiredService<ILivePositionManagementService>();
        _policies = services.GetRequiredService<ILivePolicyRegistry>();
        _safety = services.GetRequiredService<ITradingSafetyController>();
        _calibrationScheduler = services.GetRequiredService<ILiveCalibrationTrainingScheduler>();
        _shadowOutcomes = services.GetRequiredService<ILiveShadowOutcomeService>();
    }

    private async Task MonitorQuoteFeedAsync(
        ReconnectingQuoteFeed feed,
        IReadOnlyList<InstrumentKey> instruments,
        IReadOnlyDictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>> perMarketWriters,
        ChannelWriter<LiveQuoteSnapshot> managementQuotes,
        CancellationToken cancellationToken)
    {
        await feed.RunAsync(
            instruments,
            perMarketWriters,
            cancellationToken,
            (quoteEvent, _) =>
            {
                _account.ApplyQuote(quoteEvent.Quote);
                managementQuotes.TryWrite(quoteEvent.Quote);
                return ValueTask.CompletedTask;
            },
            async (connected, reason, token) =>
            {
                state.SetQuoteStreamConnected(connected);
                if (!connected)
                    await _runtime.PauseAsync(reason ?? "Quote stream unavailable.", token).ConfigureAwait(false);
                RefreshRuntimeStatus();
                await PublishAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task PumpManagementQuotesAsync(
        ChannelReader<LiveQuoteSnapshot> quotes,
        CancellationToken cancellationToken)
    {
        await foreach (LiveQuoteSnapshot quote in quotes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _shadowOutcomes.OnQuoteAsync(quote, cancellationToken).ConfigureAwait(false);
            await _positionManagement.OnQuoteAsync(quote, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessClosedEpochsAsync(CancellationToken cancellationToken)
    {
        await foreach (LiveDecisionEpochBatch batch in _epochCoordinator.ClosedEpochs
                           .ReadAllAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Decision epoch {Epoch} closed with {CandidateCount} candidate(s) and {UnavailableCount} unavailable market(s).",
                batch.Epoch,
                batch.OrderedCandidates.Count,
                batch.UnavailableInstruments.Count);
            await _runtime.ProcessEpochAsync(batch, _policies.Resolve, _policies.ResolveMode, cancellationToken).ConfigureAwait(false);
            state.UpdateDecisionEpoch(new DecisionEpochStatusDto
            {
                Epoch = batch.Epoch,
                ExpectedAgents = batch.ExpectedAgents.Count,
                CompletedAgents = batch.CompletedAgents.Count,
                FailedAgents = batch.FailedAgents.Count,
                TimedOutAgents = batch.TimedOutAgents.Count,
                MissingAgents = batch.MissingAgents.Count,
                CandidateCount = batch.OrderedCandidates.Count,
                DurationMilliseconds = batch.Duration.TotalMilliseconds
            });
            RefreshRuntimeStatus();
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ForwardCandlesAsync(
        InstrumentKey instrument,
        BarInterval interval,
        ChannelReader<Candle> input,
        ChannelWriter<LiveMarketEvent> output,
        CancellationToken cancellationToken)
    {
        await foreach (Candle candle in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await _shadowOutcomes.OnCompletedCandleAsync(candle, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(
                new CandleClosedMarketEvent
                {
                    Instrument = instrument,
                    ReceivedAt = candle.CloseTime ?? interval.AddTo(candle.OpenTime),
                    Kind = LiveMarketEventKind.CandleClosed,
                    Candle = candle,
                    Interval = interval
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpAnalysisAsync(
        LiveMarketDefinition market,
        MarketAnalysisActor actor,
        ChannelReader<MarketAnalysisUpdate> updates,
        CancellationToken cancellationToken)
    {
        state.UpdateMarket(new MarketStatusDto
        {
            Instrument = market.Instrument.Value,
            Ready = false,
            State = actor.State.ToString()
        });

        await foreach (MarketAnalysisUpdate update in updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            state.UpdateMarket(new MarketStatusDto
            {
                Instrument = market.Instrument.Value,
                Bid = actor.LatestQuote?.Bid,
                Ask = actor.LatestQuote?.Ask,
                Spread = actor.LatestQuote?.Spread,
                LastM1CloseAt = update.Health.LastM1CloseAt,
                Ready = actor.Readiness?.Ready ?? false,
                Regime = update.Analysis.TryGet(market.AnalysisBaseInterval, out var snapshot)
                    ? snapshot.MarketRegime.Regime.ToString()
                    : null,
                State = actor.State.ToString()
            });

            await _positionManagement.OnAnalysisAsync(update, actor.LatestQuote, cancellationToken)
                .ConfigureAwait(false);
            await _shadowOutcomes.OnAnalysisAsync(update, actor.LatestQuote, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<AgentEvaluationAudit> evaluationAudits = await _agentSupervisor
                .ApplyAsync(update, actor.LatestQuote?.Spread, cancellationToken)
                .ConfigureAwait(false);
            await observability.ObserveAsync(evaluationAudits, cancellationToken).ConfigureAwait(false);
            foreach (AnalysisProfileActorStatus profile in actor.ProfileStatuses)
            {
                state.UpdateAnalysisProfile(new AnalysisProfileStatusDto
                {
                    ProfileHash = profile.Profile.ProfileHash,
                    Instrument = profile.Instrument.Value,
                    RequiredIntervals = profile.Profile.RequiredIntervals
                        .OrderBy(interval => interval)
                        .Select(interval => interval.ToString())
                        .ToArray(),
                    LastSnapshotVersion = profile.LastSnapshotVersion,
                    LastAvailableAt = profile.LastAvailableAt,
                    LastBuildDurationMilliseconds = profile.LastBuildDuration.TotalMilliseconds,
                    CacheHits = profile.CacheHits,
                    CacheMisses = profile.CacheMisses,
                    FailureCount = profile.FailureCount,
                    DependentAgentCount = profile.DependentAgentCount
                });
            }
            foreach (AgentInstanceState instance in _agentSupervisor.Instances.Where(instance =>
                         instance.Key.Instrument == market.Instrument))
            {
                (double averageDuration, double p95Duration) = instance.EvaluationDurationSummary();
                state.UpdateAgent(new AgentStatusDto
                {
                    AgentInstanceId = observability.AgentInstanceId(instance.Key),
                    DeploymentId = instance.Key.DeploymentId,
                    StrategyId = instance.Key.StrategyId,
                    Instrument = instance.Key.Instrument.Value,
                    PolicyBundleId = instance.Key.PolicyBundleId,
                    PolicyRevision = instance.Key.Revision,
                    AnalysisProfileHash = instance.AnalysisProfile?.ProfileHash ?? string.Empty,
                    Mode = instance.Assignment.Mode.ToString(),
                    LastStatus = instance.LastStatus,
                    Health = instance.LastEvaluationError is not null || instance.LastStatus is "Failed" or "TimedOut"
                        ? "Unhealthy"
                        : instance.LastEvaluatedAt is null ? "Starting" : "Healthy",
                    LastEvaluatedEpoch = instance.LastEvaluatedEpoch,
                    LastSnapshotVersion = instance.LastSnapshotVersion,
                    LastEvaluatedAt = instance.LastEvaluatedAt,
                    Evaluations = instance.Evaluations,
                    Timeouts = instance.TimeoutCount,
                    AverageEvaluationDurationMilliseconds = averageDuration,
                    P95EvaluationDurationMilliseconds = p95Duration,
                    MailboxDepth = Volatile.Read(ref instance.MailboxDepth),
                    CandidatesObserved = instance.CandidatesObserved,
                    CandidatesBuy = instance.CandidatesBuy,
                    CandidatesSell = instance.CandidatesSell,
                    RejectedBySetupCalibration = instance.CandidatesRejectedBySetupCalibration,
                    RejectedByMetaLabel = instance.CandidatesRejectedByMetaLabel,
                    RejectedByTradingCondition = instance.CandidatesRejectedByTradingCondition,
                    RejectedByDataQuality = instance.CandidatesRejectedByDataQuality,
                    LastDecisionId = instance.LastDecisionId,
                    LastCandidateId = instance.LastCandidate?.CandidateId,
                    LastAction = instance.LastCandidate?.Action.ToString(),
                    LastReferencePrice = instance.LastCandidate?.ReferencePrice,
                    LastStopLossPrice = instance.LastCandidate?.StopLossPrice,
                    LastTakeProfitPrice = instance.LastCandidate?.TakeProfitPrice,
                    LastMetaLabelProbability = instance.LastCandidate?.MetaLabel.Probability,
                    LastMetaLabelRiskMultiplier = instance.LastCandidate?.MetaLabel.RiskMultiplier,
                    LastError = instance.LastEvaluationError?.Message,
                    LastRejection = instance.LastRejection
                });
            }

            RefreshRuntimeStatus();
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RegisterAgentsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, LivePolicyBundleOptions> policyBundles =
            services.GetRequiredService<IOptions<Dictionary<string, LivePolicyBundleOptions>>>().Value;
        LivePolicyBundleFactory bundleFactory = services.GetRequiredService<LivePolicyBundleFactory>();
        IStrategyDecisionPipelineFactory pipelineFactory = services.GetRequiredService<IStrategyDecisionPipelineFactory>();
        LiveExecutionRuntimeOptions executionOptions = services.GetRequiredService<LiveExecutionRuntimeOptions>();
        // Reject duplicate assignment rows before either exact-key registry performs its own
        // idempotency/conflict validation, so configuration errors fail startup with context.
        var registeredKeys = new HashSet<AgentInstanceKey>();

        foreach (LiveMarketDefinition market in markets.Value.Markets.Where(market => market.Enabled))
        {
            LiveStrategyAssignment[] executable = market.Strategies.Where(assignment => assignment.Enabled &&
                assignment.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic).ToArray();
            if (executable.Length > 1)
                return FaultConfiguration($"{market.Instrument} has more than one executable strategy. The first release allows one owner per instrument.");

            foreach (LiveStrategyAssignment assignment in market.Strategies.Where(assignment => assignment.Enabled))
            {
                if ((assignment.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic) &&
                    !executionOptions.BrokerWritesEnabled)
                {
                    return FaultConfiguration(
                        $"Agent {assignment.StrategyId}/{market.Instrument} is {assignment.Mode}, but broker writes are disabled.");
                }
                if (assignment.Mode == StrategyActivationMode.Automatic && !executionOptions.AutomaticExecutionEnabled)
                    return FaultConfiguration("Automatic assignment requires LiveExecution:AutomaticExecutionEnabled=true.");
                if (assignment.Mode == StrategyActivationMode.Automatic &&
                    assignment.StrategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase))
                {
                    return FaultConfiguration("Legacy Agents remain shadow/manual-only until separately validated.");
                }
                if (!policyBundles.TryGetValue(assignment.PolicyBundleId, out LivePolicyBundleOptions? bundleOptions))
                    return FaultConfiguration($"Policy bundle '{assignment.PolicyBundleId}' was not found.");
                TradingPolicies.TradingPolicyProfile portableProfile = bundleOptions.Profile;
                portableProfile.Validate();
                if (!string.Equals(portableProfile.StrategyId, assignment.StrategyId, StringComparison.Ordinal))
                {
                    return FaultConfiguration(
                        $"Promoted policy strategy '{portableProfile.StrategyId}' does not match assignment '{assignment.StrategyId}'.");
                }
                if (assignment.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic &&
                    portableProfile.Status != TradingPolicies.TradingPolicyProfileStatus.ApprovedForDemo)
                {
                    return FaultConfiguration(
                        $"Policy profile {portableProfile.ProfileId} is {portableProfile.Status}; executable demo strategies require ApprovedForDemo.");
                }

                ResolvedPolicyBundle resolved = await bundleFactory
                    .BuildAsync(assignment.PolicyBundleId, bundleOptions, cancellationToken)
                    .ConfigureAwait(false);

                // Phase 5: SetupCalibrationArtifact.Validate already supports checking against a
                // required feature-schema hash - this was simply never wired into registration,
                // so a calibration artifact trained under a stale feature schema silently ran
                // live instead of failing fast.
                if (resolved.SetupCalibration is not null)
                {
                    try
                    {
                        resolved.SetupCalibration.Validate(MetaLabelFeatureFactory.SchemaVersion);
                    }
                    catch (ArgumentException)
                    {
                        return FaultConfiguration(
                            $"Agent {assignment.StrategyId}/{market.Instrument}: setup calibration artifact " +
                            $"{resolved.SetupCalibration.CalibrationId} was trained under feature schema " +
                            $"{resolved.SetupCalibration.FeatureSchemaHash}, which does not match the runtime " +
                            $"feature-vector schema {MetaLabelFeatureFactory.SchemaVersion}.");
                    }
                }

                IReadOnlySet<BarInterval> requiredIntervals = TradingAgentFactory
                    .Create(portableProfile.EffectiveAgentDefinition()).RequiredIntervals.ToHashSet();
                if (!requiredIntervals.IsSubsetOf(market.AnalysisIntervals))
                {
                    return FaultConfiguration(
                        $"Agent {assignment.StrategyId}/{market.Instrument} requires intervals " +
                        $"{string.Join(", ", requiredIntervals.Order())} outside the configured market universe.");
                }
                AnalysisProfileKey profile = _analysisProfiles.GetOrCreateProfile(
                    resolved.Bundle.FeaturePolicy.AnnotationOptions, requiredIntervals);
                if (!_profilesByMarket.TryGetValue(market.Instrument, out List<AnalysisProfileKey>? marketProfiles))
                    _profilesByMarket[market.Instrument] = marketProfiles = [];
                if (!marketProfiles.Contains(profile))
                    marketProfiles.Add(profile);

                StrategyDecisionRuntime runtime = LiveAgentFactory.Create(
                    assignment.StrategyId,
                    portableProfile.EffectiveAgentDefinition(),
                    resolved.Bundle,
                    resolved.SetupCalibration,
                    resolved.MetaModel,
                    pipelineFactory);
                var key = new AgentInstanceKey(
                    AgentInstanceKey.DefaultDeploymentId,
                    market.Instrument,
                    assignment.StrategyId,
                    resolved.Bundle.PolicyBundleId,
                    resolved.Bundle.Revision);
                if (!registeredKeys.Add(key))
                {
                    return FaultConfiguration(
                        $"Agent {assignment.StrategyId}/{market.Instrument} (policy bundle {resolved.Bundle.PolicyBundleId} " +
                        $"revision {resolved.Bundle.Revision}) is registered more than once. Remove the duplicate assignment.");
                }
                _policies.Register(
                    key,
                    resolved.Bundle,
                    assignment.Mode,
                    resolved.ManagementCalibration,
                    resolved.ManagementCalibrationOptions);
                _agentSupervisor.Register(new AgentInstanceState
                {
                    Key = key,
                    Runtime = runtime,
                    PolicyBundle = resolved.Bundle,
                    Assignment = assignment,
                    AnalysisProfile = profile,
                    IsolatedRuntime = new LiveAgentRuntime(
                        key,
                        profile,
                        assignment.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic
                            ? AgentExecutionMode.Executable
                            : AgentExecutionMode.Shadow,
                        runtime.Pipeline,
                        _broker)
                });
            }
        }
        await PreloadDeployableProfilesAsync(cancellationToken).ConfigureAwait(false);
        AgentInstanceState[] instances = _agentSupervisor.Instances
            .OrderBy(instance => instance.Key.DeploymentId, StringComparer.Ordinal)
            .ThenBy(instance => instance.Key.Instrument.Value, StringComparer.Ordinal)
            .ThenBy(instance => instance.Key.StrategyId, StringComparer.Ordinal)
            .ThenBy(instance => instance.Key.PolicyBundleId)
            .ThenBy(instance => instance.Key.Revision)
            .ToArray();
        if (instances.Length > 0)
        {
            await _persistence.AppendAsync(
                "agent-assignments",
                instances.Select(instance => new
                {
                    Agent = instance.Key,
                    AnalysisProfileHash = instance.AnalysisProfile?.ProfileHash ?? string.Empty,
                    ActivationMode = instance.Assignment.Mode,
                    Registered = true
                }).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async Task PreloadDeployableProfilesAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        DBManager.Abstractions.Config.IAgentLifecycleStore lifecycle =
            scope.ServiceProvider.GetRequiredService<DBManager.Abstractions.Config.IAgentLifecycleStore>();
        IAgentPackageResolver packageResolver =
            scope.ServiceProvider.GetRequiredService<IAgentPackageResolver>();
        DBManager.Abstractions.Config.Page<DBManager.Abstractions.Config.PolicyRevisionSummary> page =
            await lifecycle.ListPolicyRevisionsAsync(null, 0, 1_000, cancellationToken).ConfigureAwait(false);
        foreach (DBManager.Abstractions.Config.PolicyRevisionSummary revision in page.Items.Where(item =>
                     item.Status is not (DBManager.Abstractions.Config.PolicyRevisionStatus.Research
                         or DBManager.Abstractions.Config.PolicyRevisionStatus.Retired
                         or DBManager.Abstractions.Config.PolicyRevisionStatus.Suspended)))
        {
            ResolvedAgentPackage package;
            try
            {
                package = await packageResolver.ResolveAsync(revision.PolicyRevisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                _logger.LogWarning(ex, "Skipping unresolved deployable policy revision {PolicyRevisionId}.",
                    revision.PolicyRevisionId);
                continue;
            }

            foreach (LiveMarketDefinition market in markets.Value.Markets.Where(item => item.Enabled
                         && package.RequiredIntervals.IsSubsetOf(item.AnalysisIntervals)))
            {
                AnalysisProfileKey profile = _analysisProfiles.GetOrCreateProfile(
                    package.FeaturePolicy.AnnotationOptions, package.RequiredIntervals);
                if (!_profilesByMarket.TryGetValue(market.Instrument, out List<AnalysisProfileKey>? marketProfiles))
                    _profilesByMarket[market.Instrument] = marketProfiles = [];
                if (!marketProfiles.Contains(profile))
                    marketProfiles.Add(profile);
            }
        }
    }

    private bool FaultConfiguration(string message)
    {
        _logger.LogCritical("{Message}", message);
        state.SetFaulted(message);
        return false;
    }

    private MarketAnalysisActor BuildActor(LiveMarketDefinition market)
    {
        var aggregator = new MultiTimeframeAggregator(
            market.Instrument,
            market.AnalysisIntervals,
            market.CandleCapacity);
        IReadOnlyList<AnalysisProfileKey> profiles =
            _profilesByMarket.TryGetValue(market.Instrument, out List<AnalysisProfileKey>? configured) && configured.Count > 0
                ? configured.OrderBy(item => item.ProfileHash, StringComparer.Ordinal).ToArray()
                : [_analysisProfiles.GetOrCreateProfile(null, market.AnalysisIntervals)];
        var annotators = profiles.ToDictionary(
            profile => profile,
            profile => (IChartAnnotator)_analysisProfiles.EngineFor(market.Instrument, profile));
        var qualityGates = profiles.ToDictionary(
            profile => profile,
            _ => (IMarketDataQualityGate)new MarketDataQualityGate());
        IReadOnlyDictionary<AnalysisProfileKey, int> dependentAgentCounts = _agentSupervisor.Instances
            .Where(instance => instance.Key.Instrument == market.Instrument && instance.AnalysisProfile is not null)
            .GroupBy(instance => instance.AnalysisProfile!)
            .ToDictionary(group => group.Key, group => group.Count());
        return new MarketAnalysisActor(
            market,
            aggregator,
            annotators[profiles[0]],
            new MarketDataQualityGate(),
            _candleProvider,
            timeProvider,
            loggerFactory.CreateLogger<MarketAnalysisActor>(),
            WarmupCandles,
            annotators,
            qualityGates,
            dependentAgentCounts);
    }

    private async Task RunPeriodicReconciliationAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(hostOptions.ReconciliationInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                BrokerReconciliationReport report = await _runtime
                    .ReconcileAsync(ReconciliationTrigger.Periodic, cancellationToken)
                    .ConfigureAwait(false);
                state.SetRestReachable(true);
                await observability.RecordReconciliationAsync(report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.SetRestReachable(false);
                await _runtime.PauseAsync($"Periodic reconciliation failed: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogError(ex, "Periodic broker reconciliation failed.");
                await observability.RecordIncidentAsync(
                    "PeriodicReconciliationFailed", "Periodic broker reconciliation failed.", ex, cancellationToken)
                    .ConfigureAwait(false);
            }
            RefreshRuntimeStatus();
            await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunPeriodicCheckpointAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(hostOptions.CheckpointInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await _runtime.SaveCheckpointAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunLeaseRenewalLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(leaseOptions.Value.RenewInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await lease.RenewAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogCritical(ex, "Account lease renewal failed.");
                    state.SetLeaseState("RenewalFailed");
                    if (_runtime is not null)
                        await _runtime.PauseAsync("Account ownership lease renewal failed.", cancellationToken)
                            .ConfigureAwait(false);
                    await PublishAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected.
        }
    }

    private void RefreshRuntimeStatus() => state.SetRuntime(_runtime.Snapshot);

    private Task PublishAsync(CancellationToken cancellationToken) =>
        publisher.PublishAsync(state.Snapshot(), cancellationToken);

    private async Task PublishIgnoringCancellationAsync()
    {
        try
        {
            await publisher.PublishAsync(state.Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fault status could not be published.");
        }
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        catch
        {
            // The primary worker path already reports the failure.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        state.SetStopped();
        await lease.ReleaseAsync(cancellationToken).ConfigureAwait(false);
    }
}
