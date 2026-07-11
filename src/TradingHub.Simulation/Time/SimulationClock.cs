using TradingHub.Abstractions.Time;

namespace TradingHub.Simulation.Time;

public sealed class SimulationClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.MinValue;

    public void AdvanceTo(DateTimeOffset timestamp)
    {
        if (timestamp < UtcNow)
        {
            throw new InvalidOperationException("Simulation time cannot move backwards.");
        }

        UtcNow = timestamp;
    }
}
