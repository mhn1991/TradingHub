using System.Security.Cryptography;
using System.Text;
using Brokers.Models;
using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using DBManager.Postgres;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Deployment;
using LiveTrading.Oanda;
using LiveTrading.Runtime;
using LiveTradingHost.Configuration;
using LiveTradingHost.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingCore.Pipeline;
using TradingPolicies;

namespace LiveTradingHost.Deployment;

public sealed record DeploymentPreflightRequest
{
    public required Guid BrokerAccountId { get; init; }
    public required string BrokerEnvironment { get; init; }
    public required long InstrumentId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required AgentMode Mode { get; init; }
    public string? ExpectedConfigurationHash { get; init; }
    public string? ExpectedPackageHash { get; init; }
    public bool RequireParityCertification { get; init; } = true;
    public Guid? DeploymentAgentId { get; init; }
}

public sealed record DeploymentPreflightGate(string Code, bool Passed, string Message);

public sealed record DeploymentPreflightResult
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<DeploymentPreflightGate> Gates { get; init; }
    public Guid? PolicyId { get; init; }
    public string? StrategyId { get; init; }
    public string? ConfigurationHash { get; init; }
    public string? PackageHash { get; init; }
    public IReadOnlyList<string> RequiredIntervals { get; init; } = [];
}

