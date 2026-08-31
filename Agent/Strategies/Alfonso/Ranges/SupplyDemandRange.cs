using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Ranges;

/// <summary>Where price sits between the opposing imbalances that bound a timeframe's range.</summary>
public enum RangeLocation
{
    /// <summary>
    /// No range could be computed. Module 6: "In all-time highs and all-time lows scenarios, we will
    /// not be able to calculate the range because there is no opposing imbalance to do the math."
    /// </summary>
    Unavailable,

    /// <summary>In the bottom band. "If the price is too low in the range, stop selling into it."</summary>
    TooLow,

    /// <summary>Between the bands, where both directions remain available.</summary>
    Middle,

    /// <summary>In the top band. "If the price is too high in the range, stop buying into it."</summary>
    TooHigh
}

/// <summary>
/// A timeframe's supply and demand range: how expensive or cheap the asset is between the nearest
/// opposing imbalances, and what that permits.
/// <para>
/// Module 6: "The supply and demand range is the percentage used to define how expensive or cheap
/// any given asset is ... As a rule of thumb, we will use 20% and 80% to determine that the
/// underlying asset is too high or too low in the range to trade against it."
/// </para>
/// </summary>
public sealed record SupplyDemandRange
{
    /// <summary>Proximal line of the nearest live supply zone above price, when one exists.</summary>
    public decimal? SupplyProximal { get; init; }

    /// <summary>Proximal line of the nearest live demand zone below price, when one exists.</summary>
    public decimal? DemandProximal { get; init; }

    /// <summary>
    /// Upper band edge: the supply proximal pulled inward by the configured percentage of the span.
    /// In module 6's worked example a 7.5 point span with a 20% rule puts this at 112 under a supply
    /// proximal of 113.50.
    /// </summary>
    public decimal? TooHighAbove { get; init; }

    /// <summary>Lower band edge, the demand proximal pushed inward by the same amount.</summary>
    public decimal? TooLowBelow { get; init; }

    /// <summary>Where price sits in the span, 0 at the demand proximal and 1 at the supply proximal.</summary>
    public decimal? Position { get; init; }

    public required RangeLocation Location { get; init; }

    public required string Reason { get; init; }

    /// <summary>
    /// Whether a long is permitted here. Module 6: "If the price is too high in the range, stop
    /// buying into it using lower timeframes."
    /// <para>
    /// An unavailable range permits both. At all-time highs and lows the course does not stop
    /// trading - module 11 covers exactly that case and offers entries - so a missing range is an
    /// absence of information, not a prohibition.
    /// </para>
    /// </summary>
    public bool AllowsBuying => Location != RangeLocation.TooHigh;

    /// <summary>Whether a short is permitted. "If the price is too low in the range, stop selling."</summary>
    public bool AllowsSelling => Location != RangeLocation.TooLow;

    public static SupplyDemandRange Unavailable(string reason) => new()
    {
        Location = RangeLocation.Unavailable,
        Reason = reason
    };
}

/// <summary>
/// A zone that has been reached and has not broken. Module 6: "An imbalance in control is an
/// imbalance that has been tested (any number of times) and remains unbroken. It must be hit and
/// unbroken to remain in control without an opposing zone gaining control."
/// </summary>
public sealed record ZoneInControl
{
    public required Imbalance Zone { get; init; }

    /// <summary>When price first reached the zone's proximal line and took control.</summary>
    public required DateTimeOffset Since { get; init; }

    public ImbalanceKind Kind => Zone.Kind;
}
