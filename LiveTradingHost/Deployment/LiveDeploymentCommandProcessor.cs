using System.Text.Json;
using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using LiveTrading.Reconciliation;
using LiveTrading.Runtime;

namespace LiveTradingHost.Deployment;

/// <summary>Claims the PostgreSQL command queue and makes database lifecycle state match the
/// exact package currently registered in this host. All operations are idempotent and recoverable.</summary>
public sealed class LiveDeploymentCommandProcessor(
    IServiceScopeFactory scopeFactory,
    HostInstanceIdentity host,
    LiveEngineHostedService engine,
    ILiveDynamicAgentRuntimeService agents,
    ILiveTradingRuntimeCoordinator accountRuntime,
    LiveStatusRealtimePublisher realtime,
    ILogger<LiveDeploymentCommandProcessor> logger) : BackgroundService
{
    private sealed class DeploymentReconciliationException(string message)
        : InvalidOperationException(message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!engine.IsReady && !stoppingToken.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
            return;

        await RecoverAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IAgentLifecycleStore lifecycle = scope.ServiceProvider.GetRequiredService<IAgentLifecycleStore>();
            IReadOnlyList<ClaimedDeploymentCommand> commands = await lifecycle.ClaimPendingCommandsAsync(
                host.Value, 16, stoppingToken).ConfigureAwait(false);
            if (commands.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false);
                continue;
            }

            foreach (ClaimedDeploymentCommand command in commands)
                await ProcessAsync(scope.ServiceProvider, lifecycle, command, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(IServiceProvider services, IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, CancellationToken cancellationToken)
    {
        string correlationId = command.CommandId.ToString("N");
        try
        {
            switch (command.CommandType)
            {
                case DeploymentCommandType.StartAgent:
                    await StartAsync(services, lifecycle, command, cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.PauseAgent:
                    await ChangeAgentAsync(lifecycle, command, DeploymentAgentStatus.Paused,
                        id => agents.PauseAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.ResumeAgent:
                    await RequireCleanReconciliationAsync(ReconciliationTrigger.Resume, cancellationToken)
                        .ConfigureAwait(false);
                    await ChangeAgentAsync(lifecycle, command, DeploymentAgentStatus.Running,
                        id => agents.ResumeAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.DrainAgent:
                    await ChangeAgentAsync(lifecycle, command, DeploymentAgentStatus.Draining,
                        id => agents.DrainAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.StopAgent:
                    await ChangeAgentAsync(lifecycle, command, DeploymentAgentStatus.Stopped,
                        id => agents.StopAsync(id, cancellationToken), cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.ReplaceAgentRevision:
                    await ReplaceAsync(services, lifecycle, command, cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.PauseDeployment:
                    await PauseDeploymentAsync(lifecycle, command, cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.ResumeDeployment:
                    await ResumeDeploymentAsync(lifecycle, command, cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.StopDeployment:
                    await StopDeploymentAsync(lifecycle, command, cancellationToken).ConfigureAwait(false);
                    break;
                case DeploymentCommandType.Reconcile:
                    await accountRuntime.ReconcileAsync(ReconciliationTrigger.Manual, cancellationToken)
                        .ConfigureAwait(false);
                    await AppendEventAsync(lifecycle, command, "DeploymentReconciled", null, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported lifecycle command '{command.CommandType}'.");
            }

            EnsureAccepted(await lifecycle.CompleteCommandAsync(command.CommandId, host.Value, true,
                null, null, cancellationToken).ConfigureAwait(false));
            DeploymentDetail? changed = await lifecycle.GetDeploymentAsync(
                command.DeploymentId, cancellationToken).ConfigureAwait(false);
            if (changed is not null)
                await realtime.PublishDeploymentChangedAsync(changed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string code = ex is DeploymentValidationException ? "deployment_preflight_failed"
                : ex is DeploymentReconciliationException ? "deployment_reconciliation_not_clean"
                : ex is KeyNotFoundException ? "lifecycle_target_not_found"
                : "lifecycle_command_failed";
            logger.LogError(ex, "Deployment command {CommandId} ({CommandType}) failed with {ErrorCode}.",
                command.CommandId, command.CommandType, code);
            await MarkFaultAsync(lifecycle, command, code, ex.Message, cancellationToken).ConfigureAwait(false);
            EnsureAccepted(await lifecycle.CompleteCommandAsync(command.CommandId, host.Value, false,
                code, ex.Message, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task StartAsync(IServiceProvider services, IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, CancellationToken cancellationToken)
    {
        DeploymentAgentDetail agent = await RequireAgentAsync(lifecycle, command, cancellationToken)
            .ConfigureAwait(false);
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        string reason = ReadReason(command.PayloadJson);

        await TransitionDeploymentIfAsync(lifecycle, command, DeploymentStatus.Requested,
            DeploymentStatus.Validating, reason, cancellationToken).ConfigureAwait(false);
        await TransitionAgentIfAsync(lifecycle, command, DeploymentAgentStatus.Requested,
            DeploymentAgentStatus.Validating, true, cancellationToken).ConfigureAwait(false);

        var preflightRequest = new DeploymentPreflightRequest
        {
            BrokerAccountId = detail.Deployment.BrokerAccountId,
            BrokerEnvironment = detail.Deployment.Environment,
            InstrumentId = agent.InstrumentId,
            PolicyRevisionId = agent.PolicyRevisionId,
            Mode = agent.AgentMode,
            ExpectedPackageHash = agent.PackageHash,
            DeploymentAgentId = agent.DeploymentAgentId
        };
        DeploymentPreflightResult validation = await services
            .GetRequiredService<ILiveDeploymentPreflightService>()
            .ValidateAsync(preflightRequest, cancellationToken).ConfigureAwait(false);
        if (!validation.Passed)
        {
            await TransitionAgentIfAsync(lifecycle, command, DeploymentAgentStatus.Validating,
                DeploymentAgentStatus.ValidationFailed, false, cancellationToken,
                "deployment_preflight_failed", JsonSerializer.Serialize(validation.Gates)).ConfigureAwait(false);
            throw new DeploymentValidationException(validation);
        }

        await TransitionDeploymentIfAsync(lifecycle, command, DeploymentStatus.Validating,
            DeploymentStatus.Preparing, reason, cancellationToken).ConfigureAwait(false);
        await TransitionAgentIfAsync(lifecycle, command, DeploymentAgentStatus.Validating,
            DeploymentAgentStatus.Preparing, true, cancellationToken).ConfigureAwait(false);
        await TransitionDeploymentIfAsync(lifecycle, command, DeploymentStatus.Preparing,
            DeploymentStatus.WarmingUp, reason, cancellationToken).ConfigureAwait(false);
        await TransitionAgentIfAsync(lifecycle, command, DeploymentAgentStatus.Preparing,
            DeploymentAgentStatus.WarmingUp, true, cancellationToken).ConfigureAwait(false);

        await TransitionDeploymentIfAsync(lifecycle, command, DeploymentStatus.WarmingUp,
            DeploymentStatus.Reconciling, reason, cancellationToken).ConfigureAwait(false);
        BrokerReconciliationReport reconciliation = await RequireCleanReconciliationAsync(
            ReconciliationTrigger.Manual, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(lifecycle, command, "DeploymentReconciled",
            JsonSerializer.Serialize(new
            {
                reconciliation.ReconciliationId,
                DifferenceCount = reconciliation.Differences.Count
            }), cancellationToken).ConfigureAwait(false);

        agent = await RequireAgentAsync(lifecycle, command, cancellationToken).ConfigureAwait(false);
        if (agent.Status != DeploymentAgentStatus.Running)
            await agents.StartAsync(agent, cancellationToken).ConfigureAwait(false);
        await TransitionAgentIfAsync(lifecycle, command, DeploymentAgentStatus.WarmingUp,
            DeploymentAgentStatus.Running, true, cancellationToken).ConfigureAwait(false);
        await TransitionDeploymentIfAsync(lifecycle, command, DeploymentStatus.Reconciling,
            DeploymentStatus.Active, reason, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(lifecycle, command, "AgentActivated", validation.PackageHash, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReplaceAsync(IServiceProvider services, IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, CancellationToken cancellationToken)
    {
        Guid replacementId = ReadGuid(command.PayloadJson, "replacementDeploymentAgentId");
        DeploymentAgentDetail current = await RequireAgentAsync(lifecycle, command, cancellationToken)
            .ConfigureAwait(false);
        DeploymentAgentDetail replacement = await lifecycle.GetDeploymentAgentAsync(replacementId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Replacement agent '{replacementId}' was not found.");
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        DeploymentPreflightResult validation = await services.GetRequiredService<ILiveDeploymentPreflightService>()
            .ValidateAsync(new DeploymentPreflightRequest
            {
                BrokerAccountId = detail.Deployment.BrokerAccountId,
                BrokerEnvironment = detail.Deployment.Environment,
                InstrumentId = replacement.InstrumentId,
                PolicyRevisionId = replacement.PolicyRevisionId,
                Mode = replacement.AgentMode,
                ExpectedPackageHash = replacement.PackageHash,
                DeploymentAgentId = current.DeploymentAgentId
            }, cancellationToken).ConfigureAwait(false);
        if (!validation.Passed)
            throw new DeploymentValidationException(validation);

        await TransitionAgentDirectAsync(lifecycle, replacementId, DeploymentAgentStatus.Validating, false,
            command, cancellationToken).ConfigureAwait(false);
        await TransitionAgentDirectAsync(lifecycle, replacementId, DeploymentAgentStatus.Preparing, false,
            command, cancellationToken).ConfigureAwait(false);
        await TransitionAgentDirectAsync(lifecycle, replacementId, DeploymentAgentStatus.WarmingUp, false,
            command, cancellationToken).ConfigureAwait(false);
        replacement = await lifecycle.GetDeploymentAgentAsync(replacementId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Replacement agent '{replacementId}' was not found.");
        await agents.ReplaceAsync(current.DeploymentAgentId, replacement, cancellationToken).ConfigureAwait(false);
        await TransitionAgentDirectAsync(lifecycle, current.DeploymentAgentId, DeploymentAgentStatus.Stopped, false,
            command, cancellationToken).ConfigureAwait(false);
        await TransitionAgentDirectAsync(lifecycle, replacementId, DeploymentAgentStatus.Running, true,
            command, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(lifecycle, command, "AgentPackageReplaced", JsonSerializer.Serialize(new
        {
            PreviousPackageHash = current.PackageHash,
            ReplacementPackageHash = replacement.PackageHash,
            ReplacementDeploymentAgentId = replacementId
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task ChangeAgentAsync(IAgentLifecycleStore lifecycle, ClaimedDeploymentCommand command,
        DeploymentAgentStatus status, Func<Guid, Task<long>> runtimeChange, CancellationToken cancellationToken)
    {
        DeploymentAgentDetail agent = await RequireAgentAsync(lifecycle, command, cancellationToken)
            .ConfigureAwait(false);
        if (agent.Status == status)
            return;
        await runtimeChange(agent.DeploymentAgentId).ConfigureAwait(false);
        await TransitionAgentDirectAsync(lifecycle, agent.DeploymentAgentId, status,
            status is DeploymentAgentStatus.Running, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task PauseDeploymentAsync(IAgentLifecycleStore lifecycle, ClaimedDeploymentCommand command,
        CancellationToken cancellationToken)
    {
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        foreach (DeploymentAgentDetail agent in detail.Agents.Where(x => x.Status == DeploymentAgentStatus.Running))
        {
            await agents.PauseAsync(agent.DeploymentAgentId, cancellationToken).ConfigureAwait(false);
            await TransitionAgentDirectAsync(lifecycle, agent.DeploymentAgentId, DeploymentAgentStatus.Paused,
                false, command, cancellationToken).ConfigureAwait(false);
        }
        await TransitionDeploymentCurrentAsync(lifecycle, command, DeploymentStatus.Paused,
            ReadReason(command.PayloadJson), cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeDeploymentAsync(IAgentLifecycleStore lifecycle, ClaimedDeploymentCommand command,
        CancellationToken cancellationToken)
    {
        await RequireCleanReconciliationAsync(ReconciliationTrigger.Resume, cancellationToken)
            .ConfigureAwait(false);
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        foreach (DeploymentAgentDetail agent in detail.Agents.Where(x => x.Status == DeploymentAgentStatus.Paused))
        {
            await agents.ResumeAsync(agent.DeploymentAgentId, cancellationToken).ConfigureAwait(false);
            await TransitionAgentDirectAsync(lifecycle, agent.DeploymentAgentId, DeploymentAgentStatus.Running,
                true, command, cancellationToken).ConfigureAwait(false);
        }
        await TransitionDeploymentCurrentAsync(lifecycle, command, DeploymentStatus.Active,
            ReadReason(command.PayloadJson), cancellationToken).ConfigureAwait(false);
    }

    private async Task StopDeploymentAsync(IAgentLifecycleStore lifecycle, ClaimedDeploymentCommand command,
        CancellationToken cancellationToken)
    {
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (detail.Deployment.Status is DeploymentStatus.Active or DeploymentStatus.Paused)
            await TransitionDeploymentCurrentAsync(lifecycle, command, DeploymentStatus.Draining,
                ReadReason(command.PayloadJson), cancellationToken).ConfigureAwait(false);

        foreach (DeploymentAgentDetail agent in detail.Agents.Where(x => x.Status is
                     DeploymentAgentStatus.Running or DeploymentAgentStatus.Paused))
        {
            await agents.DrainAsync(agent.DeploymentAgentId, cancellationToken).ConfigureAwait(false);
            await TransitionAgentDirectAsync(lifecycle, agent.DeploymentAgentId, DeploymentAgentStatus.Draining,
                false, command, cancellationToken).ConfigureAwait(false);
        }

        detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken).ConfigureAwait(false);
        foreach (DeploymentAgentDetail agent in detail.Agents.Where(x => x.Status == DeploymentAgentStatus.Draining))
        {
            try
            {
                await agents.StopAsync(agent.DeploymentAgentId, cancellationToken).ConfigureAwait(false);
                await TransitionAgentDirectAsync(lifecycle, agent.DeploymentAgentId, DeploymentAgentStatus.Stopped,
                    false, command, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("managed positions", StringComparison.OrdinalIgnoreCase))
            {
                await AppendEventAsync(lifecycle, command, "AgentDrainWaitingForFlatPosition",
                    agent.DeploymentAgentId.ToString("N"), cancellationToken).ConfigureAwait(false);
            }
        }

        detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken).ConfigureAwait(false);
        if (detail.Agents.All(x => x.Status is DeploymentAgentStatus.Stopped or DeploymentAgentStatus.ValidationFailed))
            await TransitionDeploymentCurrentAsync(lifecycle, command, DeploymentStatus.Stopped,
                ReadReason(command.PayloadJson), cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RequireCleanReconciliationAsync(ReconciliationTrigger.Resume, cancellationToken)
                .ConfigureAwait(false);
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IAgentLifecycleStore lifecycle = scope.ServiceProvider.GetRequiredService<IAgentLifecycleStore>();
            for (int offset = 0;; offset += 200)
            {
                Page<DeploymentSummary> page = await lifecycle.ListDeploymentsAsync(offset, 200, cancellationToken)
                    .ConfigureAwait(false);
                foreach (DeploymentSummary deployment in page.Items.Where(x =>
                             StringComparer.Ordinal.Equals(x.HostInstanceId, host.Value)
                             && x.Status is DeploymentStatus.Active or DeploymentStatus.Paused or DeploymentStatus.Draining))
                {
                    DeploymentDetail? detail = await lifecycle.GetDeploymentAsync(deployment.DeploymentId, cancellationToken)
                        .ConfigureAwait(false);
                    if (detail is null)
                        continue;
                    foreach (DeploymentAgentDetail agent in detail.Agents.Where(x => x.Status is
                                 DeploymentAgentStatus.Running or DeploymentAgentStatus.Paused or DeploymentAgentStatus.Draining))
                    {
                        await agents.StartAsync(agent, cancellationToken).ConfigureAwait(false);
                        if (agent.Status == DeploymentAgentStatus.Paused)
                            await agents.PauseAsync(agent.DeploymentAgentId, cancellationToken).ConfigureAwait(false);
                        else if (agent.Status == DeploymentAgentStatus.Draining)
                            await agents.DrainAsync(agent.DeploymentAgentId, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (offset + page.Items.Count >= page.Total)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dynamic deployment recovery failed; queued commands remain durable.");
        }
    }

    private async Task<BrokerReconciliationReport> RequireCleanReconciliationAsync(
        ReconciliationTrigger trigger, CancellationToken cancellationToken)
    {
        BrokerReconciliationReport report = await accountRuntime.ReconcileAsync(trigger, cancellationToken)
            .ConfigureAwait(false);
        if (report.CanOpenNewEntries && !report.RequiresOperatorReview)
            return report;

        string differences = string.Join(", ", report.Differences.Select(x => x.Code).Distinct());
        throw new DeploymentReconciliationException(string.IsNullOrWhiteSpace(differences)
            ? "Authoritative broker reconciliation did not permit new entries."
            : $"Authoritative broker reconciliation requires operator review: {differences}.");
    }

    private static async Task<DeploymentAgentDetail> RequireAgentAsync(IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, CancellationToken cancellationToken)
    {
        Guid id = command.DeploymentAgentId
            ?? throw new InvalidOperationException("The command does not target a deployment agent.");
        return await lifecycle.GetDeploymentAgentAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Deployment agent '{id}' was not found.");
    }

    private static async Task<DeploymentDetail> RequireDeploymentAsync(IAgentLifecycleStore lifecycle,
        Guid deploymentId, CancellationToken cancellationToken) =>
        await lifecycle.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Deployment '{deploymentId}' was not found.");

    private static async Task TransitionDeploymentIfAsync(IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, DeploymentStatus from, DeploymentStatus to, string reason,
        CancellationToken cancellationToken)
    {
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (detail.Deployment.Status != from)
            return;
        EnsureAccepted(await lifecycle.TransitionDeploymentAsync(command.DeploymentId,
            detail.Deployment.Version, to, command.RequestedBy, command.CommandId.ToString("N"), reason,
            cancellationToken).ConfigureAwait(false));
    }

    private static async Task TransitionDeploymentCurrentAsync(IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, DeploymentStatus to, string reason, CancellationToken cancellationToken)
    {
        DeploymentDetail detail = await RequireDeploymentAsync(lifecycle, command.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (detail.Deployment.Status == to)
            return;
        EnsureAccepted(await lifecycle.TransitionDeploymentAsync(command.DeploymentId,
            detail.Deployment.Version, to, command.RequestedBy, command.CommandId.ToString("N"), reason,
            cancellationToken).ConfigureAwait(false));
    }

    private static async Task TransitionAgentIfAsync(IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, DeploymentAgentStatus from, DeploymentAgentStatus to, bool enabled,
        CancellationToken cancellationToken, string? faultCode = null, string? faultMessage = null)
    {
        DeploymentAgentDetail agent = await RequireAgentAsync(lifecycle, command, cancellationToken)
            .ConfigureAwait(false);
        if (agent.Status != from)
            return;
        await TransitionAgentDirectAsync(lifecycle, agent.DeploymentAgentId, to, enabled, command,
            cancellationToken, faultCode, faultMessage).ConfigureAwait(false);
    }

    private static async Task TransitionAgentDirectAsync(IAgentLifecycleStore lifecycle, Guid agentId,
        DeploymentAgentStatus status, bool enabled, ClaimedDeploymentCommand command,
        CancellationToken cancellationToken, string? faultCode = null, string? faultMessage = null)
    {
        EnsureAccepted(await lifecycle.TransitionDeploymentAgentAsync(agentId, status, enabled,
            command.RequestedBy, command.CommandId.ToString("N"), faultCode, faultMessage, cancellationToken)
            .ConfigureAwait(false));
    }

    private static async Task AppendEventAsync(IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, string eventType, string? detail, CancellationToken cancellationToken)
    {
        EnsureAccepted(await lifecycle.AppendDeploymentEventAsync(command.DeploymentId,
            command.DeploymentAgentId, eventType, command.RequestedBy, command.CommandId.ToString("N"),
            null, detail, cancellationToken).ConfigureAwait(false));
    }

    private static async Task MarkFaultAsync(IAgentLifecycleStore lifecycle,
        ClaimedDeploymentCommand command, string code, string message, CancellationToken cancellationToken)
    {
        if (command.DeploymentAgentId.HasValue)
        {
            DeploymentAgentDetail? agent = await lifecycle.GetDeploymentAgentAsync(
                command.DeploymentAgentId.Value, cancellationToken).ConfigureAwait(false);
            if (agent is not null && agent.Status is not (DeploymentAgentStatus.Stopped
                    or DeploymentAgentStatus.ValidationFailed or DeploymentAgentStatus.Faulted))
            {
                await lifecycle.TransitionDeploymentAgentAsync(agent.DeploymentAgentId,
                    DeploymentAgentStatus.Faulted, false, command.RequestedBy, command.CommandId.ToString("N"),
                    code, message, cancellationToken).ConfigureAwait(false);
            }
        }
        await lifecycle.AppendDeploymentEventAsync(command.DeploymentId, command.DeploymentAgentId,
            "DeploymentCommandFailed", command.RequestedBy, command.CommandId.ToString("N"), code,
            JsonSerializer.Serialize(new { message }), cancellationToken).ConfigureAwait(false);
    }

    private static string ReadReason(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("Reason", out JsonElement reason)
            || document.RootElement.TryGetProperty("reason", out reason)
            ? reason.GetString() ?? "Lifecycle command"
            : "Lifecycle command";
    }

    private static Guid ReadGuid(string payload, string property)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        foreach (JsonProperty item in document.RootElement.EnumerateObject())
        {
            if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(item.Value.GetString(), out Guid value))
                return value;
        }
        throw new ArgumentException($"Command payload property '{property}' is required.");
    }

    private static void EnsureAccepted(DurableResult result)
    {
        if (result.Outcome is not (DurableOutcome.Committed or DurableOutcome.Duplicate))
            throw new InvalidOperationException($"{result.ReasonCode}: {result.Detail}");
    }
}
