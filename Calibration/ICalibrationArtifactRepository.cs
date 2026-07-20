using RiskManager.Calibration;
using TradeManager;

namespace Simulator.Calibration;

/// <summary>
/// Safe, server-owned storage for calibration artifacts (audit §16.3). Clients reference an
/// artifact only by <see cref="Guid"/> - never a server-local path.
/// </summary>
public interface ICalibrationArtifactRepository
{
    Task<CalibrationArtifactMetadata> StoreSetupAsync(
        SetupCalibrationArtifact artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default);

    Task<CalibrationArtifactMetadata> StoreManagementAsync(
        TradeManagementCalibration artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default);

    Task<SetupCalibrationArtifact?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TradeManagementCalibration?> GetManagementAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CalibrationArtifactMetadata> StoreMetaModelAsync(
        MetaModelArtifact artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default);

    Task<CalibrationArtifactMetadata> StoreIndicatorParametersAsync(
        IndicatorCalibrationArtifact artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default);

    Task<IndicatorCalibrationArtifact?> GetIndicatorParametersAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Updates only the promotion status of an already-stored artifact's metadata (envelope content stays unchanged).</summary>
    Task<CalibrationArtifactMetadata?> UpdatePromotionStatusAsync(
        Guid id, CalibrationPromotionStatus status, CancellationToken cancellationToken = default);

    Task<MetaModelArtifact?> GetMetaModelAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CalibrationArtifactMetadata?> GetMetadataAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalibrationArtifactMetadata>> ListAsync(
        CalibrationArtifactType? filter = null, int take = 50, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
