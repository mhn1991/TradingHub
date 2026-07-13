using TradingJournal;
using RiskManager.Safety;
using Brokers.Abstractions;
using Brokers.Reconciliation;

namespace ExecutionManager.Reconciliation;

/// <summary>
/// Converts broker/local position mismatches into an auditable safety trip.
/// The caller supplies its durable expected-position snapshot.
/// </summary>
public sealed class PositionReconciliationGuard
{
    private readonly PositionReconciler _reconciler;
    private readonly ITradingSafetyController _safety;
    private readonly ITradeJournal _journal;

    public PositionReconciliationGuard(
        ITradingSafetyController safety,
        ITradeJournal? journal = null,
        PositionReconciler? reconciler = null)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _journal = journal ?? NullTradeJournal.Instance;
        _reconciler = reconciler ?? new PositionReconciler();
    }

    public async Task<PositionReconciliationResult> CheckAsync(
        IReadOnlyList<ExpectedPosition> expected,
        ITradingBrokerClient broker,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        PositionReconciliationResult result = await _reconciler
            .ReconcileAsync(expected, broker, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsConsistent)
        {
            return result;
        }

        foreach (PositionMismatch mismatch in result.Mismatches)
        {
            _journal.Append(new TradeJournalEntry
            {
                Sequence = 0,
                Timestamp = timestamp,
                Type = TradeJournalEventType.PositionMismatch,
                Instrument = mismatch.Instrument,
                Message = mismatch.Message
            });
        }

        string message = $"Broker position reconciliation found {result.Mismatches.Count} mismatch(es).";
        TradingSafetySnapshot snapshot = _safety.Trip(
            SafetyTripReason.BrokerStateMismatch,
            message,
            timestamp);
        _journal.Append(new TradeJournalEntry
        {
            Sequence = 0,
            Timestamp = timestamp,
            Type = TradeJournalEventType.SafetyStateChanged,
            Message = $"Trading safety changed to {snapshot.State}: {snapshot.Reason}."
        });
        return result;
    }
}
