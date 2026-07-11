namespace TradingHub.Domain.Markets;

public sealed record InstrumentDefinition
{
    public required string Id { get; init; }

    public required string DisplaySymbol { get; init; }

    public required string BaseAsset { get; init; }

    public required string QuoteAsset { get; init; }

    public required AssetClass AssetClass { get; init; }

    public required decimal PriceIncrement { get; init; }

    public required decimal QuantityIncrement { get; init; }

    public required decimal MinimumQuantity { get; init; }

    public required decimal MaximumQuantity { get; init; }

    public decimal ContractSize { get; init; } = 1m;

    public bool IsEnabled { get; init; } = true;

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(DisplaySymbol))
        {
            throw new InvalidOperationException("Instrument identifiers are required.");
        }

        if (PriceIncrement <= 0m || QuantityIncrement <= 0m || ContractSize <= 0m)
        {
            throw new InvalidOperationException("Instrument increments and contract size must be positive.");
        }

        if (MinimumQuantity <= 0m || MaximumQuantity < MinimumQuantity)
        {
            throw new InvalidOperationException("Instrument quantity limits are invalid.");
        }
    }

    public bool IsQuantityValid(decimal quantity)
    {
        return quantity >= MinimumQuantity
            && quantity <= MaximumQuantity
            && quantity % QuantityIncrement == 0m;
    }

    public bool IsPriceValid(decimal price)
    {
        return price > 0m && price % PriceIncrement == 0m;
    }
}
