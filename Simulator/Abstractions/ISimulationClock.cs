namespace Simulator.Abstractions;

public interface ISimulationClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IAdjustableSimulationClock : ISimulationClock
{
    void AdvanceTo(DateTimeOffset time);
}
