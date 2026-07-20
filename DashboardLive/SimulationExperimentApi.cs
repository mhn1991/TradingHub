using Simulator.Experiments;
using Simulator.Experiments.Models;

namespace Dashboard.Live;

public static class SimulationExperimentApi
{
    public static void MapSimulationExperimentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/simulation-experiments", async (
            SimulationExperimentRequest request,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                SimulationExperimentHandle handle = await service.StartAsync(request, cancellationToken);
                return Results.Accepted($"/api/simulation-experiments/{handle.Id:N}", handle);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/simulation-experiments", async (
            int? take,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) => Results.Ok(
                await service.ListAsync(Math.Clamp(take ?? 50, 1, 500), cancellationToken)));

        app.MapGet("/api/simulation-experiments/{id:guid}", async (
            Guid id,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationExperimentSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        app.MapPost("/api/simulation-experiments/{id:guid}/pause", (
            Guid id,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.PauseAsync(id, cancellationToken)));

        app.MapPost("/api/simulation-experiments/{id:guid}/resume", (
            Guid id,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.ResumeAsync(id, cancellationToken)));

        app.MapPost("/api/simulation-experiments/{id:guid}/cancel", (
            Guid id,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.CancelAsync(id, cancellationToken)));

        app.MapGet("/api/simulation-experiments/{id:guid}/results", async (
            Guid id,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationExperimentSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot is null)
                return Results.NotFound();
            return snapshot.Comparison is null
                ? Results.Conflict(new { error = $"Experiment results are not available while {snapshot.State}/{snapshot.Stage}." })
                : Results.Ok(snapshot.Comparison);
        });

        app.MapGet("/api/simulation-experiments/{id:guid}/manifest", async (
            Guid id,
            ISimulationExperimentApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationExperimentSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot.Manifest);
        });
    }

    private static async Task<IResult> ApplyAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.Accepted();
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }
}
