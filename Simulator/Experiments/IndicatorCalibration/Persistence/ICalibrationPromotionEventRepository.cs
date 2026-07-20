using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration.Persistence;

/// <summary>
/// Durable storage for <see cref="CalibrationPromotionEvent"/> audit records (blueprint §16, §19
/// Phase 7: "approval audit event persistence"). Append-only - an event is never updated or
/// deleted once saved, matching its own nature as an immutable audit trail.
/// </summary>
public interface ICalibrationPromotionEventRepository
{
    Task SaveAsync(CalibrationPromotionEvent promotionEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalibrationPromotionEvent>> ListForArtifactAsync(
        string artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalibrationPromotionEvent>> ListAsync(
        int take = 50, CancellationToken cancellationToken = default);
}
