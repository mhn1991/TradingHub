using Brokers.Models;

namespace ChartAnnotator.Models;

/// <summary>
/// One instrument's causal return/volatility observation, feeding a leave-one-out
/// currency-strength computation. Defined alongside the other market-context DTOs
/// (rather than in PortfolioManager, which depends on Agent/ChartAnnotator) so
/// AgentMarketContext can carry a strength snapshot without a circular project reference.
/// </summary>
public sealed record CurrencyPairReturn
{
    public required InstrumentKey Instrument { get; init; }
    public required decimal LogReturn { get; init; }
    public required decimal RealizedVolatility { get; init; }
    public decimal DataQualityWeight { get; init; } = 1m;
    public decimal LiquidityWeight { get; init; } = 1m;
}

public sealed record CurrencyStrengthSnapshot
{
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyDictionary<string, decimal> Scores { get; init; }
    public required IReadOnlyDictionary<string, decimal> Coverage { get; init; }
    public required string MethodVersion { get; init; }
}
