namespace Simulator.Calibration;

/// <summary>
/// Immutable audit record of an explicit human approval (blueprint §16: "Approval must be an
/// immutable audit event, not merely a mutable status flip"). Recorded in addition to - not
/// instead of - flipping the artifact's own <see cref="CalibrationPromotionStatus"/>.
/// </summary>
public sealed record CalibrationPromotionEvent
{
    public required string EventId { get; init; }
    public required string ArtifactId { get; init; }
    public required string ArtifactChecksum { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public required string TargetEnvironment { get; init; }
    public required string ReviewNotes { get; init; }
    public string? ReplacesArtifactId { get; init; }
    public string? RollbackArtifactId { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EventId) ||
            string.IsNullOrWhiteSpace(ArtifactId) ||
            string.IsNullOrWhiteSpace(ArtifactChecksum) ||
            string.IsNullOrWhiteSpace(ApprovedBy) ||
            string.IsNullOrWhiteSpace(TargetEnvironment))
        {
            throw new ArgumentException("Calibration promotion event identity fields are required.");
        }
        if (ApprovedAt > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(ApprovedAt));
    }
}
