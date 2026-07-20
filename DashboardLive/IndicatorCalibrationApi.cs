using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;

namespace Dashboard.Live;

/// <summary>Minimal-API routes for indicator calibration (blueprint §17.2).</summary>
public static class IndicatorCalibrationApi
{
    public static void MapIndicatorCalibrationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/indicator-calibrations/preview", async (
            IndicatorCalibrationRequest request,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.PreviewAsync(request, cancellationToken));
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

        app.MapPost("/api/indicator-calibrations", async (
            IndicatorCalibrationRequest request,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                IndicatorCalibrationRunSummary summary = await service.StartAsync(request, cancellationToken);
                return Results.Accepted($"/api/indicator-calibrations/{summary.CalibrationId}", summary);
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

        app.MapGet("/api/indicator-calibrations", async (
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) => Results.Ok(await service.ListAsync(cancellationToken)));

        app.MapGet("/api/indicator-calibrations/{id}", async (
            string id,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) =>
        {
            IndicatorCalibrationRunDetails? details = await service.GetAsync(id, cancellationToken);
            return details is null ? Results.NotFound() : Results.Ok(details);
        });

        app.MapGet("/api/indicator-calibrations/{id}/artifact", async (
            string id,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) =>
        {
            IndicatorCalibrationRunDetails? details = await service.GetAsync(id, cancellationToken);
            if (details is null)
                return Results.NotFound();
            return details.Artifact is null
                ? Results.Conflict(new { error = $"Run {id} has not produced an artifact yet ({details.Summary.State})." })
                : Results.Ok(details.Artifact);
        });

        app.MapGet("/api/indicator-calibrations/{id}/ledger-summary", async (
            string id,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) =>
        {
            IndicatorCalibrationRunDetails? details = await service.GetAsync(id, cancellationToken);
            if (details is null)
                return Results.NotFound();
            return Results.Ok(new
            {
                details.Summary.CalibrationId,
                details.Summary.State,
                details.Summary.Outcome,
                FoldCount = details.Result?.Folds.Count,
                TotalCandidatesEvaluated = details.Result?.TotalCandidatesEvaluated,
                details.Result?.Reason
            });
        });

        app.MapPost("/api/indicator-calibrations/{id}/pause", (
            string id,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.PauseAsync(id, cancellationToken)));

        app.MapPost("/api/indicator-calibrations/{id}/resume", (
            string id,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.ResumeAsync(id, cancellationToken)));

        app.MapPost("/api/indicator-calibrations/{id}/cancel", (
            string id,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.CancelAsync(id, cancellationToken)));

        app.MapPost("/api/indicator-calibrations/{id}/approve", async (
            string id,
            IndicatorCalibrationApprovalBody body,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                CalibrationPromotionEvent promotionEvent = await service.ApproveAsync(new IndicatorCalibrationApprovalRequest
                {
                    CalibrationId = id,
                    ApprovedBy = body.ApprovedBy,
                    TargetEnvironment = body.TargetEnvironment,
                    ReviewNotes = body.ReviewNotes,
                    ReplacesArtifactId = body.ReplacesArtifactId
                }, cancellationToken);
                return Results.Ok(promotionEvent);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapPost("/api/indicator-calibrations/{id}/reject", (
            string id,
            IndicatorCalibrationRejectionBody body,
            IIndicatorCalibrationApplicationService service,
            CancellationToken cancellationToken) => ApplyAsync(() => service.RejectAsync(new IndicatorCalibrationRejectionRequest
            {
                CalibrationId = id,
                RejectedBy = body.RejectedBy,
                Reason = body.Reason
            }, cancellationToken)));
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
        catch (NotSupportedException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    public sealed record IndicatorCalibrationApprovalBody(
        string ApprovedBy, string TargetEnvironment, string ReviewNotes, string? ReplacesArtifactId);

    public sealed record IndicatorCalibrationRejectionBody(string RejectedBy, string Reason);
}
