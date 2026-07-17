using Brokers.Models;

namespace LiveTrading.Actors;

public sealed record ComponentReadiness
{
    public required string Component { get; init; }
    public required BarInterval Interval { get; init; }
    public required int RequiredSamples { get; init; }
    public required int AvailableSamples { get; init; }
    public required string Status { get; init; }
}

public sealed record LiveAnalysisReadiness
{
    public required InstrumentKey Instrument { get; init; }
    public required bool Ready { get; init; }
    public required IReadOnlyList<ComponentReadiness> Components { get; init; }
}
