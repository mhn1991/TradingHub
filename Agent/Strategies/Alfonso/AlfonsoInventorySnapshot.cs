using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;

namespace Agent.Strategies.Alfonso;

/// <summary>The live zone population of one timeframe and one side, at one instant.</summary>
public sealed record AlfonsoInventorySnapshot
{
    public required DateTimeOffset At { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required SequenceRole Role { get; init; }
    public required ImbalanceKind Kind { get; init; }

    /// <summary>Zones the engine is still tracking on this side.</summary>
    public required int LiveZones { get; init; }

    /// <summary>Of those, how many sit within the distance where fills actually occur.</summary>
    public required int Reachable { get; init; }

    public decimal? MedianDistanceAtr { get; init; }
    public decimal? NearestDistanceAtr { get; init; }
    public required decimal Price { get; init; }
    public decimal? Atr { get; init; }
}
