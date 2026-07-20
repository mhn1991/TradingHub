using Agent.Factories;
using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using DBManager.Postgres;
using LiveTradingHost.Deployment;
using Microsoft.EntityFrameworkCore;

namespace LiveTradingHost.Api;

public sealed record StartDeploymentApiRequest
{
    public required DeploymentPreflightRequest Preflight { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
}

public sealed record AddDeploymentAgentApiRequest
{
    public required DeploymentPreflightRequest Preflight { get; init; }
    public string? IdempotencyKey { get; init; }
    public required long ExpectedVersion { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
}

public sealed record DeploymentLifecycleApiRequest
{
    public string? IdempotencyKey { get; init; }
    public required long ExpectedVersion { get; init; }
    public required string Actor { get; init; }
    public required string Reason { get; init; }
    public Guid? ReplacementPolicyRevisionId { get; init; }
    public string? ExpectedPackageHash { get; init; }
}

public sealed record LifecycleApiError(
    string Code, string Message, object? Detail = null, long? CurrentVersion = null);

public sealed record LiveBrokerAccountOption(
    Guid BrokerAccountId, string Broker, string DisplayName, string Environment,
    bool IsLive, string AccountCurrency);

public sealed record LiveInstrumentOption(
    long InstrumentId, string CanonicalKey, string DisplayName, string BrokerSymbol,
    string? BaseCurrency, string? QuoteCurrency);

public static class LiveDeploymentApi
{
    public static void MapLiveDeploymentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/live/agent-types", (ITradingAgentCatalog catalogue) =>
            Results.Ok(catalogue.List()));

        app.MapGet("/api/live/policies", async (int? offset, int? limit,
            IAgentLifecycleStore lifecycle, CancellationToken cancellationToken) =>
            Results.Ok(await lifecycle.ListPoliciesAsync(offset ?? 0, limit ?? 100, cancellationToken)
                .ConfigureAwait(false)));

        app.MapGet("/api/live/policy-revisions", async (Guid? policyId, int? offset, int? limit,
            IAgentLifecycleStore lifecycle, CancellationToken cancellationToken) =>
            Results.Ok(await lifecycle.ListPolicyRevisionsAsync(policyId, offset ?? 0, limit ?? 250,
                cancellationToken).ConfigureAwait(false)));

        app.MapGet("/api/live/broker-accounts", async (
            IDbContextFactory<TradingHubDbContext> contextFactory, CancellationToken cancellationToken) =>
        {
            await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            LiveBrokerAccountOption[] accounts = await (
                    from account in db.BrokerAccounts.AsNoTracking()
                    join settings in db.BrokerAccountSettingsRevisions.AsNoTracking()
                        on account.BrokerAccountId equals settings.BrokerAccountId
                    join environment in db.BrokerEnvironments.AsNoTracking()
                        on settings.BrokerEnvironmentId equals environment.BrokerEnvironmentId
                    join broker in db.Brokers.AsNoTracking() on account.BrokerId equals broker.BrokerId
                    where account.Enabled && settings.Active && environment.Enabled
                    orderby broker.DisplayName, account.MaskedAccountName
                    select new LiveBrokerAccountOption(account.BrokerAccountId, broker.DisplayName,
                        account.MaskedAccountName, environment.EnvironmentCode, environment.IsLive,
                        account.AccountCurrency))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(accounts);
        });

        app.MapGet("/api/live/broker-accounts/{brokerAccountId:guid}/instruments", async (
            Guid brokerAccountId, IDbContextFactory<TradingHubDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            LiveInstrumentOption[] instruments = await (
                    from mapping in db.BrokerInstruments.AsNoTracking()
                    join instrument in db.Instruments.AsNoTracking()
                        on mapping.InstrumentId equals instrument.InstrumentId
                    where mapping.BrokerAccountId == brokerAccountId && mapping.Tradeable
                        && mapping.ValidFrom <= now && (mapping.ValidTo == null || mapping.ValidTo > now)
                    orderby instrument.DisplayName
                    select new LiveInstrumentOption(instrument.InstrumentId, instrument.CanonicalKey,
                        instrument.DisplayName, mapping.BrokerSymbol, instrument.BaseCurrency,
                        instrument.QuoteCurrency))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(instruments);
        });

