using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;

namespace Dashboard.Live;

public static class SimulationProfileApi
{
    public static void MapSimulationProfileEndpoints(this WebApplication app)
    {
        app.MapPost("/api/simulation-profiles", async (
            SimulationStrategyProfile draft,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                SimulationStrategyProfile created = await store.CreateAsync(draft with
                {
                    ProfileId = draft.ProfileId == Guid.Empty ? Guid.NewGuid() : draft.ProfileId,
                    Revision = 1,
                    ContentHash = string.Empty
                }, cancellationToken);
                return Results.Created(
                    $"/api/simulation-profiles/{created.ProfileId:N}/revisions/{created.Revision}",
                    created);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapGet("/api/simulation-profiles", async (
            bool? includeArchived,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) => Results.Ok(
                await store.ListAsync(includeArchived ?? false, cancellationToken)));

        app.MapGet("/api/simulation-profiles/{id:guid}/revisions/{revision:int}", async (
            Guid id,
            int revision,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            SimulationStrategyProfile? profile = await store.ReadAsync(id, revision, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        app.MapPost("/api/simulation-profiles/{id:guid}/clone", async (
            Guid id,
            CloneSimulationProfileRequest request,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                SimulationStrategyProfile clone = await store.CloneAsync(
                    id,
                    request.SourceRevision,
                    request.CloneId ?? Guid.NewGuid(),
                    request.Name,
                    cancellationToken);
                return Results.Created(
                    $"/api/simulation-profiles/{clone.ProfileId:N}/revisions/{clone.Revision}",
                    clone);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapPost("/api/simulation-profiles/{id:guid}/revisions", async (
            Guid id,
            SimulationStrategyProfile draft,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                SimulationStrategyProfile revision = await store.CreateRevisionAsync(
                    id,
                    draft with { ProfileId = id, ContentHash = string.Empty },
                    cancellationToken);
                return Results.Created(
                    $"/api/simulation-profiles/{id:N}/revisions/{revision.Revision}",
                    revision);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapDelete("/api/simulation-profiles/{id:guid}", async (
            Guid id,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await store.ArchiveAsync(id, cancellationToken);
                return Results.NoContent();
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        app.MapGet("/api/simulation-profiles/{leftId:guid}/revisions/{leftRevision:int}/diff", async (
            Guid leftId,
            int leftRevision,
            Guid rightId,
            int rightRevision,
            ISimulationStrategyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await store.DiffAsync(
                    leftId,
                    leftRevision,
                    rightId,
                    rightRevision,
                    cancellationToken));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });
    }
}

public sealed record CloneSimulationProfileRequest
{
    public required int SourceRevision { get; init; }
    public required string Name { get; init; }
    public Guid? CloneId { get; init; }
}
