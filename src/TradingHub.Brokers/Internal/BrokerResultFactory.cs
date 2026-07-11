using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Internal;

internal static class BrokerResultFactory
{
    public static OrderSubmissionResult RejectedOrder(string reason, DateTimeOffset occurredAt)
    {
        return new OrderSubmissionResult
        {
            Outcome = SubmissionOutcome.Rejected,
            Status = OrderStatus.Rejected,
            OccurredAt = occurredAt,
            Reason = reason
        };
    }

    public static OrderSubmissionResult UnknownOrder(string reason, DateTimeOffset occurredAt)
    {
        return new OrderSubmissionResult
        {
            Outcome = SubmissionOutcome.Unknown,
            Status = OrderStatus.Unknown,
            OccurredAt = occurredAt,
            Reason = reason
        };
    }

    public static OrderCancellationResult RejectedCancellation(string reason, DateTimeOffset occurredAt)
    {
        return new OrderCancellationResult
        {
            Outcome = SubmissionOutcome.Rejected,
            Status = OrderStatus.Rejected,
            OccurredAt = occurredAt,
            Reason = reason
        };
    }

    public static OrderCancellationResult UnknownCancellation(string reason, DateTimeOffset occurredAt)
    {
        return new OrderCancellationResult
        {
            Outcome = SubmissionOutcome.Unknown,
            Status = OrderStatus.Unknown,
            OccurredAt = occurredAt,
            Reason = reason
        };
    }
}
