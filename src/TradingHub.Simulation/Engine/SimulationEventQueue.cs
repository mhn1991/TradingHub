using TradingHub.Simulation.Time;

namespace TradingHub.Simulation.Engine;

public sealed class SimulationEventQueue
{
    private readonly SimulationClock _clock;
    private readonly PriorityQueue<ScheduledEvent, EventPriority> _queue = new();
    private long _sequence;

    public SimulationEventQueue(SimulationClock clock)
    {
        _clock = clock;
    }

    public int Count => _queue.Count;

    public void Schedule(
        DateTimeOffset timestamp,
        int phase,
        string name,
        Func<CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        var sequence = Interlocked.Increment(ref _sequence);
        var scheduledEvent = new ScheduledEvent(name, handler);
        _queue.Enqueue(scheduledEvent, new EventPriority(timestamp, phase, sequence));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (_queue.TryDequeue(out var scheduledEvent, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _clock.AdvanceTo(priority.Timestamp);
            await scheduledEvent.Handler(cancellationToken);
        }
    }

    private sealed record ScheduledEvent(
        string Name,
        Func<CancellationToken, Task> Handler);

    private readonly record struct EventPriority(
        DateTimeOffset Timestamp,
        int Phase,
        long Sequence) : IComparable<EventPriority>
    {
        public int CompareTo(EventPriority other)
        {
            var timeComparison = Timestamp.CompareTo(other.Timestamp);
            if (timeComparison != 0)
            {
                return timeComparison;
            }

            var phaseComparison = Phase.CompareTo(other.Phase);
            return phaseComparison != 0 ? phaseComparison : Sequence.CompareTo(other.Sequence);
        }
    }
}