        app.MapGet("/api/live/policies/{policyRevisionId:guid}", async (Guid policyRevisionId,
            IPolicyConfigurationStore policies, CancellationToken cancellationToken) =>
        {
            PolicyRevisionDetail? revision = await policies.GetPolicyRevisionAsync(
                policyRevisionId, cancellationToken).ConfigureAwait(false);
            return revision is null ? Results.NotFound() : Results.Ok(revision);
        });

        app.MapGet("/api/live/policies/{policyRevisionId:guid}/certification", async (
            Guid policyRevisionId, IAgentLifecycleStore lifecycle, CancellationToken cancellationToken) =>
        {
            ParityCertificationDetail? certification = await lifecycle.GetLatestParityCertificationAsync(
                policyRevisionId, cancellationToken).ConfigureAwait(false);
            return certification is null ? Results.NotFound() : Results.Ok(certification);
        });

        app.MapGet("/api/live/policies/{policyRevisionId:guid}/permissions", async (
            Guid policyRevisionId, string environment, Guid? brokerAccountId,
            IAgentLifecycleStore lifecycle, CancellationToken cancellationToken) =>
            Results.Ok(await lifecycle.GetPermissionsAsync(policyRevisionId, environment,
                brokerAccountId, cancellationToken).ConfigureAwait(false)));