public sealed record StartDeploymentRequest
{
    public required DeploymentPreflightRequest Preflight { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
}

public sealed record StartDeploymentResult(
    Guid DeploymentId, Guid DeploymentAgentId, Guid CommandId, long Version,
    DeploymentPreflightResult Preflight);

public sealed record AddDeploymentAgentLifecycleRequest
{
    public required DeploymentPreflightRequest Preflight { get; init; }
    public required string IdempotencyKey { get; init; }
    public required long ExpectedVersion { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
}

public sealed record AddDeploymentAgentResult(
    Guid DeploymentId, Guid DeploymentAgentId, Guid CommandId, long Version,
    DeploymentPreflightResult Preflight);

public sealed record DeploymentLifecycleRequest
{
    public required string IdempotencyKey { get; init; }
    public required long ExpectedVersion { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
    public Guid? ReplacementPolicyRevisionId { get; init; }
    public string? ExpectedPackageHash { get; init; }
}

public interface ILiveDeploymentPreflightService
{
    Task<DeploymentPreflightResult> ValidateAsync(DeploymentPreflightRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILiveDeploymentOrchestrator
{
    Task<DeploymentPreflightResult> ValidateAsync(DeploymentPreflightRequest request,
        CancellationToken cancellationToken = default);
    Task<StartDeploymentResult> RequestStartAsync(StartDeploymentRequest request,
        CancellationToken cancellationToken = default);
    Task<AddDeploymentAgentResult> RequestAddAgentAsync(Guid deploymentId,
        AddDeploymentAgentLifecycleRequest request, CancellationToken cancellationToken = default);
    Task<DurableResult> RequestDeploymentCommandAsync(Guid deploymentId, DeploymentCommandType command,
        DeploymentLifecycleRequest request, CancellationToken cancellationToken = default);
    Task<DurableResult> RequestAgentCommandAsync(Guid deploymentAgentId, DeploymentCommandType command,
        DeploymentLifecycleRequest request, CancellationToken cancellationToken = default);
}

public sealed class LiveDeploymentPreflightService(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    IAgentLifecycleStore lifecycle,
    IAgentPackageResolver packages,
    LiveExecutionRuntimeOptions execution,
    ExecutionOwnershipRegistry ownership,
    LiveAnalysisProfileRegistry analysisProfiles,
    LiveEngineState engineState,
    ILiveTradingRuntimeCoordinator runtime,
    IOptions<LiveMarketUniverseOptions> markets,
    IOptions<LiveOandaOptions> brokerOptions) : ILiveDeploymentPreflightService
{
    public async Task<DeploymentPreflightResult> ValidateAsync(
        DeploymentPreflightRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gates = new List<DeploymentPreflightGate>();
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var account = await (
                from a in db.BrokerAccounts.AsNoTracking()
                join s in db.BrokerAccountSettingsRevisions.AsNoTracking()
                    on a.BrokerAccountId equals s.BrokerAccountId
                join e in db.BrokerEnvironments.AsNoTracking()
                    on s.BrokerEnvironmentId equals e.BrokerEnvironmentId
                where a.BrokerAccountId == request.BrokerAccountId && s.Active
                select new
                {
                    a.Enabled,
                    s.ExternalAccountId,
                    e.EnvironmentCode,
                    e.IsLive,
                    EnvironmentEnabled = e.Enabled
                })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        Add(gates, "broker_account", account is { Enabled: true, EnvironmentEnabled: true },
            account is null ? "Broker account or active environment revision was not found."
                : account.Enabled && account.EnvironmentEnabled ? "Broker account is enabled."
                : "Broker account or environment is disabled.");
        bool environmentMatches = account is not null && string.Equals(
            account.EnvironmentCode, request.BrokerEnvironment, StringComparison.OrdinalIgnoreCase);
        Add(gates, "broker_environment", environmentMatches,
            environmentMatches ? "Requested broker environment matches the account."
                : "Requested broker environment does not match the account revision.");
        bool hostAccountMatches = account is not null && StringComparer.Ordinal.Equals(
            account.ExternalAccountId, brokerOptions.Value.AccountId);
        Add(gates, "host_broker_account", hostAccountMatches,
            hostAccountMatches ? "The requested database account is the broker account owned by this host."
                : "The requested database account is not the broker account owned by this host.");

        string? canonicalInstrument = await db.Instruments.AsNoTracking()
            .Where(x => x.InstrumentId == request.InstrumentId)
            .Select(x => x.CanonicalKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        bool instrumentExists = canonicalInstrument is not null;
        bool mappingTradeable = await db.BrokerInstruments.AsNoTracking().AnyAsync(x =>
            x.BrokerAccountId == request.BrokerAccountId && x.InstrumentId == request.InstrumentId
            && x.Tradeable && x.ValidFrom <= DateTimeOffset.UtcNow
            && (x.ValidTo == null || x.ValidTo > DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        Add(gates, "instrument_mapping", instrumentExists && mappingTradeable,
            instrumentExists && mappingTradeable ? "An active tradeable broker mapping exists."
                : "Instrument or active tradeable broker mapping was not found.");

        var revision = await db.PolicyRevisions.AsNoTracking()
            .Where(x => x.PolicyRevisionId == request.PolicyRevisionId)
            .Select(x => new { x.Status, x.ConfigurationHash })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        bool revisionEligible = revision is not null && revision.Status is not
            (PolicyRevisionStatus.Research or PolicyRevisionStatus.Retired or PolicyRevisionStatus.Suspended);
        Add(gates, "policy_revision", revisionEligible,
            revisionEligible ? $"Policy revision status is {revision!.Status}."
                : revision is null ? "Exact policy revision was not found."
                : $"Policy revision status {revision.Status} is not deployable.");

        ResolvedAgentPackage? package = null;
        try
        {
            package = await packages.ResolveAsync(request.PolicyRevisionId, cancellationToken).ConfigureAwait(false);
            Add(gates, "agent_package", true, "Exact immutable package and linked artifacts resolved.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            Add(gates, "agent_package", false, ex.Message);
        }
        Add(gates, "artifacts_compatible", package is not null,
            package is not null ? "All package artifacts resolved and passed compatibility validation."
                : "Package artifacts are missing or incompatible.");

        bool configHashMatches = package is not null && revision is not null
            && StringComparer.Ordinal.Equals(package.ConfigurationHash, revision.ConfigurationHash)
            && (request.ExpectedConfigurationHash is null || StringComparer.Ordinal.Equals(
                package.ConfigurationHash, request.ExpectedConfigurationHash));
        Add(gates, "configuration_hash", configHashMatches,
            configHashMatches ? "Configuration hash matches the exact revision."
                : "Configuration hash mismatch.");
        bool packageHashMatches = package is not null && (request.ExpectedPackageHash is null
            || StringComparer.Ordinal.Equals(package.PackageHash, request.ExpectedPackageHash));
        Add(gates, "package_hash", packageHashMatches,
            packageHashMatches ? "Package hash is pinned."
                : "Expected package hash does not match the resolved package.");

        PolicyPermissionDetail? permissions = revision is null ? null : await lifecycle.GetPermissionsAsync(
            request.PolicyRevisionId, request.BrokerEnvironment, request.BrokerAccountId, cancellationToken)
            .ConfigureAwait(false);
        PolicyExecutionPermission requiredPermission = RequiredPermission(request.Mode, account?.IsLive == true);
        bool permissionGranted = permissions is not null
            && (permissions.EffectivePermissions & requiredPermission) == requiredPermission;
        Add(gates, "execution_permission", permissionGranted,
            permissionGranted ? $"Explicit {requiredPermission} permission is effective."
                : $"Explicit {requiredPermission} permission is not effective.");

        ParityCertificationDetail? certification = revision is null ? null
            : await lifecycle.GetLatestParityCertificationAsync(request.PolicyRevisionId, cancellationToken)
                .ConfigureAwait(false);
        bool parityPassed = !request.RequireParityCertification || package is not null
            && certification is { Status: ParityCertificationStatus.Passed, MismatchCount: 0 }
            && StringComparer.Ordinal.Equals(certification.ConfigurationHash, package.ConfigurationHash)
            && StringComparer.Ordinal.Equals(certification.SourceCommit, package.SourceCommit);
        Add(gates, "parity_certification", parityPassed,
            parityPassed ? request.RequireParityCertification ? "Current exact-revision parity certification passed."
                    : "Parity certification was not required."
                : "A current passing certification for the exact configuration and source commit is required.");

        bool capabilities = request.Mode is AgentMode.RecordOnly or AgentMode.Shadow || package is not null
            && OandaBrokerClientFactory.CapabilityMatrix.AtomicStopOnFill
            && OandaBrokerClientFactory.CapabilityMatrix.AtomicTakeProfitOnFill
            && (!package.PositionManagement.EnableScaleOut
                || OandaBrokerClientFactory.CapabilityMatrix.PartialClosePracticeCertified);
        Add(gates, "broker_capabilities", capabilities,
            capabilities ? "Broker capabilities satisfy the package management policy."
                : "Broker capability certification does not satisfy the management policy.");

        bool writesAllowed = request.Mode is AgentMode.RecordOnly or AgentMode.Shadow || execution.BrokerWritesEnabled;
        Add(gates, "broker_writes_switch", writesAllowed,
            writesAllowed ? "Global broker-write gate permits this mode."
                : "Global broker writes are disabled.");
        bool automaticAllowed = request.Mode != AgentMode.Automatic || execution.AutomaticExecutionEnabled;
        Add(gates, "automatic_execution_switch", automaticAllowed,
            automaticAllowed ? "Global automatic-execution gate permits this mode."
                : "Global automatic execution is disabled.");

        var runtimeKey = new BrokerInstrumentRuntimeKey(request.BrokerAccountId, request.InstrumentId);
        bool ownerAvailable = request.Mode is AgentMode.RecordOnly or AgentMode.Shadow
            || ownership.IsAvailable(runtimeKey, request.DeploymentAgentId);
        Add(gates, "execution_ownership", ownerAvailable,
            ownerAvailable ? "Executable ownership is available."
                : "Another executable agent owns this account/instrument.");
        Add(gates, "required_intervals", package is not null && package.RequiredIntervals.Count > 0,
            package is null ? "Package intervals could not be resolved."
                : $"Required intervals resolved: {string.Join(", ", package.RequiredIntervals.Order())}.");

        LiveMarketDefinition? configuredMarket = canonicalInstrument is null ? null
            : markets.Value.Markets.SingleOrDefault(x => x.Enabled
                && x.Instrument == new InstrumentKey(canonicalInstrument));
        bool intervalsSupported = package is not null && configuredMarket is not null
            && package.RequiredIntervals.IsSubsetOf(configuredMarket.AnalysisIntervals);
        Add(gates, "analysis_intervals_supported", intervalsSupported,
            intervalsSupported ? "The host market supports every required analysis interval."
                : "The host market is missing one or more required analysis intervals.");

        AnalysisProfileKey? profile = package is null ? null : analysisProfiles.GetOrCreateProfile(
            package.FeaturePolicy.AnnotationOptions, package.RequiredIntervals);
        var status = engineState.Snapshot();
        bool leaseAvailable = StringComparer.Ordinal.Equals(status.EngineState, "Running")
            && StringComparer.Ordinal.Equals(status.Connections.LeaseState, "Acquired");
        Add(gates, "broker_account_lease", leaseAvailable,
            leaseAvailable ? "This running host owns the broker account lease."
                : "This host does not currently own an active broker account lease.");

        bool warmupAvailable = profile is not null && canonicalInstrument is not null
            && status.AnalysisProfiles.Any(x => StringComparer.Ordinal.Equals(x.Instrument, canonicalInstrument)
                && StringComparer.Ordinal.Equals(x.ProfileHash, profile.ProfileHash)
                && x.LastAvailableAt.HasValue && x.FailureCount == 0);
        Add(gates, "warmup_data", warmupAvailable,
            warmupAvailable ? "The exact analysis profile has a current warmed snapshot."
                : "The exact analysis profile has not produced a healthy warmed snapshot.");

        LiveRuntimeSnapshot runtimeSnapshot = runtime.Snapshot;
        bool reconciliationClean = runtimeSnapshot.CanOpenNewEntries
            && runtimeSnapshot.LastReconciliation is
            {
                CanOpenNewEntries: true,
                RequiresOperatorReview: false
            };
        Add(gates, "reconciliation_clean", reconciliationClean,
            reconciliationClean ? "The latest authoritative broker reconciliation is clean."
                : "A clean authoritative broker reconciliation is required.");
        Add(gates, "database_persistence", true,
            "PostgreSQL lifecycle, reference, permission, and certification reads succeeded.");

        return new DeploymentPreflightResult
        {
            Passed = gates.All(x => x.Passed),
            Gates = gates,
            PolicyId = package?.PolicyId,
            StrategyId = package?.StrategyId,
            ConfigurationHash = package?.ConfigurationHash,
            PackageHash = package?.PackageHash,
            RequiredIntervals = package?.RequiredIntervals.Order().Select(x => x.ToString()).ToArray() ?? []
        };
    }

    private static void Add(List<DeploymentPreflightGate> gates, string code, bool passed, string message) =>
        gates.Add(new DeploymentPreflightGate(code, passed, message));

    private static PolicyExecutionPermission RequiredPermission(AgentMode mode, bool live) => mode switch
    {
        AgentMode.RecordOnly or AgentMode.Shadow => PolicyExecutionPermission.Shadow,
        AgentMode.ManualApproval => live ? PolicyExecutionPermission.LiveManual : PolicyExecutionPermission.DemoManual,
        AgentMode.Automatic => live ? PolicyExecutionPermission.LiveAutomatic : PolicyExecutionPermission.DemoAutomatic,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

public sealed class LiveDeploymentOrchestrator(
    ILiveDeploymentPreflightService preflight,
    IAgentLifecycleStore lifecycle,
    IAgentPackageResolver packages,
    HostInstanceIdentity host) : ILiveDeploymentOrchestrator
{
    public Task<DeploymentPreflightResult> ValidateAsync(DeploymentPreflightRequest request,
        CancellationToken cancellationToken = default) => preflight.ValidateAsync(request, cancellationToken);

    public async Task<StartDeploymentResult> RequestStartAsync(
        StartDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMutation(request.IdempotencyKey, request.Actor, request.Reason);
        DeploymentPreflightResult validation = await preflight.ValidateAsync(request.Preflight, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Passed)
            throw new DeploymentValidationException(validation);

        Guid deploymentId = StableId($"deployment|{request.IdempotencyKey}");
        Guid deploymentAgentId = StableId($"agent|{request.IdempotencyKey}");
        Guid commandId = StableId($"start|{request.IdempotencyKey}");
        ResolvedAgentPackage package = await packages.ResolveAsync(
            request.Preflight.PolicyRevisionId, cancellationToken).ConfigureAwait(false);
        string deploymentHash = Hash(string.Join('|', request.Preflight.BrokerAccountId,
            request.Preflight.BrokerEnvironment.ToUpperInvariant(), request.Preflight.InstrumentId,
            request.Preflight.Mode, package.PackageHash, host.Value));

        EnsureAccepted(await lifecycle.CreateDeploymentAsync(new CreateDeploymentRequest
        {
            DeploymentId = deploymentId,
            BrokerAccountId = request.Preflight.BrokerAccountId,
            Environment = request.Preflight.BrokerEnvironment.ToUpperInvariant(),
            HostInstanceId = host.Value,
            RequestedBy = request.Actor,
            DeploymentHash = deploymentHash,
            IdempotencyKey = request.IdempotencyKey
        }, cancellationToken).ConfigureAwait(false));
        DeploymentDetail detail = await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Deployment disappeared after creation.");
        EnsureAccepted(await lifecycle.AddDeploymentAgentAsync(new AddDeploymentAgentRequest
        {
            DeploymentAgentId = deploymentAgentId,
            DeploymentId = deploymentId,
            PolicyRevisionId = package.PolicyRevisionId,
            InstrumentId = request.Preflight.InstrumentId,
            StrategyId = package.StrategyId,
            AgentMode = request.Preflight.Mode,
            PackageHash = package.PackageHash,
            RequestedBy = request.Actor,
            ExpectedVersion = detail.Deployment.Version
        }, cancellationToken).ConfigureAwait(false));
        detail = await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Deployment disappeared after creation.");
        EnsureAccepted(await lifecycle.EnqueueCommandAsync(new EnqueueDeploymentCommandRequest
        {
            CommandId = commandId,
            DeploymentId = deploymentId,
            DeploymentAgentId = deploymentAgentId,
            CommandType = DeploymentCommandType.StartAgent,
            RequestedBy = request.Actor,
            ExpectedVersion = detail.Deployment.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                request.Reason,
                Preflight = validation
            }),
            IdempotencyKey = request.IdempotencyKey
        }, cancellationToken).ConfigureAwait(false));
        detail = await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Deployment disappeared after command enqueue.");
        return new StartDeploymentResult(deploymentId, deploymentAgentId, commandId,
            detail.Deployment.Version, validation);
    }

    public async Task<AddDeploymentAgentResult> RequestAddAgentAsync(Guid deploymentId,
        AddDeploymentAgentLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMutation(request.IdempotencyKey, request.Actor, request.Reason);
        DeploymentDetail detail = await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Deployment '{deploymentId}' was not found.");
        if (detail.Deployment.Status is not (DeploymentStatus.Active or DeploymentStatus.Paused))
            throw new InvalidOperationException("Agents can only be added to an active or paused deployment.");
        EnsureDeploymentScope(detail, request.Preflight);

        DeploymentPreflightResult validation = await preflight.ValidateAsync(request.Preflight, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Passed)
            throw new DeploymentValidationException(validation);
        ResolvedAgentPackage package = await packages.ResolveAsync(
            request.Preflight.PolicyRevisionId, cancellationToken).ConfigureAwait(false);
        Guid agentId = StableId($"agent|{deploymentId:N}|{request.IdempotencyKey}");
        Guid commandId = StableId($"start|{deploymentId:N}|{request.IdempotencyKey}");
        DeploymentAgentDetail? existing = detail.Agents.SingleOrDefault(x => x.DeploymentAgentId == agentId);
        if (existing is null && detail.Deployment.Version != request.ExpectedVersion)
            throw new DeploymentConcurrencyException(detail.Deployment.Version);
        if (existing is not null && (existing.PolicyRevisionId != package.PolicyRevisionId
            || existing.InstrumentId != request.Preflight.InstrumentId
            || existing.AgentMode != request.Preflight.Mode
            || !StringComparer.Ordinal.Equals(existing.PackageHash, package.PackageHash)))
            throw new InvalidOperationException("Idempotency key was already used for a different Agent assignment.");
        EnsureAccepted(await lifecycle.AddDeploymentAgentAsync(new AddDeploymentAgentRequest
        {
            DeploymentAgentId = agentId,
            DeploymentId = deploymentId,
            PolicyRevisionId = package.PolicyRevisionId,
            InstrumentId = request.Preflight.InstrumentId,
            StrategyId = package.StrategyId,
            AgentMode = request.Preflight.Mode,
            PackageHash = package.PackageHash,
            RequestedBy = request.Actor,
            ExpectedVersion = existing is null ? request.ExpectedVersion : null
        }, cancellationToken).ConfigureAwait(false));
        detail = await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Deployment disappeared after Agent creation.");
        EnsureAccepted(await lifecycle.EnqueueCommandAsync(new EnqueueDeploymentCommandRequest
        {
            CommandId = commandId,
            DeploymentId = deploymentId,
            DeploymentAgentId = agentId,
            CommandType = DeploymentCommandType.StartAgent,
            RequestedBy = request.Actor,
            ExpectedVersion = detail.Deployment.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { request.Reason, Preflight = validation }),
            IdempotencyKey = request.IdempotencyKey
        }, cancellationToken).ConfigureAwait(false));
        detail = await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Deployment disappeared after command enqueue.");
        return new AddDeploymentAgentResult(deploymentId, agentId, commandId,
            detail.Deployment.Version, validation);
    }

    public Task<DurableResult> RequestDeploymentCommandAsync(Guid deploymentId,
        DeploymentCommandType command, DeploymentLifecycleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (command is not (DeploymentCommandType.StopDeployment or DeploymentCommandType.Reconcile
            or DeploymentCommandType.PauseDeployment or DeploymentCommandType.ResumeDeployment))
            throw new ArgumentOutOfRangeException(nameof(command));
        return Enqueue(deploymentId, null, command, request, cancellationToken);
    }

    public async Task<DurableResult> RequestAgentCommandAsync(Guid deploymentAgentId,
        DeploymentCommandType command, DeploymentLifecycleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (command is not (DeploymentCommandType.PauseAgent or DeploymentCommandType.ResumeAgent
            or DeploymentCommandType.StopAgent or DeploymentCommandType.DrainAgent
            or DeploymentCommandType.ReplaceAgentRevision))
            throw new ArgumentOutOfRangeException(nameof(command));
        DeploymentAgentDetail agent = await lifecycle.GetDeploymentAgentAsync(deploymentAgentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Deployment agent '{deploymentAgentId}' was not found.");
        if (command == DeploymentCommandType.ReplaceAgentRevision)
            return await EnqueueReplacementAsync(agent, request, cancellationToken).ConfigureAwait(false);
        return await Enqueue(agent.DeploymentId, deploymentAgentId, command, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DurableResult> EnqueueReplacementAsync(DeploymentAgentDetail current,
        DeploymentLifecycleRequest request, CancellationToken cancellationToken)
    {
        ValidateMutation(request.IdempotencyKey, request.Actor, request.Reason);
        Guid revisionId = request.ReplacementPolicyRevisionId
            ?? throw new ArgumentException("ReplacementPolicyRevisionId is required for a replacement.");
        Guid replacementId = StableId($"replacement|{current.DeploymentAgentId:N}|{request.IdempotencyKey}");
        Guid commandId = StableId($"replace|{current.DeploymentAgentId:N}|{request.IdempotencyKey}");
        DeploymentDetail detail = await lifecycle.GetDeploymentAsync(current.DeploymentId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Deployment '{current.DeploymentId}' was not found.");
        ResolvedAgentPackage replacement = await packages.ResolveAsync(revisionId, cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(replacement.StrategyId, current.StrategyId))
            throw new InvalidOperationException("A replacement revision must retain the Agent strategy.");
        DeploymentAgentDetail? existing = detail.Agents.SingleOrDefault(x => x.DeploymentAgentId == replacementId);
        if (existing is not null)
        {
            if (existing.PolicyRevisionId != replacement.PolicyRevisionId
                || existing.InstrumentId != current.InstrumentId
                || existing.AgentMode != current.AgentMode
                || !StringComparer.Ordinal.Equals(existing.PackageHash, replacement.PackageHash))
            {
                throw new InvalidOperationException("Idempotency key was already used for a different replacement.");
            }

            return await lifecycle.EnqueueCommandAsync(new EnqueueDeploymentCommandRequest
            {
                CommandId = commandId,
                DeploymentId = current.DeploymentId,
                DeploymentAgentId = current.DeploymentAgentId,
                CommandType = DeploymentCommandType.ReplaceAgentRevision,
                RequestedBy = request.Actor,
                ExpectedVersion = request.ExpectedVersion,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    request.Reason,
                    ReplacementDeploymentAgentId = replacementId,
                    ReplacementPolicyRevisionId = revisionId,
                    replacement.PackageHash
                }),
                IdempotencyKey = request.IdempotencyKey
            }, cancellationToken).ConfigureAwait(false);
        }

        if (current.Status != DeploymentAgentStatus.Running)
            throw new InvalidOperationException("Only a running Agent can be replaced.");
        var preflightRequest = new DeploymentPreflightRequest
        {
            BrokerAccountId = detail.Deployment.BrokerAccountId,
            BrokerEnvironment = detail.Deployment.Environment,
            InstrumentId = current.InstrumentId,
            PolicyRevisionId = revisionId,
            Mode = current.AgentMode,
            ExpectedPackageHash = request.ExpectedPackageHash,
            DeploymentAgentId = current.DeploymentAgentId
        };
        DeploymentPreflightResult validation = await preflight.ValidateAsync(preflightRequest, cancellationToken)
            .ConfigureAwait(false);
        if (!validation.Passed)
            throw new DeploymentValidationException(validation);
        if (detail.Deployment.Version != request.ExpectedVersion)
            return DurableResult.RejectedByConcurrency("deployment_version_mismatch");
        EnsureAccepted(await lifecycle.AddDeploymentAgentAsync(new AddDeploymentAgentRequest
        {
            DeploymentAgentId = replacementId,
            DeploymentId = current.DeploymentId,
            PolicyRevisionId = replacement.PolicyRevisionId,
            InstrumentId = current.InstrumentId,
            StrategyId = replacement.StrategyId,
            AgentMode = current.AgentMode,
            PackageHash = replacement.PackageHash,
            RequestedBy = request.Actor,
            Enabled = false,
            ExpectedVersion = request.ExpectedVersion
        }, cancellationToken).ConfigureAwait(false));
        detail = await lifecycle.GetDeploymentAsync(current.DeploymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Deployment disappeared after replacement creation.");
        return await lifecycle.EnqueueCommandAsync(new EnqueueDeploymentCommandRequest
        {
            CommandId = commandId,
            DeploymentId = current.DeploymentId,
            DeploymentAgentId = current.DeploymentAgentId,
            CommandType = DeploymentCommandType.ReplaceAgentRevision,
            RequestedBy = request.Actor,
            ExpectedVersion = detail.Deployment.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                request.Reason,
                ReplacementDeploymentAgentId = replacementId,
                ReplacementPolicyRevisionId = revisionId,
                replacement.PackageHash
            }),
            IdempotencyKey = request.IdempotencyKey
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<DurableResult> Enqueue(Guid deploymentId, Guid? deploymentAgentId,
        DeploymentCommandType command, DeploymentLifecycleRequest request, CancellationToken cancellationToken)
    {
        ValidateMutation(request.IdempotencyKey, request.Actor, request.Reason);
        return lifecycle.EnqueueCommandAsync(new EnqueueDeploymentCommandRequest
        {
            CommandId = StableId($"{command}|{request.IdempotencyKey}"),
            DeploymentId = deploymentId,
            DeploymentAgentId = deploymentAgentId,
            CommandType = command,
            RequestedBy = request.Actor,
            ExpectedVersion = request.ExpectedVersion,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { request.Reason }),
            IdempotencyKey = request.IdempotencyKey
        }, cancellationToken);
    }

    private static void ValidateMutation(string idempotencyKey, string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }

    private static void EnsureDeploymentScope(DeploymentDetail deployment, DeploymentPreflightRequest request)
    {
        if (deployment.Deployment.BrokerAccountId != request.BrokerAccountId
            || !string.Equals(deployment.Deployment.Environment, request.BrokerEnvironment,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Preflight broker account/environment does not match the deployment.");
    }

    private static void EnsureAccepted(DurableResult result)
    {
        if (result.Outcome is not (DurableOutcome.Committed or DurableOutcome.Duplicate))
            throw new InvalidOperationException($"{result.ReasonCode}: {result.Detail}");
    }

    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record HostInstanceIdentity(string Value);

public sealed class DeploymentValidationException(DeploymentPreflightResult result)
    : InvalidOperationException("Deployment preflight failed.")
{
    public DeploymentPreflightResult Result { get; } = result;
}

public sealed class DeploymentConcurrencyException(long currentVersion)
    : InvalidOperationException($"Deployment version mismatch. Current version is {currentVersion}.")
{
    public long CurrentVersion { get; } = currentVersion;
}
