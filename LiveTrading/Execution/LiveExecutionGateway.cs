using System.Security.Cryptography;
using System.Text;
using Brokers.Abstractions;
using Brokers.Models;
using ExecutionManager;
using LiveTrading.Portfolio;
using LiveTrading.Registry;
using PortfolioManager.Risk;
using LiveTrading.Agents;

namespace LiveTrading.Execution;

public enum LiveExecutionStatus
{
    Rejected,
    Accepted,
    Pending,
    Filled,
    Unknown
}

public sealed record LiveExecutionResult
{
    public required LiveExecutionStatus Status { get; init; }
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public required ExecutionCertainty Certainty { get; init; }
    public string? Reason { get; init; }
}

public interface ILiveExecutionGateway
{
    Task<LiveExecutionResult> SubmitEntryAsync(
        PortfolioApprovedDecision decision,
        CancellationToken cancellationToken);

    Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken);

    Task<PositionReductionResult> ReducePositionAsync(
        LivePositionRecord position,
        decimal quantity,
        string reason,
        CancellationToken cancellationToken);

    Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        LivePositionRecord position,
        decimal proposedStopPrice,
        decimal currentExecutablePrice,
        decimal minimumPriceIncrement,
        decimal openProfitR,
        StopAmendmentReason amendmentReason,
        string reason,
        long sequence,
        CancellationToken cancellationToken);
}

