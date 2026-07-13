using Agent.Models;
using Brokers.Models;

namespace TradingJournal;

public enum TradeJournalEventType
{
    SignalEvaluated,
    SignalRejected,
    DataQualityRejected,
    OrderSubmitted,
    OrderAccepted,
    OrderRejected,
    PositionMismatch,
    SafetyStateChanged,
    ClosedTradeRecorded,
    Error
}

public sealed record TradeJournalEntry
{
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TradeJournalEventType Type { get; init; }
    public InstrumentKey? Instrument { get; init; }
    public AgentAction? Action { get; init; }
    public string? DecisionId { get; init; }
    public string? ClientOrderId { get; init; }
    public decimal? Confidence { get; init; }
    public decimal? Value { get; init; }
    public required string Message { get; init; }
}

public interface ITradeJournal
{
    TradeJournalEntry Append(TradeJournalEntry entry);
    IReadOnlyList<TradeJournalEntry> Snapshot();
}

public sealed class NullTradeJournal : ITradeJournal
{
    public static NullTradeJournal Instance { get; } = new();

    private NullTradeJournal()
    {
    }

    public TradeJournalEntry Append(TradeJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry;
    }

    public IReadOnlyList<TradeJournalEntry> Snapshot() => [];
}

public sealed class InMemoryTradeJournal : ITradeJournal
{
    private readonly object _sync = new();
    private readonly Queue<TradeJournalEntry> _entries;
    private readonly int _capacity;
    private long _sequence;

    public InMemoryTradeJournal(int capacity = 20_000)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _entries = new Queue<TradeJournalEntry>(Math.Min(capacity, 1024));
    }

    public TradeJournalEntry Append(TradeJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            TradeJournalEntry sequenced = entry with { Sequence = ++_sequence };
            if (_entries.Count == _capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(sequenced);
            return sequenced;
        }
    }

    public IReadOnlyList<TradeJournalEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }
}
