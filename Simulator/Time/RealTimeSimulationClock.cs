using Simulator.Abstractions;

namespace Simulator.Time;

public sealed class RealTimeSimulationClock : ISimulationClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
