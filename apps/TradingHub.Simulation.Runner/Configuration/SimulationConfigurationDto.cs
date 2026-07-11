using TradingHub.Domain.Markets;

namespace TradingHub.Simulation.Runner.Configuration;

internal sealed record SimulationConfigurationDto
{
    public required string RunId { get; init; }

    public required DataConfigurationDto Data { get; init; }

    public required AccountConfigurationDto Account { get; init; }

    public required InstrumentConfigurationDto Instrument { get; init; }

    public required StrategyConfigurationDto Strategy { get; init; }

    public required RiskConfigurationDto Risk { get; init; }

    public required ExecutionConfigurationDto Execution { get; init; }

    public required OutputConfigurationDto Output { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(RunId)
            || RunId.Contains("..", StringComparison.Ordinal)
            || RunId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("The simulation run ID is invalid.");
        }

        Data.EnsureValid();
        Account.EnsureValid();
        Instrument.EnsureValid();
        Strategy.EnsureValid();
        Risk.EnsureValid();
        Execution.EnsureValid();
        Output.EnsureValid();
    }
}

internal sealed record DataConfigurationDto
{
    public required string File { get; init; }

    public required int PeriodMinutes { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(File) || PeriodMinutes <= 0)
        {
            throw new InvalidOperationException("Historical data configuration is invalid.");
        }
    }
}

internal sealed record AccountConfigurationDto
{
    public required string Id { get; init; }

    public required string Currency { get; init; }

    public required decimal InitialBalance { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Currency) || InitialBalance <= 0m)
        {
            throw new InvalidOperationException("Simulation account configuration is invalid.");
        }
    }
}

internal sealed record InstrumentConfigurationDto
{
    public required string Id { get; init; }

    public required string DisplaySymbol { get; init; }

    public required AssetClass AssetClass { get; init; }

    public required AssetPairConfigurationDto Assets { get; init; }

    public required InstrumentRulesConfigurationDto Rules { get; init; }

    public void EnsureValid()
    {
        ToDomain().EnsureValid();
    }

    public InstrumentDefinition ToDomain()
    {
        return new InstrumentDefinition
        {
            Id = Id,
            DisplaySymbol = DisplaySymbol,
            AssetClass = AssetClass,
            BaseAsset = Assets.Base,
            QuoteAsset = Assets.Quote,
            PriceIncrement = Rules.PriceIncrement,
            QuantityIncrement = Rules.QuantityIncrement,
            MinimumQuantity = Rules.MinimumQuantity,
            MaximumQuantity = Rules.MaximumQuantity,
            ContractSize = Rules.ContractSize
        };
    }
}

internal sealed record AssetPairConfigurationDto
{
    public required string Base { get; init; }

    public required string Quote { get; init; }
}

internal sealed record InstrumentRulesConfigurationDto
{
    public required decimal PriceIncrement { get; init; }

    public required decimal QuantityIncrement { get; init; }

    public required decimal MinimumQuantity { get; init; }

    public required decimal MaximumQuantity { get; init; }

    public decimal ContractSize { get; init; } = 1m;
}

internal sealed record StrategyConfigurationDto
{
    public required string Id { get; init; }

    public required int ShortPeriod { get; init; }

    public required int LongPeriod { get; init; }

    public required decimal OrderQuantity { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Id)
            || ShortPeriod < 1
            || LongPeriod <= ShortPeriod
            || OrderQuantity <= 0m)
        {
            throw new InvalidOperationException("Strategy configuration is invalid.");
        }
    }
}

internal sealed record RiskConfigurationDto
{
    public required decimal MaximumOrderQuantity { get; init; }

    public required decimal MaximumAbsolutePosition { get; init; }

    public required decimal MaximumSpreadBps { get; init; }

    public required int MaximumOpenOrders { get; init; }

    public bool AllowShortSelling { get; init; }

    public void EnsureValid()
    {
        if (MaximumOrderQuantity <= 0m
            || MaximumAbsolutePosition <= 0m
            || MaximumSpreadBps < 0m
            || MaximumOpenOrders < 1)
        {
            throw new InvalidOperationException("Risk configuration is invalid.");
        }
    }
}

internal sealed record ExecutionConfigurationDto
{
    public decimal SlippageBps { get; init; }

    public decimal CommissionBps { get; init; }

    public decimal? MaximumFillQuantityPerBar { get; init; }

    public int OutboundLatencyMilliseconds { get; init; }

    public void EnsureValid()
    {
        if (SlippageBps < 0m
            || CommissionBps < 0m
            || MaximumFillQuantityPerBar <= 0m
            || OutboundLatencyMilliseconds < 0)
        {
            throw new InvalidOperationException("Execution configuration is invalid.");
        }
    }
}

internal sealed record OutputConfigurationDto
{
    public required string Directory { get; init; }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Directory))
        {
            throw new InvalidOperationException("An output directory is required.");
        }
    }
}
