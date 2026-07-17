using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;

namespace Dashboard.Live;

/// <summary>
/// Safe, server-owned calibration-artifact storage (audit §16.3). Clients upload an artifact
/// payload directly and receive a server-minted <see cref="Guid"/> back; simulation requests
/// then reference that ID only (see <see cref="CreateSimulationRequest.SetupCalibrationArtifactId"/>/
/// <see cref="CreateSimulationRequest.ManagementCalibrationArtifactId"/>) - never a raw
/// artifact object or server-local path, mirroring the existing ImportedDatasetId precedent.
/// </summary>
public static class CalibrationApi
{
    public static void MapCalibrationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/calibrations/setup", async (
            SetupCalibrationArtifact body,
            string? description,
            ICalibrationArtifactRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                CalibrationArtifactMetadata metadata = await repository
                    .StoreSetupAsync(body, description, cancellationToken: cancellationToken);
                return Results.Created($"/api/calibrations/{metadata.Id:N}", metadata);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message, code = "InvalidSetupCalibrationArtifact" });
            }
        });

        app.MapPost("/api/calibrations/management", async (
            TradeManagementCalibration body,
            string? description,
            ICalibrationArtifactRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                CalibrationArtifactMetadata metadata = await repository
                    .StoreManagementAsync(body, description, cancellationToken: cancellationToken);
                return Results.Created($"/api/calibrations/{metadata.Id:N}", metadata);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message, code = "InvalidManagementCalibrationArtifact" });
            }
        });

        app.MapPost("/api/calibrations/metamodel", async (
            MetaModelArtifact body,
            string? description,
            ICalibrationArtifactRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                CalibrationArtifactMetadata metadata = await repository
                    .StoreMetaModelAsync(body, description, cancellationToken: cancellationToken);
                return Results.Created($"/api/calibrations/{metadata.Id:N}", metadata);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message, code = "InvalidMetaModelArtifact" });
            }
        });

        app.MapGet("/api/calibrations", async (
            string? type,
            int? take,
            ICalibrationArtifactRepository repository,
            CancellationToken cancellationToken) =>
        {
            CalibrationArtifactType? filter = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                if (!Enum.TryParse(type, ignoreCase: true, out CalibrationArtifactType parsed))
                    return Results.BadRequest(new { error = $"Unknown calibration artifact type '{type}'." });
                filter = parsed;
            }

            IReadOnlyList<CalibrationArtifactMetadata> items = await repository
                .ListAsync(filter, take ?? 50, cancellationToken);
            return Results.Ok(items);
        });

        app.MapGet("/api/calibrations/{id:guid}", async (
            Guid id,
            ICalibrationArtifactRepository repository,
            CancellationToken cancellationToken) =>
        {
            CalibrationArtifactMetadata? metadata = await repository.GetMetadataAsync(id, cancellationToken);
            return metadata is null ? Results.NotFound() : Results.Ok(metadata);
        });

        app.MapDelete("/api/calibrations/{id:guid}", async (
            Guid id,
            ICalibrationArtifactRepository repository,
            CancellationToken cancellationToken) =>
        {
            bool deleted = await repository.DeleteAsync(id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
