using TradingHub.Domain.Markets;

namespace TradingHub.Application.Risk;

public sealed record RiskContext
{
    public required DateTimeOffset Now { get; init; }

    public required InstrumentDefinition Instrument { get; init; }

    public required PriceBar LatestBar { get; init; }

    public required decimal CurrentNetQuantity { get; init; }

    public required int OpenOrderCount { get; init; }
}
