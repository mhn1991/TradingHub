using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Ranges;

/// <summary>Thresholds for module 6's range rules.</summary>
public sealed record RangeOptions
{
    /// <summary>
    /// Fraction of the span at each end that counts as too high or too low. Module 6: "As a rule of
    /// thumb, we will use 20% and 80%".
    /// </summary>
    public decimal EdgeFraction { get; init; } = 0.20m;

    /// <summary>
    /// Whether an untradeable zone can still bound the range. It can, and this defaults to true: a
    /// zone that fails the 2:1 tradeability bar is still a real level price has to travel through,
    /// and module 7 is explicit that failing 2:1 "does not negate the level as a valid imbalance".
    /// </summary>
    public bool BoundWithUntradeableZones { get; init; } = true;

    public void Validate()
    {
        if (EdgeFraction is <= 0m or >= 0.5m)
        {
            throw new InvalidOperationException(
                "EdgeFraction must be within (0, 0.5); at 0.5 the two bands meet and nothing is tradeable.");
        }
    }
}

/// <summary>
/// Computes a timeframe's supply and demand range from its live zones.
/// <para>
/// Module 6: "We must use two opposing imbalances, one above the current price (supply) and another
/// below the current price (demand) in the same timeframe to have a range. A single imbalance cannot
/// tell us how far or how close the underlying price is from another imbalance, making it impossible
/// to establish a range or percentage."
/// </para>
/// </summary>
public static class RangeCalculator
{
    public static SupplyDemandRange Compute(
        IReadOnlyList<Imbalance> zones, decimal price, RangeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(zones);
        RangeOptions settings = options ?? new RangeOptions();
        settings.Validate();

        bool Eligible(Imbalance zone) =>
            zone.State != ImbalanceState.Eliminated &&
            (settings.BoundWithUntradeableZones || zone.MeetsTradeabilityCriteria);

        // The nearest opposing levels are the ones price has to deal with first, so the range is
        // bounded by those rather than by the widest pair available.
        decimal? supply = zones
            .Where(zone => Eligible(zone) && zone.Kind == ImbalanceKind.Supply && zone.Proximal > price)
            .Select(zone => (decimal?)zone.Proximal)
            .DefaultIfEmpty(null)
            .Min();

        decimal? demand = zones
            .Where(zone => Eligible(zone) && zone.Kind == ImbalanceKind.Demand && zone.Proximal < price)
            .Select(zone => (decimal?)zone.Proximal)
            .DefaultIfEmpty(null)
            .Max();

        if (supply is not decimal top || demand is not decimal bottom)
        {
            return SupplyDemandRange.Unavailable(
                supply is null && demand is null
                    ? "No opposing imbalance on either side."
                    : supply is null
                        ? "No supply above price; an all-time-high style scenario."
                        : "No demand below price; an all-time-low style scenario.");
        }

        decimal span = top - bottom;
        if (span <= 0m)
            return SupplyDemandRange.Unavailable("Opposing proximal lines do not span a range.");

        // Module 6's worked example: a 7.5 point span at 20% moves each edge inward by 1.5 points,
        // giving 112 under a supply proximal of 113.50 and 107.50 over a demand proximal of 106.
        decimal edge = span * settings.EdgeFraction;
        decimal tooHighAbove = top - edge;
        decimal tooLowBelow = bottom + edge;
        decimal position = (price - bottom) / span;

        RangeLocation location = price >= tooHighAbove
            ? RangeLocation.TooHigh
            : price <= tooLowBelow
                ? RangeLocation.TooLow
                : RangeLocation.Middle;

        return new SupplyDemandRange
        {
            SupplyProximal = top,
            DemandProximal = bottom,
            TooHighAbove = tooHighAbove,
            TooLowBelow = tooLowBelow,
            Position = position,
            Location = location,
            Reason = location switch
            {
                RangeLocation.TooHigh =>
                    $"Price {price:F2} is in the top {settings.EdgeFraction:P0} of {bottom:F2}-{top:F2}; no buying below this timeframe.",
                RangeLocation.TooLow =>
                    $"Price {price:F2} is in the bottom {settings.EdgeFraction:P0} of {bottom:F2}-{top:F2}; no selling below this timeframe.",
                _ => $"Price {price:F2} sits at {position:P0} of {bottom:F2}-{top:F2}."
            }
        };
    }
}
