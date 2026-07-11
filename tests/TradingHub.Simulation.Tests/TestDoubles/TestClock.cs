using TradingHub.Abstractions.Time;

namespace TradingHub.Simulation.Tests.TestDoubles;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
}
