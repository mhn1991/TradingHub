using TradingObservability.Abstractions.Reporting;

namespace Dashboard.Live;

public static class TradingReportApi
{
    public static void MapTradingReportEndpoints(this WebApplication app)
    {
        RouteGroupBuilder reports = app.MapGroup("/api/reports");

        reports.MapGet("/simulations/{simulationId:guid}", async (
            Guid simulationId, ITradingReportQueryService service, CancellationToken cancellationToken) =>
            Report(await service.GetSimulationAsync(simulationId, cancellationToken)));
        reports.MapGet("/experiments/{experimentId:guid}", async (
            Guid experimentId, ITradingReportQueryService service, CancellationToken cancellationToken) =>
            Report(await service.GetExperimentAsync(experimentId, cancellationToken)));
        reports.MapGet("/live/sessions/{runtimeSessionId:guid}", async (
            Guid runtimeSessionId, ITradingReportQueryService service, CancellationToken cancellationToken) =>
            Report(await service.GetLiveSessionAsync(runtimeSessionId, cancellationToken)));
        reports.MapGet("/deployments/{deploymentId:guid}", async (
            Guid deploymentId, DateTimeOffset? from, DateTimeOffset? to,
            ITradingReportQueryService service, CancellationToken cancellationToken) =>
            InvalidRange(from, to)
                ? Results.BadRequest(new { error = "'from' must be earlier than or equal to 'to'." })
                : Report(await service.GetDeploymentAsync(deploymentId, from, to, cancellationToken)));
        reports.MapGet("/candidates/{candidateId:guid}", async (
            Guid candidateId, ITradingReportQueryService service, CancellationToken cancellationToken) =>
            Report(await service.GetCandidateAsync(candidateId, cancellationToken)));
        reports.MapGet("/positions/{positionId:guid}", async (
            Guid positionId, ITradingReportQueryService service, CancellationToken cancellationToken) =>
            Report(await service.GetPositionAsync(positionId, cancellationToken)));
        reports.MapGet("/agents/{agentInstanceId:guid}", async (
            Guid agentInstanceId, DateTimeOffset? from, DateTimeOffset? to,
            ITradingReportQueryService service, CancellationToken cancellationToken) =>
            InvalidRange(from, to)
                ? Results.BadRequest(new { error = "'from' must be earlier than or equal to 'to'." })
                : Results.Ok(await service.GetAgentAsync(agentInstanceId, from, to, cancellationToken)));
        reports.MapGet("/rejection-reasons", async (
            DateTimeOffset? from, DateTimeOffset? to, Guid? deploymentId, Guid? simulationId,
            Guid? agentInstanceId, int? limit, ITradingReportQueryService service,
            CancellationToken cancellationToken) =>
        {
            if (InvalidRange(from, to))
                return Results.BadRequest(new { error = "'from' must be earlier than or equal to 'to'." });
            var range = new ReportQueryRange(from, to, deploymentId, simulationId, agentInstanceId, limit ?? 50);
            return Results.Ok(await service.GetRejectionReasonsAsync(range, cancellationToken));
        });
    }

    private static bool InvalidRange(DateTimeOffset? from, DateTimeOffset? to) =>
        from.HasValue && to.HasValue && from.Value > to.Value;

    private static IResult Report<T>(T? report) where T : class => report is null ? Results.NotFound() : Results.Ok(report);
}
