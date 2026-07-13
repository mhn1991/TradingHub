using Simulator.Abstractions;

namespace Simulator.Time;

public sealed class HistoricalSimulationClock : IAdjustableSimulationClock
{
    private bool _started;

    public DateTimeOffset UtcNow { get; private set; }

    public void AdvanceTo(DateTimeOffset time)
    {
        DateTimeOffset utc = time.ToUniversalTime();
        if (_started && utc < UtcNow)
        {
            throw new InvalidOperationException("Simulation time cannot move backwards.");
        }

        UtcNow = utc;
        _started = true;
    }
}
