using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Persistence;
using LiveTrading.Registry;
using Microsoft.Extensions.Logging;
using PortfolioManager.Risk;
using RiskManager.Safety;

namespace LiveTrading.Reconciliation;

public interface ILiveBrokerEventProcessor
{
    string? LastTransactionId { get; }

    void InitializeCursor(string? lastTransactionId);

    Task ReplaySinceAsync(string lastTransactionId, CancellationToken cancellationToken);

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reconnecting transaction-stream consumer with durable cursor catch-up. Every normalized broker
/// event is appended before it mutates the local projection. Before startup/reconnect it replays
/// transactions newer than the durable cursor, so fills or closes that occurred while the process
/// was offline are not inferred only from a later account snapshot.
/// </summary>
public sealed class LiveBrokerEventProcessor(
    ITradingBrokerClient broker,
    ILiveOrderPositionRegistry registry,
    IPortfolioReservationBook reservations,
    ILiveTradingPersistence persistence,
    ITradingSafetyController safety,
    TimeProvider timeProvider,
    ILogger<LiveBrokerEventProcessor> logger) : ILiveBrokerEventProcessor
{
    private readonly object _cursorSync = new();
    private string? _lastTransactionId;

    public string? LastTransactionId
    {
        get
        {
            lock (_cursorSync)
            {
                return _lastTransactionId;
            }
        }
    }

    public void InitializeCursor(string? lastTransactionId)
    {
        if (string.IsNullOrWhiteSpace(lastTransactionId))
            return;
        lock (_cursorSync)
        {
            _lastTransactionId ??= lastTransactionId;
        }
    }

    public async Task ReplaySinceAsync(
        string lastTransactionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastTransactionId);
        if (broker is not ITransactionHistoryBrokerClient historyBroker)
        {
            throw new NotSupportedException(
                $"Broker {broker.Descriptor.Kind} does not expose durable transaction replay.");
        }

        BrokerEventHistoryBatch batch = await historyBroker.TransactionHistory
            .GetOrderEventsSinceAsync(lastTransactionId, cancellationToken)
            .ConfigureAwait(false);
        foreach (OrderEvent brokerEvent in batch.Events)
        {
            await PersistAndApplyAsync(brokerEvent, cancellationToken).ConfigureAwait(false);
        }
        SetCursor(batch.LastTransactionId);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            bool receivedEvent = false;
            try
            {
                string? cursor = LastTransactionId;
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    await ReplaySinceAsync(cursor, cancellationToken).ConfigureAwait(false);
                }

                await foreach (OrderEvent brokerEvent in broker.Orders
                                   .StreamOrderEventsAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    receivedEvent = true;
                    delay = TimeSpan.FromSeconds(1);
                    await PersistAndApplyAsync(brokerEvent, cancellationToken).ConfigureAwait(false);
                    SetCursor(brokerEvent.BrokerTransactionId);
                }

                if (!cancellationToken.IsCancellationRequested)
                    throw new IOException("The broker transaction stream closed unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                safety.Pause(
                    "The broker transaction stream is unavailable; new entries remain paused until reconciliation and explicit resume.",
                    timeProvider.GetUtcNow());
                logger.LogError(
                    ex,
                    "Broker transaction stream faulted{EventState}; reconnecting in {Delay}.",
                    receivedEvent ? " after receiving events" : string.Empty,
                    delay);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    private async Task PersistAndApplyAsync(
        OrderEvent brokerEvent,
        CancellationToken cancellationToken)
    {
        await persistence.AppendAsync("broker-events", brokerEvent, cancellationToken)
            .ConfigureAwait(false);
        Apply(brokerEvent);
    }

    private void SetCursor(string? transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
            return;
        lock (_cursorSync)
        {
            if (_lastTransactionId is null || CompareTransactionIds(transactionId, _lastTransactionId) > 0)
                _lastTransactionId = transactionId;
        }
    }

    private static int CompareTransactionIds(string left, string right)
    {
        if (long.TryParse(left, out long leftNumber) && long.TryParse(right, out long rightNumber))
            return leftNumber.CompareTo(rightNumber);
        return string.CompareOrdinal(left, right);
    }

    private void Apply(OrderEvent brokerEvent)
    {
        RegistryApplyResult applied = registry.Apply(brokerEvent);
        if (!applied.Applied || applied.Order is null)
        {
            if (!applied.Duplicate)
            {
                logger.LogWarning(
                    "Broker event {TransactionId}/{OrderId} was not applied: {ReasonCode}.",
                    brokerEvent.BrokerTransactionId,
                    brokerEvent.BrokerOrderId,
                    applied.ReasonCode);
            }
            return;
        }

        string reservationId = applied.Order.ReservationId;
        if ((brokerEvent.Type is OrderEventType.PartiallyFilled or OrderEventType.Filled) &&
            brokerEvent.PositionEffect is not (OrderPositionEffect.Reduce or OrderPositionEffect.Close))
        {
            PortfolioReservation? reservation = reservations.Snapshot.Reservations
                .FirstOrDefault(item => item.ReservationId == reservationId);
            if (reservation is not null)
            {
                decimal fillQuantity = Math.Abs(brokerEvent.FillQuantity ?? reservation.RemainingQuantity);
                fillQuantity = Math.Min(fillQuantity, reservation.RemainingQuantity);
                if (fillQuantity > 0m)
                {
                    reservations.CommitFill(reservationId, new PortfolioFillAllocation
                    {
                        FilledQuantity = fillQuantity,
                        FillRiskAccountCurrency = reservation.PlannedStopRiskAccountCurrency *
                            fillQuantity / reservation.Quantity,
                        FillMarginAccountCurrency = reservation.EstimatedMargin *
                            fillQuantity / reservation.Quantity
                    });
                }
            }
        }
        else if (brokerEvent.Type is OrderEventType.Rejected or OrderEventType.Cancelled or OrderEventType.Expired)
        {
            PortfolioReleaseReason reason = brokerEvent.Type switch
            {
                OrderEventType.Cancelled => PortfolioReleaseReason.Cancelled,
                OrderEventType.Expired => PortfolioReleaseReason.Expired,
                _ => PortfolioReleaseReason.Rejected
            };
            reservations.Release(reservationId, reason);
        }

        if (brokerEvent.RealizedProfitLoss is decimal realized)
            safety.RecordClosedTrade(realized, brokerEvent.Timestamp);
    }
}
