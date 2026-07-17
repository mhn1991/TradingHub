namespace Dashboard.Live;

/// <summary>
/// Dashboard endpoints for research calibrations: list/store artifacts (via
/// <see cref="CalibrationApi"/>) and run server-owned calibration jobs that produce those
/// artifacts from real simulator backtests.
/// </summary>
public static class ResearchApi
{
    public static void MapResearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/research/jobs", (
            ResearchCalibrationService service,
            int? take) => Results.Ok(service.List(take ?? 30)));

        app.MapGet("/api/research/jobs/{id:guid}", (
            Guid id,
            ResearchCalibrationService service) =>
        {
            ResearchJobSnapshot? job = service.Get(id);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        app.MapPost("/api/research/calibrations", (
            ResearchCalibrationRequest body,
            ResearchCalibrationService service) =>
        {
            try
            {
                ResearchJobSnapshot job = service.Start(body);
                return Results.Accepted($"/api/research/jobs/{job.JobId:N}", job);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message, code = "InvalidResearchRequest" });
            }
        });
    }
}
