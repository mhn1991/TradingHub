using Brokers.Models;

namespace Brokers.Abstractions;

public interface IBrokerClient : IAsyncDisposable
{
    BrokerDescriptor Descriptor { get; }

    BrokerCapabilities Capabilities => BrokerCapabilities.All;

    IMarketDataClient MarketData { get; }

    IAccountClient Accounts { get; }

    IOrderClient Orders { get; }

    IPositionClient Positions { get; }

    ICostClient Costs { get; }
}

public sealed record BrokerCapabilities(
    bool SupportsMarketData,
    bool SupportsAccounts,
    bool SupportsOrders,
    bool SupportsPositions,
    bool SupportsCosts)
{
    public static BrokerCapabilities All { get; } = new(true, true, true, true, true);
}

public interface IMarketDataClient
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default);
}

public interface IAccountClient
{
    Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default);
}

public interface IOrderClient
{
    Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default);
}

public interface IPositionClient
{
    Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default);
}

public interface ICostClient
{
    Task<CommissionSchedule> GetCommissionAsync(
        InstrumentKey instrument,
        CancellationToken cancellationToken = default);
}

public interface IInstrumentMetadataBrokerClient : IBrokerClient
{
    IInstrumentMetadataClient InstrumentMetadata { get; }
}

public interface IInstrumentMetadataClient
{
    Task<IReadOnlyList<InstrumentTradingMetadata>> GetInstrumentMetadataAsync(
        CancellationToken cancellationToken = default);
}