        app.MapPost("/api/live/deployments/preflight", async (DeploymentPreflightRequest request,
            ILiveDeploymentOrchestrator orchestrator, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () => Results.Ok(await orchestrator.ValidateAsync(
                request, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false));

        app.MapPost("/api/live/deployments", async (StartDeploymentApiRequest request,
            HttpRequest http, ILiveDeploymentOrchestrator orchestrator, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () => Results.Accepted(value: await orchestrator.RequestStartAsync(
                new StartDeploymentRequest
                {
                    Preflight = request.Preflight,
                    IdempotencyKey = IdempotencyKey(http, request.IdempotencyKey),
                    Actor = request.Actor,
                    Reason = request.Reason
                }, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false));

        app.MapGet("/api/live/deployments", async (int? offset, int? limit,
            IAgentLifecycleStore lifecycle, CancellationToken cancellationToken) =>
            Results.Ok(await lifecycle.ListDeploymentsAsync(offset ?? 0, limit ?? 100, cancellationToken)
                .ConfigureAwait(false)));

        app.MapGet("/api/live/deployments/{deploymentId:guid}", async (Guid deploymentId,
            IAgentLifecycleStore lifecycle, CancellationToken cancellationToken) =>
        {
            DeploymentDetail? deployment = await lifecycle.GetDeploymentAsync(
                deploymentId, cancellationToken).ConfigureAwait(false);
            return deployment is null ? Results.NotFound() : Results.Ok(deployment);
        });

        app.MapGet("/api/live/runtime-agents", (ILiveDynamicAgentRuntimeService runtime) =>
            Results.Ok(runtime.Snapshot));

        MapDeploymentCommand(app, "pause", DeploymentCommandType.PauseDeployment);
        MapDeploymentCommand(app, "resume", DeploymentCommandType.ResumeDeployment);
        MapDeploymentCommand(app, "reconcile", DeploymentCommandType.Reconcile);
        MapDeploymentCommand(app, "stop", DeploymentCommandType.StopDeployment);

        app.MapPost("/api/live/deployments/{deploymentId:guid}/agents", async (Guid deploymentId,
            AddDeploymentAgentApiRequest request, HttpRequest http,
            ILiveDeploymentOrchestrator orchestrator, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () => Results.Accepted(value: await orchestrator.RequestAddAgentAsync(
                deploymentId, new AddDeploymentAgentLifecycleRequest
                {
                    Preflight = request.Preflight,
                    IdempotencyKey = IdempotencyKey(http, request.IdempotencyKey),
                    ExpectedVersion = request.ExpectedVersion,
                    Actor = request.Actor,
                    Reason = request.Reason
                }, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false));

        MapAgentCommand(app, "pause", DeploymentCommandType.PauseAgent);
        MapAgentCommand(app, "resume", DeploymentCommandType.ResumeAgent);
        MapAgentCommand(app, "drain", DeploymentCommandType.DrainAgent);
        MapAgentCommand(app, "stop", DeploymentCommandType.StopAgent);
        MapAgentCommand(app, "replace", DeploymentCommandType.ReplaceAgentRevision);
    }

    private static void MapDeploymentCommand(WebApplication app, string route,
        DeploymentCommandType command)
    {
        app.MapPost($"/api/live/deployments/{{deploymentId:guid}}/{route}", async (
            Guid deploymentId, DeploymentLifecycleApiRequest request, HttpRequest http,
            ILiveDeploymentOrchestrator orchestrator, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () => FromDurable(await orchestrator.RequestDeploymentCommandAsync(
                deploymentId, command, ToRequest(request, http), cancellationToken).ConfigureAwait(false)))
                .ConfigureAwait(false));
    }

    private static void MapAgentCommand(WebApplication app, string route, DeploymentCommandType command)
    {
        app.MapPost($"/api/live/deployment-agents/{{deploymentAgentId:guid}}/{route}", async (
            Guid deploymentAgentId, DeploymentLifecycleApiRequest request, HttpRequest http,
            ILiveDeploymentOrchestrator orchestrator, CancellationToken cancellationToken) =>
            await ExecuteAsync(async () => FromDurable(await orchestrator.RequestAgentCommandAsync(
                deploymentAgentId, command, ToRequest(request, http), cancellationToken).ConfigureAwait(false)))
                .ConfigureAwait(false));
    }

    private static DeploymentLifecycleRequest ToRequest(DeploymentLifecycleApiRequest request, HttpRequest http) =>
        new()
        {
            IdempotencyKey = IdempotencyKey(http, request.IdempotencyKey),
            ExpectedVersion = request.ExpectedVersion,
            Actor = request.Actor,
            Reason = request.Reason,
            ReplacementPolicyRevisionId = request.ReplacementPolicyRevisionId,
            ExpectedPackageHash = request.ExpectedPackageHash
        };

    private static string IdempotencyKey(HttpRequest request, string? bodyValue)
    {
        string value = request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(value))
            value = bodyValue ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("An Idempotency-Key header or idempotencyKey body value is required.");
    }

    private static IResult FromDurable(DurableResult result) => result.Outcome switch
    {
        DurableOutcome.Committed or DurableOutcome.Duplicate => Results.Accepted(value: result),
        DurableOutcome.RejectedByConcurrency => Results.Json(
            new LifecycleApiError(result.ReasonCode ?? "concurrency_conflict",
                "The deployment changed; refresh and retry.", result.Detail), statusCode: StatusCodes.Status409Conflict),
        DurableOutcome.RetryableFailure => Results.Json(
            new LifecycleApiError(result.ReasonCode ?? "temporarily_unavailable",
                "The lifecycle request could not be persisted yet.", result.Detail),
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(new LifecycleApiError(result.ReasonCode ?? "request_rejected",
            "The lifecycle request was rejected.", result.Detail))
    };

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DeploymentValidationException ex)
        {
            return Results.Json(new LifecycleApiError("deployment_preflight_failed", ex.Message, ex.Result),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (DeploymentConcurrencyException ex)
        {
            return Results.Json(new LifecycleApiError("deployment_version_mismatch", ex.Message,
                CurrentVersion: ex.CurrentVersion), statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new LifecycleApiError("lifecycle_target_not_found", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new LifecycleApiError("invalid_lifecycle_request", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new LifecycleApiError("lifecycle_state_conflict", ex.Message),
                statusCode: StatusCodes.Status409Conflict);
        }
    }
}
