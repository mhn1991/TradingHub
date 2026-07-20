namespace Simulator.Calibration;

/// <summary>
/// The complete identity a calibration artifact is bound to, and the identity a live/simulation
/// consumer must reproduce exactly before an overlay is allowed to apply (blueprint §2.1/§15.2).
/// A mismatch on any field means the artifact was produced against a different strategy
/// implementation, options shape, or timeframe setup than the one about to consume it - the
/// overlay must be rejected atomically, never partially applied.
/// </summary>
public sealed record CalibrationCompatibilityIdentity
{
    public required string StrategyId { get; init; }
    public required string StrategyImplementationVersion { get; init; }
    public required string OptionsSchemaVersion { get; init; }
    public required string DefaultConfigurationHash { get; init; }
    public required string TimeframeTopologyHash { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StrategyId) ||
            string.IsNullOrWhiteSpace(StrategyImplementationVersion) ||
            string.IsNullOrWhiteSpace(OptionsSchemaVersion) ||
            string.IsNullOrWhiteSpace(DefaultConfigurationHash) ||
            string.IsNullOrWhiteSpace(TimeframeTopologyHash))
        {
            throw new ArgumentException("Calibration compatibility identity fields are required.");
        }
    }

    /// <summary>Exact match on every field - the only comparison an overlay-consumption check may use.</summary>
    public bool Matches(CalibrationCompatibilityIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(StrategyId, other.StrategyId, StringComparison.Ordinal) &&
            string.Equals(StrategyImplementationVersion, other.StrategyImplementationVersion, StringComparison.Ordinal) &&
            string.Equals(OptionsSchemaVersion, other.OptionsSchemaVersion, StringComparison.Ordinal) &&
            string.Equals(DefaultConfigurationHash, other.DefaultConfigurationHash, StringComparison.Ordinal) &&
            string.Equals(TimeframeTopologyHash, other.TimeframeTopologyHash, StringComparison.Ordinal);
    }
}

/// <summary>
/// Per-strategy provider of <see cref="CalibrationCompatibilityIdentity"/>. One implementation
/// per calibration-enabled strategy; registered explicitly, never discovered via reflection
/// (blueprint §1.4/§5.3 - manifests and everything they depend on are explicit, versioned code).
/// </summary>
public interface ICalibrationCompatibilityProvider<in TOptions>
{
    string StrategyId { get; }
    string StrategyImplementationVersion { get; }
    string OptionsSchemaVersion { get; }

    /// <summary>Canonical hash of the options record's compiled-in default values (a fresh <c>new TOptions()</c>).</summary>
    string ComputeDefaultConfigurationHash();

    CalibrationCompatibilityIdentity Describe(TimeframeTopology topology);
}
