using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Registry;
using PortfolioManager.Risk;

namespace LiveTrading.Reconciliation;

public enum ReconciliationTrigger
{
    Startup,
    Periodic,
    PricingReconnect,
    TransactionReconnect,
    UnknownSubmission,
    StopAmendment,
    PartialClose,
    Manual,
    Resume
}

public enum ReconciliationSeverity
{
    Information,
    Warning,
    Critical
}

public sealed record ReconciliationDifference
{
    public required string Code { get; init; }
    public required ReconciliationSeverity Severity { get; init; }
    public required string Explanation { get; init; }
    public string? ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? PositionId { get; init; }
}

public sealed record BrokerReconciliationReport
{
    public required string ReconciliationId { get; init; }
    public required ReconciliationTrigger Trigger { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required IReadOnlyList<ReconciliationDifference> Differences { get; init; }
    public required bool CanOpenNewEntries { get; init; }
    public required bool RequiresOperatorReview { get; init; }
}

public interface ILiveBrokerReconciler
{
    Task<BrokerReconciliationReport> ReconcileAsync(
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken);
}

/// <summary>
/// Compares transaction-driven local state with authoritative REST snapshots. Unknown or unowned
/// broker state pauses entries; explainable broker status changes repair the local projection.
/// </summary>
public sealed class LiveBrokerReconciler(
    IBrokerClient broker,
    ILiveOrderPositionRegistry registry,
    IPortfolioReservationBook reservations,
    TimeProvider timeProvider) : ILiveBrokerReconciler
{
    private readonly SemaphoreSlim _serial = new(1, 1);

    public async Task<BrokerReconciliationReport> ReconcileAsync(
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset started = timeProvider.GetUtcNow();
        try
        {
            Task<IReadOnlyList<BrokerOrder>> ordersTask = broker.Orders.GetOpenOrdersAsync(
                cancellationToken: cancellationToken);
            Task<IReadOnlyList<BrokerPosition>> positionsTask = broker.Positions.GetOpenPositionsAsync(cancellationToken);
            await Task.WhenAll(ordersTask, positionsTask).ConfigureAwait(false);
            IReadOnlyList<BrokerOrder> brokerOrders = await ordersTask.ConfigureAwait(false);
            IReadOnlyList<BrokerPosition> brokerPositions = await positionsTask.ConfigureAwait(false);
            LiveRegistrySnapshot local = registry.Snapshot;
            var differences = new List<ReconciliationDifference>();

            foreach (BrokerOrder brokerOrder in brokerOrders)
            {
                LiveOrderRecord? localOrder = !string.IsNullOrWhiteSpace(brokerOrder.ClientOrderId)
                    ? local.Orders.FirstOrDefault(o => o.ClientOrderId == brokerOrder.ClientOrderId)
                    : local.Orders.FirstOrDefault(o => o.BrokerOrderId == brokerOrder.BrokerOrderId);
                if (localOrder is null)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "BrokerOrderMissingLocally",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = "An open broker order is not owned by the local registry.",
                        ClientOrderId = brokerOrder.ClientOrderId,
                        BrokerOrderId = brokerOrder.BrokerOrderId
                    });
                }
            }

