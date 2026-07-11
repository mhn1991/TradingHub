using TradingHub.Domain.Trading;

namespace TradingHub.Abstractions.Brokers;

public interface IBrokerGateway
{
    string BrokerId { get; }

    string ProviderName { get; }

    TradingEnvironment Environment { get; }

    Task<OrderSubmissionResult> SubmitOrderAsync(
        OrderRequest request,
        CancellationToken cancellationToken = default);

    Task<OrderCancellationResult> CancelOrderAsync(
        OrderCancellationRequest request,
        CancellationToken cancellationToken = default);

    Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}
