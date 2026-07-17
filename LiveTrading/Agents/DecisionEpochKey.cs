using Brokers.Models;

namespace LiveTrading.Agents;

/// <summary>
/// Lightweight per-instrument-per-epoch diagnostic/validation identity - not a restructuring of
/// <see cref="LiveDecisionEpochCoordinator"/>'s deliberate cross-instrument batching (one batch
/// still groups every instrument's candidates for one canonical close), just a convenient,
/// loggable key for the per-instrument staleness/dedup checks Phase 6 adds.
/// </summary>
public readonly record struct DecisionEpochKey(InstrumentKey Instrument, long Epoch)
{
    public override string ToString() => $"{Instrument.Value}@{Epoch}";
}
