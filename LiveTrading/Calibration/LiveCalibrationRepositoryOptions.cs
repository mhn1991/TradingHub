namespace LiveTrading.Calibration;

/// <summary>Binds to the <c>LiveCalibration</c> config section; used to construct the shared
/// <c>Simulator.Calibration.FileCalibrationArtifactRepository</c> the same calibration artifacts
/// the simulator produces/reads live under.</summary>
public sealed record LiveCalibrationRepositoryOptions
{
    public required string RootDirectory { get; init; }
}
