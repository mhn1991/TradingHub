using Agent.Strategies.StructuralConfluence;

namespace Simulator.Calibration;

/// <summary>
/// Compatibility identity provider for <c>LiquiditySweepReversalPlaybook</c>, mirroring
/// <see cref="IndicatorConfluenceCalibrationCompatibility"/>/<see cref="LiquidityBreakRetestCalibrationCompatibility"/>
/// exactly. <see cref="StrategyImplementationVersion"/> is hand-maintained, deliberately independent
/// of <c>LiquiditySweepReversalPlaybook.Version</c> - bump it by hand whenever the playbook's own
/// decision logic changes in a calibration-relevant way.
/// </summary>
public sealed class LiquiditySweepReversalCalibrationCompatibility :
    ICalibrationCompatibilityProvider<LiquiditySweepReversalOptions>
{
    public static LiquiditySweepReversalCalibrationCompatibility Instance { get; } = new();

    /// <summary>Matches <c>LiquiditySweepReversalPlaybook.StableId</c> so artifacts and playbook identity never drift apart.</summary>
    public string StrategyId => "structural.liquidity-sweep-reversal";
    public string StrategyImplementationVersion => "1.0";
    public string OptionsSchemaVersion => "liquidity-sweep-reversal-options-v1";

    public string ComputeDefaultConfigurationHash() =>
        IndicatorCalibrationHash.ComputeOfObject(new LiquiditySweepReversalOptions());

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
