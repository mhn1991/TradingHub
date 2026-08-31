namespace Agent.Strategies.Alfonso.Zones;

/// <summary>What changed on the timeframe when one closed candle was applied.</summary>
public sealed record ImbalanceDetectorUpdate
{
    /// <summary>Zones confirmed by this candle, i.e. whose consolidation away completed here.</summary>
    public IReadOnlyList<Imbalance> Created { get; init; } = [];

    /// <summary>Zones whose distal line this candle penetrated.</summary>
    public IReadOnlyList<Imbalance> Eliminated { get; init; } = [];

    /// <summary>Zones that completed a test on this candle, i.e. got one pullback older.</summary>
    public IReadOnlyList<Imbalance> Tested { get; init; } = [];

    /// <summary>
    /// Zones whose proximal line price reached on this candle. Distinct from <see cref="Tested"/>:
    /// reaching a level starts a test, while completing one needs a full candle back away from it.
    /// Module 6 hands control to a zone at the moment price arrives - "once price reaches the supply
    /// [1] proximal line at [A], we consider the imbalance to be in control" - so control cannot be
    /// driven off completed tests alone.
    /// </summary>
    public IReadOnlyList<Imbalance> Touched { get; init; } = [];

    public static readonly ImbalanceDetectorUpdate Empty = new();
}
