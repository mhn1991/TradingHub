using System.Collections.Concurrent;
using Brokers.Abstractions;
using Brokers.Models;
using DBManager.Abstractions.Config;
using DBManager.Postgres;
using LiveTrading;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Deployment;
using LiveTrading.Runtime;
using Microsoft.EntityFrameworkCore;
using TradingCore.Pipeline;
using TradingPolicies;

namespace LiveTradingHost.Deployment;

public interface ILiveDynamicAgentRuntimeService
{
    Task<long> StartAsync(DeploymentAgentDetail agent, CancellationToken cancellationToken = default);
    Task<long> PauseAsync(Guid deploymentAgentId, CancellationToken cancellationToken = default);
    Task<long> ResumeAsync(Guid deploymentAgentId, CancellationToken cancellationToken = default);
    Task<long> DrainAsync(Guid deploymentAgentId, CancellationToken cancellationToken = default);
    Task<long> StopAsync(Guid deploymentAgentId, CancellationToken cancellationToken = default);
    Task<long> ReplaceAsync(Guid currentDeploymentAgentId, DeploymentAgentDetail replacement,
        CancellationToken cancellationToken = default);
    IReadOnlyList<DynamicAgentRuntimeState> Snapshot { get; }
}

public sealed class LiveDynamicAgentRuntimeService(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    IAgentPackageResolver packages,
    AgentSupervisor supervisor,
    LiveAnalysisProfileRegistry analysisProfiles,
    ILivePolicyRegistry policies,
    IStrategyDecisionPipelineFactory pipelineFactory,
    IBrokerClient broker,
    MarketSubscriptionRegistry marketSubscriptions,
    AnalysisRuntimeRegistry analysisRuntimes,
    AgentRuntimeRegistry runtimes,
    ExecutionOwnershipRegistry ownership,
    ILiveTradingRuntimeCoordinator runtimeCoordinator,
    LiveEngineHostedService engine,
    TimeProvider timeProvider) : ILiveDynamicAgentRuntimeService
{
    private sealed record ActiveRuntime(
        DeploymentAgentDetail Detail,
        AgentInstanceKey Key,
        AnalysisProfileKey Profile,
        BrokerInstrumentRuntimeKey MarketKey,
        AnalysisRuntimeKey AnalysisKey,
        StrategyActivationMode Mode,
        ResolvedAgentPackage Package);

    private readonly ConcurrentDictionary<Guid, ActiveRuntime> _active = new();

    public IReadOnlyList<DynamicAgentRuntimeState> Snapshot => runtimes.Snapshot();

    public async Task<long> StartAsync(DeploymentAgentDetail agent,
        CancellationToken cancellationToken = default)
    {
        if (_active.TryGetValue(agent.DeploymentAgentId, out ActiveRuntime? existing))
            return runtimes.Get(agent.DeploymentAgentId)?.AppliedEpoch ?? 0;

        ActiveRuntime active = await BuildAsync(agent, cancellationToken).ConfigureAwait(false);
        bool ownsExecution = ownership.TryAcquire(active.MarketKey, agent.DeploymentAgentId,
            active.Mode, out Guid? conflictingOwner);
        if (!ownsExecution)
            throw new InvalidOperationException($"Execution ownership is held by {conflictingOwner}.");

        string consumer = agent.DeploymentAgentId.ToString("N");
        try
        {
            marketSubscriptions.Acquire(active.MarketKey, consumer);
            analysisRuntimes.Acquire(active.AnalysisKey, active.Package.RequiredIntervals, consumer);
            analysisRuntimes.MarkReady(active.AnalysisKey, timeProvider.GetUtcNow());
            AgentInstanceState instance = CreateInstance(active);
            LiveTradingPolicyBundle bundle = instance.PolicyBundle;
            policies.Register(active.Key, bundle, active.Mode, active.Package.ManagementCalibration);
            long epoch = await supervisor.StageRegisterAsync(instance, cancellationToken)
                .ConfigureAwait(false);
            runtimes.StageStart(ToState(active, DynamicAgentRuntimeStatus.Staged, 0));
            runtimes.ApplyDecisionEpochBoundary(epoch);
            if (!_active.TryAdd(agent.DeploymentAgentId, active))
                throw new InvalidOperationException($"Deployment agent '{agent.DeploymentAgentId}' raced activation.");
            return epoch;
        }
        catch
        {
            marketSubscriptions.Release(active.MarketKey, consumer);
            analysisRuntimes.Release(active.AnalysisKey, consumer);
            ownership.Release(active.MarketKey, agent.DeploymentAgentId);
            throw;
        }
    }

    public Task<long> PauseAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, supervisor.StagePauseAsync, runtimes.StagePause,
            cancellationToken);

    public Task<long> ResumeAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, supervisor.StageResumeAsync, runtimes.StageResume,
            cancellationToken);

    public Task<long> DrainAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(id, supervisor.StageDrainAsync, runtimes.StageDrain,
            cancellationToken);

    public async Task<long> StopAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ActiveRuntime active = RequireActive(id);
        long epoch = await supervisor.StageStopAsync(active.Key, cancellationToken).ConfigureAwait(false);
        runtimes.StageStop(id);
        runtimes.ApplyDecisionEpochBoundary(epoch);
        _active.TryRemove(id, out _);
        string consumer = id.ToString("N");
        marketSubscriptions.Release(active.MarketKey, consumer);
        analysisRuntimes.Release(active.AnalysisKey, consumer);
        ownership.Release(active.MarketKey, id);
        return epoch;
    }

    public async Task<long> ReplaceAsync(Guid currentId, DeploymentAgentDetail replacement,
        CancellationToken cancellationToken = default)
    {
        ActiveRuntime current = RequireActive(currentId);
        ActiveRuntime next = await BuildAsync(replacement, cancellationToken).ConfigureAwait(false);
        if (next.MarketKey != current.MarketKey || next.Key.Instrument != current.Key.Instrument)
            throw new InvalidOperationException("A hot-swap must retain the broker account and instrument.");
        if (next.Mode != current.Mode)
            throw new InvalidOperationException("A revision hot-swap cannot change execution mode.");
        if (IsExecutable(next.Mode) && !ownership.TryTransfer(next.MarketKey, currentId, replacement.DeploymentAgentId))
            throw new InvalidOperationException("Executable ownership changed before the hot-swap could be applied.");

        string nextConsumer = replacement.DeploymentAgentId.ToString("N");
        marketSubscriptions.Acquire(next.MarketKey, nextConsumer);
        analysisRuntimes.Acquire(next.AnalysisKey, next.Package.RequiredIntervals, nextConsumer);
        analysisRuntimes.MarkReady(next.AnalysisKey, timeProvider.GetUtcNow());
        try
        {
            AgentInstanceState instance = CreateInstance(next);
            var registration = new LivePolicyRegistration
            {
                Key = next.Key,
                Policy = instance.PolicyBundle,
                Mode = next.Mode,
                ManagementCalibration = next.Package.ManagementCalibration
            };
            policies.RegisterHistorical(next.Key, registration);
            long epoch = await supervisor.StageReplaceAsync(current.Key, instance, cancellationToken)
                .ConfigureAwait(false);
            await runtimeCoordinator.ActivatePolicyRevisionAsync(next.Key, registration, cancellationToken)
                .ConfigureAwait(false);
            runtimes.StageStop(currentId);
            runtimes.StageStart(ToState(next, DynamicAgentRuntimeStatus.Staged, 0));
            runtimes.ApplyDecisionEpochBoundary(epoch);
            _active.TryRemove(currentId, out _);
            _active[replacement.DeploymentAgentId] = next;
            string currentConsumer = currentId.ToString("N");
            marketSubscriptions.Release(current.MarketKey, currentConsumer);
            analysisRuntimes.Release(current.AnalysisKey, currentConsumer);
            return epoch;
        }
        catch
        {
            marketSubscriptions.Release(next.MarketKey, nextConsumer);
            analysisRuntimes.Release(next.AnalysisKey, nextConsumer);
            if (IsExecutable(next.Mode))
                ownership.TryTransfer(next.MarketKey, replacement.DeploymentAgentId, currentId);
            throw;
        }
    }

    private async Task<long> ChangeAsync(Guid id,
        Func<AgentInstanceKey, CancellationToken, Task<long>> stageSupervisor, Action<Guid> stageRegistry,
        CancellationToken cancellationToken)
    {
        ActiveRuntime active = RequireActive(id);
        long epoch = await stageSupervisor(active.Key, cancellationToken).ConfigureAwait(false);
        stageRegistry(id);
        runtimes.ApplyDecisionEpochBoundary(epoch);
        return epoch;
    }

    private async Task<ActiveRuntime> BuildAsync(DeploymentAgentDetail agent,
        CancellationToken cancellationToken)
    {
        ResolvedAgentPackage package = await packages.ResolveAsync(agent.PolicyRevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(package.PackageHash, agent.PackageHash))
            throw new InvalidOperationException("Deployment agent package hash no longer matches its exact revision.");
        if (!StringComparer.Ordinal.Equals(package.StrategyId, agent.StrategyId))
            throw new InvalidOperationException("Deployment agent strategy does not match its exact revision.");
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        string canonicalKey = await db.Instruments.AsNoTracking()
            .Where(item => item.InstrumentId == agent.InstrumentId)
            .Select(item => item.CanonicalKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Instrument '{agent.InstrumentId}' was not found.");
        var instrument = new InstrumentKey(canonicalKey);
        AnalysisProfileKey profile = analysisProfiles.GetOrCreateProfile(
            package.FeaturePolicy.AnnotationOptions, package.RequiredIntervals);
        if (!engine.CanServeAnalysis(instrument, profile))
            throw new InvalidOperationException(
                "The live host did not preload this exact analysis profile for the configured instrument.");
        StrategyActivationMode mode = ToActivationMode(agent.AgentMode);
        return new ActiveRuntime(agent,
            new AgentInstanceKey(agent.DeploymentId.ToString("N"), instrument, package.StrategyId,
                package.PolicyId, package.Revision),
            profile,
            new BrokerInstrumentRuntimeKey(agent.BrokerAccountId, agent.InstrumentId),
            new AnalysisRuntimeKey(agent.InstrumentId, profile.ProfileHash),
            mode,
            package);
    }

    private AgentInstanceState CreateInstance(ActiveRuntime active)
    {
        StrategyDecisionRuntime runtime = LiveAgentFactory.Create(active.Package, pipelineFactory);
        LiveTradingPolicyBundle bundle = LiveTradingPolicyBundle.FromResolvedPackage(active.Package);
        var assignment = new LiveStrategyAssignment
        {
            StrategyId = active.Package.StrategyId,
            PolicyBundleId = active.Package.PolicyId.ToString("N"),
            Mode = active.Mode,
            Enabled = true
        };
        return new AgentInstanceState
        {
            Key = active.Key,
            Runtime = runtime,
            PolicyBundle = bundle,
            Assignment = assignment,
            AnalysisProfile = active.Profile,
            IsolatedRuntime = new LiveAgentRuntime(active.Key, active.Profile,
                IsExecutable(active.Mode) ? AgentExecutionMode.Executable : AgentExecutionMode.Shadow,
                runtime.Pipeline, broker)
        };
    }

    private DynamicAgentRuntimeState ToState(ActiveRuntime active, DynamicAgentRuntimeStatus status, long epoch) =>
        new()
        {
            DeploymentAgentId = active.Detail.DeploymentAgentId,
            BrokerAccountId = active.Detail.BrokerAccountId,
            InstrumentId = active.Detail.InstrumentId,
            Mode = active.Mode,
            Package = active.Package,
            Status = status,
            AppliedEpoch = epoch
        };

    private ActiveRuntime RequireActive(Guid id) => _active.TryGetValue(id, out ActiveRuntime? active)
        ? active
        : throw new KeyNotFoundException($"Deployment agent '{id}' is not active in this host.");

    private static bool IsExecutable(StrategyActivationMode mode) =>
        mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic;

    private static StrategyActivationMode ToActivationMode(AgentMode mode) => mode switch
    {
        AgentMode.RecordOnly => StrategyActivationMode.ObserveOnly,
        AgentMode.Shadow => StrategyActivationMode.Shadow,
        AgentMode.ManualApproval => StrategyActivationMode.ManualApproval,
        AgentMode.Automatic => StrategyActivationMode.Automatic,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
