using Brokers.Models;

namespace LiveTrading.Configuration;

/// <summary>One strategy deployment assigned to a market. <see cref="Mode"/> is a host-only
/// activation decision and is never passed into the Agent or domain subsystems.
/// <see cref="PolicyBundleId"/> resolves one immutable <c>TradingPolicyProfile</c> promoted from
/// simulator/research; the live host has no separate Agent/risk/management configuration.</summary>
public sealed record LiveStrategyAssignment
{
    public required string StrategyId { get; init; }
    public required string PolicyBundleId { get; init; }
    public StrategyActivationMode Mode { get; init; } = StrategyActivationMode.Shadow;
    public bool Enabled { get; init; } = true;
}

public sealed record LiveMarketDefinition
{
    public required InstrumentKey Instrument { get; init; }
    public IReadOnlyList<LiveStrategyAssignment> Strategies { get; init; } = [];
    public required BarInterval ExecutionInterval { get; init; }
    public required BarInterval AnalysisBaseInterval { get; init; }
    public required IReadOnlySet<BarInterval> AnalysisIntervals { get; init; }
    public bool Enabled { get; init; } = true;
    public decimal? MaximumInstrumentRiskPercent { get; init; }
    public decimal? MaximumInstrumentMarginPercent { get; init; }
    public string? LiquidityProfileId { get; init; }
    public int CandleCapacity { get; init; } = 2_000;
}

public sealed record LiveMarketUniverseOptions
{
    public const string SectionName = "LiveMarkets";

    public IReadOnlyList<LiveMarketDefinition> Markets { get; init; } = DefaultFivePair;

    public static IReadOnlyList<LiveMarketDefinition> DefaultFivePair { get; } = BuildDefault();

    private static IReadOnlyList<LiveMarketDefinition> BuildDefault()
    {
        BarInterval m1 = BarInterval.Minutes(1);
        IReadOnlySet<BarInterval> analysisIntervals = new HashSet<BarInterval>
        {
            m1,
            BarInterval.Minutes(5),
            BarInterval.Minutes(15),
            BarInterval.Hours(1)
        };

        string[] instruments = ["FX:EUR/USD", "FX:GBP/USD", "FX:USD/JPY", "FX:GBP/JPY", "FX:EUR/JPY"];
        return instruments.Select(instrument => new LiveMarketDefinition
        {
            Instrument = new InstrumentKey(instrument),
            ExecutionInterval = m1,
            AnalysisBaseInterval = m1,
            AnalysisIntervals = analysisIntervals
        }).ToList();
    }
}
