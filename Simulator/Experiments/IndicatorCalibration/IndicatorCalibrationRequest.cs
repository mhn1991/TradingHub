using Brokers.Models;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// The immutable, fully-specified request for one instrument-scoped calibration run (blueprint
/// §7.1). "Instrument-only" for the first release - group scope (§7.2) is explicitly deferred and
/// there is no group-request contract yet.
/// </summary>
public sealed record IndicatorCalibrationRequest
{
    public required string StrategyId { get; init; }
    public required string ManifestVersion { get; init; }

    public required InstrumentKey Instrument { get; init; }
    public required TimeframeTopology TimeframeTopology { get; init; }

    public required CalibrationTimeline Timeline { get; init; }
    public required int InternalFoldCount { get; init; }
    public required int RandomSeed { get; init; }

    public required CalibrationEvaluationBudget Budget { get; init; }
    public required string BaselineConfigurationHash { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StrategyId) || string.IsNullOrWhiteSpace(ManifestVersion))
            throw new ArgumentException("StrategyId and ManifestVersion are required.");
        if (Instrument.IsEmpty)
            throw new ArgumentException("Instrument is required.");
        ArgumentNullException.ThrowIfNull(TimeframeTopology);
        TimeframeTopology.Validate();
        ArgumentNullException.ThrowIfNull(Timeline);
        Timeline.Validate();
        if (InternalFoldCount < 3)
            throw new ArgumentOutOfRangeException(nameof(InternalFoldCount), "At least 3 internal folds are required for a meaningful stability check.");
        ArgumentNullException.ThrowIfNull(Budget);
        Budget.Validate();
        if (string.IsNullOrWhiteSpace(BaselineConfigurationHash))
            throw new ArgumentException("BaselineConfigurationHash is required.");
    }
}
