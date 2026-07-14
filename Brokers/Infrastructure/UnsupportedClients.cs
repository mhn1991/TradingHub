using Brokers.Abstractions;
using Brokers.Exceptions;
using Brokers.Models;

namespace Brokers.Infrastructure;

internal sealed class UnsupportedPositionClient(BrokerKind broker) : IPositionClient
{
    public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default) =>
        throw new BrokerFeatureNotSupportedException(broker, "Open positions");
}

internal sealed class UnsupportedCostClient(BrokerKind broker) : ICostClient
{
    public Task<CommissionSchedule> GetCommissionAsync(
        InstrumentKey instrument,
        CancellationToken cancellationToken = default) =>
        throw new BrokerFeatureNotSupportedException(broker, "Direct commission schedule");
}

internal sealed class UnsupportedAccountClient(BrokerKind broker) : IAccountClient
{
    public Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default) =>
        throw new BrokerFeatureNotSupportedException(broker, "Authenticated account access");
}

internal sealed class UnsupportedOrderClient(BrokerKind broker) : IOrderClient
{
    public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default) =>
        throw new BrokerFeatureNotSupportedException(broker, "Authenticated order access");
}