            foreach (LiveOrderRecord localOrder in local.Orders.Where(o =>
                         o.State is LiveOrderState.Accepted or LiveOrderState.PartiallyFilled or
                         LiveOrderState.SubmissionPending or LiveOrderState.SubmissionUnknown))
            {
                bool found = brokerOrders.Any(o =>
                    (!string.IsNullOrWhiteSpace(localOrder.BrokerOrderId) && o.BrokerOrderId == localOrder.BrokerOrderId) ||
                    (!string.IsNullOrWhiteSpace(o.ClientOrderId) && o.ClientOrderId == localOrder.ClientOrderId));
                if (!found && localOrder.State == LiveOrderState.SubmissionUnknown)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "UnknownSubmissionNotFound",
                        Severity = ReconciliationSeverity.Warning,
                        Explanation = "An uncertain submission is absent from the current open-order snapshot and requires transaction/account-history proof before release.",
                        ClientOrderId = localOrder.ClientOrderId,
                        BrokerOrderId = localOrder.BrokerOrderId
                    });
                }
            }

            foreach (BrokerPosition position in brokerPositions)
            {
                LivePositionRecord[] exactMatches = local.Positions.Where(p =>
                    p.PositionId == position.PositionId || p.BrokerTradeId == position.PositionId).ToArray();
                LiveOrderRecord? ownershipOrder = !string.IsNullOrWhiteSpace(position.ClientTradeId)
                    ? local.Orders.FirstOrDefault(order => order.ClientOrderId == position.ClientTradeId)
                    : null;
                LivePositionRecord[] ownershipMatches = exactMatches.Length > 0
                    ? exactMatches
                    : local.Positions.Where(p =>
                        p.Instrument == position.Instrument && p.Side == position.Side).ToArray();
                if (ownershipMatches.Length > 1)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "AmbiguousPositionOwnership",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = "More than one local strategy lot maps to the same broker trade; automatic ownership assignment is forbidden.",
                        PositionId = position.PositionId
                    });
                    continue;
                }
                LivePositionRecord? localPosition = ownershipMatches.SingleOrDefault();
                if (localPosition is null && ownershipOrder is null)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "UnownedBrokerPosition",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = "A broker trade is not owned by the local registry or a deterministic local client-trade ID; automatic adoption is forbidden.",
                        PositionId = position.PositionId
                    });
                    continue;
                }

                if (localPosition is not null && localPosition.Quantity != position.Quantity)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "PositionQuantityMismatch",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = $"Local quantity {localPosition.Quantity} differs from broker quantity {position.Quantity}.",
                        PositionId = position.PositionId
                    });
                }
                if (position.ProtectiveStopPrice is not > 0m ||
                    string.IsNullOrWhiteSpace(position.ProtectiveStopOrderId))
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "MissingProtectiveStop",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = "An owned broker trade has no confirmed dependent stop-loss order.",
                        PositionId = position.PositionId
                    });
                }
                else if (localPosition?.ProtectiveStopPrice is > 0m &&
                    localPosition.ProtectiveStopPrice != position.ProtectiveStopPrice)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "ProtectiveStopMismatch",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = $"Local stop {localPosition.ProtectiveStopPrice} differs from broker stop {position.ProtectiveStopPrice}.",
                        PositionId = position.PositionId
                    });
                }

                if (ownershipOrder is not null)
                    reservations.Release(ownershipOrder.ReservationId, PortfolioReleaseReason.FillReconciled);
            }

            foreach (LivePositionRecord localPosition in local.Positions)
            {
                bool found = brokerPositions.Any(position =>
                    position.PositionId == localPosition.PositionId ||
                    position.PositionId == localPosition.BrokerTradeId);
                if (!found)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "LocalPositionMissingAtBroker",
                        Severity = ReconciliationSeverity.Critical,
                        Explanation = "A locally owned position is absent from the authoritative broker open-trade snapshot.",
                        PositionId = localPosition.PositionId
                    });
                }
            }

            foreach (PortfolioReservation reservation in reservations.Snapshot.Reservations)
            {
                bool hasOrder = local.Orders.Any(o => o.ReservationId == reservation.ReservationId &&
                    o.State is not (LiveOrderState.Rejected or LiveOrderState.Cancelled or LiveOrderState.Expired));
                if (!hasOrder)
                {
                    differences.Add(new ReconciliationDifference
                    {
                        Code = "ReservationWithoutOrder",
                        Severity = ReconciliationSeverity.Warning,
                        Explanation = $"Reservation '{reservation.ReservationId}' has no active local order."
                    });
                }
            }

            registry.ApplyReconciliation(brokerOrders, brokerPositions, timeProvider.GetUtcNow());
            bool critical = differences.Any(d => d.Severity == ReconciliationSeverity.Critical);
            bool uncertain = differences.Any(d => d.Code is "UnknownSubmissionNotFound" or "ReservationWithoutOrder");
            return new BrokerReconciliationReport
            {
                ReconciliationId = Guid.NewGuid().ToString("N"),
                Trigger = trigger,
                StartedAt = started,
                CompletedAt = timeProvider.GetUtcNow(),
                Differences = differences,
                CanOpenNewEntries = !critical && !uncertain,
                RequiresOperatorReview = critical || uncertain
            };
        }
        finally
        {
            _serial.Release();
        }
    }
}
