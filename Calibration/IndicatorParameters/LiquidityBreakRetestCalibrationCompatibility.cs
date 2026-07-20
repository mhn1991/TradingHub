using Agent.Strategies.StructuralConfluence;

namespace Simulator.Calibration;

/// <summary>
/// Compatibility identity provider for <c>LiquidityBreakRetestPlaybook</c> (blueprint §19 Phase 8,
/// mirroring <see cref="IndicatorConfluenceCalibrationCompatibility"/> exactly for the second
/// calibration-enabled strategy). <see cref="StrategyImplementationVersion"/> is hand-maintained,
/// deliberately independent of <c>LiquidityBreakRetestPlaybook.Version</c> - bump it by hand
/// whenever the playbook's own decision logic changes in a calibration-relevant way.
/// </summary>
public sealed class LiquidityBreakRetestCalibrationCompatibility :
    ICalibrationCompatibilityProvider<LiquidityBreakRetestOptions>
{
    public static LiquidityBreakRetestCalibrationCompatibility Instance { get; } = new();

    /// <summary>Matches <c>LiquidityBreakRetestPlaybook.StableId</c> so artifacts and playbook identity never drift apart.</summary>
    public string StrategyId => "structural.liquidity-break-retest";
    public string StrategyImplementationVersion => "1.0";
    public string OptionsSchemaVersion => "liquidity-break-retest-options-v1";

    public string ComputeDefaultConfigurationHash() =>
        IndicatorCalibrationHash.ComputeOfObject(new LiquidityBreakRetestOptions());

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