/// <summary>
/// Single serialized broker writer. It delegates final broker mapping and pre-trade safeguards to
/// <see cref="IExecutionCoordinator"/>, but owns live idempotency, reservation release rules, and
/// the local order projection.
/// </summary>
public sealed class LiveExecutionGateway(
    ITradingBrokerClient broker,
    IExecutionCoordinator execution,
    ILiveOrderPositionRegistry registry,
    IPortfolioReservationBook reservations,
    TimeProvider timeProvider) : ILiveExecutionGateway
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Dictionary<string, Task<LiveExecutionResult>> _inFlight = new(StringComparer.Ordinal);

    public async Task<LiveExecutionResult> SubmitEntryAsync(
        PortfolioApprovedDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ValidateProtectedEntry(decision);
        string clientOrderId = CreateClientOrderId(decision);

        Task<LiveExecutionResult> task;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_inFlight.TryGetValue(clientOrderId, out task!))
            {
                LiveOrderRecord? existing = registry.Snapshot.Orders
                    .FirstOrDefault(order => order.ClientOrderId == clientOrderId);
                if (existing is not null)
                {
                    // A deterministic client ID is single-use. Even a previous reject/unknown
                    // must be reconciled or superseded by a new decision ID, never resubmitted
                    // blindly from a duplicate approval click or retrying API caller.
                    task = Task.FromResult(Map(existing));
                }
                else
                {
                    ValidateActiveReservation(decision);
                    registry.RegisterApproved(decision, clientOrderId, timeProvider.GetUtcNow());
                    // The caller cancellation token is honored until the order is durably registered.
                    // After that commit boundary the broker request owns its lifecycle; disconnecting
                    // an HTTP caller must not cancel an in-flight submission and leave an ambiguous retry.
                    task = SubmitCoreAsync(decision, clientOrderId, CancellationToken.None);
                }

                // Removal is owned by the operation itself, rather than by whichever API caller
                // happens to await it. A cancelled/disconnected caller therefore cannot leak an
                // in-flight entry or make a later duplicate bypass the shared task.
                task = RemoveWhenCompleteAsync(clientOrderId, task);
                _inFlight.Add(clientOrderId, task);
            }
        }
        finally
        {
            _writer.Release();
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<LiveExecutionResult> RemoveWhenCompleteAsync(
        string clientOrderId,
        Task<LiveExecutionResult> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            await _writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _inFlight.Remove(clientOrderId);
            }
            finally
            {
                _writer.Release();
            }
        }
    }

    public async Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await broker.Orders.CancelOrderAsync(brokerOrderId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PositionReductionResult> ReducePositionAsync(
        LivePositionRecord position,
        decimal quantity,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (quantity <= 0m || quantity > position.Quantity)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (string.IsNullOrWhiteSpace(position.BrokerTradeId))
            throw new InvalidOperationException("The owned position has no broker trade id and cannot be reduced safely.");
        if (broker is not IPositionReductionBrokerClient reductionBroker)
        {
            return new PositionReductionResult
            {
                Status = PositionReductionStatus.Unsupported,
                ClientRequestId = CreateReductionId(position, quantity),
                PositionId = position.PositionId,
                RequestedQuantity = quantity,
                Certainty = ExecutionCertainty.NotSent,
                Reason = "The configured broker does not support explicit reduce-only position mutations."
            };
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string requestId = CreateReductionId(position, quantity);
            PositionReductionResult result = await reductionBroker.PositionReductions.ReducePositionAsync(
                new ReduceBrokerPositionRequest
                {
                    Instrument = position.Instrument,
                    PositionId = position.BrokerTradeId,
                    PositionSide = position.Side,
                    Quantity = quantity,
                    OwnedQuantity = position.Quantity,
                    ClientRequestId = requestId,
                    RequestedAt = timeProvider.GetUtcNow(),
                    Reason = reason
                },
                cancellationToken).ConfigureAwait(false);
            registry.ApplyPositionReduction(result, timeProvider.GetUtcNow());
            return result;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        LivePositionRecord position,
        decimal proposedStopPrice,
        decimal currentExecutablePrice,
        decimal minimumPriceIncrement,
        decimal openProfitR,
        StopAmendmentReason amendmentReason,
        string reason,
        long sequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.AveragePrice is not > 0m || position.InitialStopPrice is not > 0m ||
            position.ProtectiveStopPrice is not > 0m || string.IsNullOrWhiteSpace(position.BrokerTradeId))
        {
            throw new InvalidOperationException(
                "A stop amendment requires the broker trade id, entry, initial stop, and current broker stop.");
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtectiveStopAmendmentResult result = await execution.AmendProtectiveStopAsync(
                new ProtectiveStopAmendmentCommand
                {
                    Instrument = position.Instrument,
                    StrategyId = position.StrategyId,
                    SetupId = position.SetupId ?? position.DecisionId,
                    PositionId = position.BrokerTradeId,
                    ExistingStopOrderId = position.ProtectiveStopOrderId,
                    PositionSide = position.Side,
                    PositionQuantity = position.Quantity,
                    EntryPrice = position.AveragePrice.Value,
                    InitialStopPrice = position.InitialStopPrice.Value,
                    CurrentStopPrice = position.ProtectiveStopPrice.Value,
                    ProposedStopPrice = proposedStopPrice,
                    CurrentExecutablePrice = currentExecutablePrice,
                    MinimumPriceIncrement = minimumPriceIncrement,
                    OpenProfitR = openProfitR,
                    AmendmentReason = amendmentReason,
                    Reason = reason,
                    CreatedAt = timeProvider.GetUtcNow(),
                    RequestedSequence = sequence,
                    EffectiveFromExecutionSequence = sequence + 1
                },
                broker,
                cancellationToken).ConfigureAwait(false);
            if (result.Certainty == ExecutionCertainty.Accepted)
            {
                registry.ApplyProtectiveStop(position.PositionId, result, reason, timeProvider.GetUtcNow());
            }
            return result;
        }
        finally
        {
            _writer.Release();
        }
    }

    private static string CreateReductionId(LivePositionRecord position, decimal quantity)
    {
        string canonical = string.Join("|",
            position.StrategyId,
            position.PositionId,
            position.BrokerTradeId,
            position.Quantity,
            quantity,
            position.UpdatedAt.ToUnixTimeMilliseconds());
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..32];
        return $"th-reduce-{hash}";
    }

    private async Task<LiveExecutionResult> SubmitCoreAsync(
        PortfolioApprovedDecision approved,
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        Agent.Models.AgentDecision decision = approved.Decision with
        {
            DecisionId = approved.Decision.DecisionId ?? approved.Candidate.DecisionId,
            ClientOrderId = clientOrderId,
            StrategyId = approved.Candidate.StrategyId,
            PortfolioReservationId = approved.ReservationId
        };

        try
        {
            OrderSubmission? submission = await execution.ProcessAsync(decision, broker, cancellationToken)
                .ConfigureAwait(false);
            if (submission is null)
            {
                reservations.Release(approved.ReservationId, PortfolioReleaseReason.Rejected);
                var rejected = new OrderSubmission
                {
                    ClientOrderId = clientOrderId,
                    Status = SubmissionStatus.Rejected,
                    Certainty = ExecutionCertainty.Rejected,
                    RejectionReason = "Execution coordinator produced no submission."
                };
                registry.ApplySubmission(clientOrderId, rejected, timeProvider.GetUtcNow());
                return Map(rejected);
            }

            // ExecutionCoordinator derives the same deterministic order identity from the
            // decision. Preserve its broker-returned ID/certainty, but keep the live registry
            // keyed by the live fingerprint ID registered above.
            OrderSubmission normalized = submission with { ClientOrderId = clientOrderId };
            registry.ApplySubmission(clientOrderId, normalized, timeProvider.GetUtcNow());
            if (normalized.Certainty == ExecutionCertainty.Rejected)
                reservations.Release(approved.ReservationId, PortfolioReleaseReason.Rejected);
            // Unknown deliberately keeps the reservation until authoritative reconciliation.
            return Map(normalized);
        }
        catch (Exception ex)
        {
            var unknown = new OrderSubmission
            {
                ClientOrderId = clientOrderId,
                Status = SubmissionStatus.Pending,
                Certainty = ExecutionCertainty.Unknown,
                RejectionReason = ex.Message
            };
            registry.ApplySubmission(clientOrderId, unknown, timeProvider.GetUtcNow());
            return Map(unknown);
        }
    }

    public static string CreateClientOrderId(PortfolioApprovedDecision decision)
    {
        string canonical = string.Join("|",
            decision.PolicyBundleId,
            decision.PolicyRevision,
            decision.Candidate.StrategyId,
            decision.Candidate.Instrument.Value,
            decision.Candidate.DecisionId,
            decision.Candidate.Action);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..32];
        return $"th-live-{hash}";
    }

    private static void ValidateProtectedEntry(PortfolioApprovedDecision decision)
    {
        if (decision.Decision.Action is not (Agent.Models.AgentAction.Buy or Agent.Models.AgentAction.Sell))
            throw new InvalidOperationException("Only opening Buy/Sell decisions can be submitted as live entries.");
        if (decision.Decision.StopLossPrice is not > 0m)
            throw new InvalidOperationException("Every live entry requires a broker-side initial stop.");
        if (decision.Decision.SuggestedQuantity is not > 0m ||
            decision.Decision.PortfolioAllocatedQuantity != decision.Decision.SuggestedQuantity ||
            !decision.Decision.QuantityIsPortfolioApproved)
            throw new InvalidOperationException("Every live entry requires one exact centrally approved quantity.");
        if (decision.PositionSizing.EstimatedStopLoss <= 0m || decision.PositionSizing.EstimatedMargin < 0m)
            throw new InvalidOperationException("Every live entry requires account-currency risk and margin estimates.");
        if (decision.Decision.PortfolioReservationId != decision.ReservationId)
            throw new InvalidOperationException("The execution decision does not carry its authoritative reservation ID.");
    }

    private void ValidateActiveReservation(PortfolioApprovedDecision decision)
    {
        PortfolioReservation? reservation = reservations.Snapshot.Reservations
            .SingleOrDefault(item => item.ReservationId == decision.ReservationId);
        if (reservation is null)
            throw new InvalidOperationException("The portfolio reservation is no longer active.");
        if (reservation.Quantity != decision.Decision.SuggestedQuantity ||
            reservation.RemainingQuantity != decision.Decision.SuggestedQuantity)
            throw new InvalidOperationException("The reserved quantity does not match the approved execution quantity.");
        if (reservation.PlannedStopRiskAccountCurrency != decision.PositionSizing.EstimatedStopLoss ||
            reservation.EstimatedMargin != decision.PositionSizing.EstimatedMargin)
            throw new InvalidOperationException("The reserved monetary risk or margin does not match the approved audit.");
    }

    private static LiveExecutionResult Map(OrderSubmission submission) => new()
    {
        Status = submission.Certainty == ExecutionCertainty.Unknown
            ? LiveExecutionStatus.Unknown
            : submission.Status switch
            {
                SubmissionStatus.Rejected => LiveExecutionStatus.Rejected,
                SubmissionStatus.Filled => LiveExecutionStatus.Filled,
                SubmissionStatus.Pending or SubmissionStatus.PartiallyFilled => LiveExecutionStatus.Pending,
                _ => LiveExecutionStatus.Accepted
            },
        ClientOrderId = submission.ClientOrderId,
        BrokerOrderId = submission.BrokerOrderId,
        Certainty = submission.Certainty,
        Reason = submission.RejectionReason
    };

    private static LiveExecutionResult Map(LiveOrderRecord order) => new()
    {
        Status = order.State switch
        {
            LiveOrderState.Rejected or LiveOrderState.Cancelled or LiveOrderState.Expired => LiveExecutionStatus.Rejected,
            LiveOrderState.Filled => LiveExecutionStatus.Filled,
            LiveOrderState.SubmissionUnknown => LiveExecutionStatus.Unknown,
            LiveOrderState.PortfolioApproved or LiveOrderState.SubmissionPending or LiveOrderState.PartiallyFilled =>
                LiveExecutionStatus.Pending,
            _ => LiveExecutionStatus.Accepted
        },
        ClientOrderId = order.ClientOrderId,
        BrokerOrderId = order.BrokerOrderId,
        Certainty = order.State switch
        {
            LiveOrderState.Rejected or LiveOrderState.Cancelled or LiveOrderState.Expired => ExecutionCertainty.Rejected,
            LiveOrderState.SubmissionUnknown => ExecutionCertainty.Unknown,
            LiveOrderState.PortfolioApproved or LiveOrderState.SubmissionPending => ExecutionCertainty.NotSent,
            _ => ExecutionCertainty.Accepted
        },
        Reason = order.LastReason ?? "The deterministic client order ID has already been used; no duplicate was sent."
    };
}
