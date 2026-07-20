using Agent.Strategies.StructuralConfluence;

namespace Simulator.Calibration;

/// <summary>
/// Compatibility identity provider for <c>IndicatorConfluencePlaybook</c> (blueprint §19 Phase 1,
/// §20 "Correctness"/"Safety"). <see cref="StrategyImplementationVersion"/> must be bumped by hand
/// whenever the playbook's own decision logic changes in a way that could change what a
/// previously-calibrated artifact means - it deliberately does not track
/// <c>IndicatorConfluencePlaybook.Version</c> automatically, since that string exists for a
/// different purpose (evidence/replay identity) and bumping it is not guaranteed to happen for
/// every calibration-relevant logic change.
/// </summary>
public sealed class IndicatorConfluenceCalibrationCompatibility :
    ICalibrationCompatibilityProvider<IndicatorConfluenceOptions>
{
    public static IndicatorConfluenceCalibrationCompatibility Instance { get; } = new();

    /// <summary>Matches <c>IndicatorConfluencePlaybook.StableId</c> so artifacts and playbook identity never drift apart.</summary>
    public string StrategyId => "structural.indicator-confluence";
    public string StrategyImplementationVersion => "1.0";
    public string OptionsSchemaVersion => "indicator-confluence-options-v1";

    public string ComputeDefaultConfigurationHash() =>
        IndicatorCalibrationHash.ComputeOfObject(new IndicatorConfluenceOptions());

    public CalibrationCompatibilityIdentity Describe(TimeframeTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return new CalibrationCompatibilityIdentity
        {
            StrategyId = StrategyId,
            StrategyImplementationVersion = StrategyImplementationVersion,
            OptionsSchemaVersion = OptionsSchemaVersion,
            DefaultConfigurationHash = ComputeDefaultConfigurationHash(),
            TimeframeTopologyHash = topology.ComputeHash()
        };
    }
}
