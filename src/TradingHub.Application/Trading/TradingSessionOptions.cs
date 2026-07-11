using TradingHub.Domain.Markets;

namespace TradingHub.Application.Trading;

public sealed record TradingSessionOptions
{
    public required string AccountId { get; init; }

    public required string AccountCurrency { get; init; }

    public required decimal InitialBalance { get; init; }

    public required IReadOnlyDictionary<string, InstrumentDefinition> Instruments { get; init; }
}
