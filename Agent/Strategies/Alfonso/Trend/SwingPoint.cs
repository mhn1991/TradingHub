using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Trend;

/// <summary>
/// A valley or a peak - module 2's "two types of imbalance", minus the continuation patterns.
/// <para>
/// Swings are not detected separately from zones. Module 2 defines a valley as exactly the structure
/// that forms a demand imbalance ("Leg in or bearish move, a basing structure, leg out or bullish
/// move") and a peak as the structure that forms a supply imbalance, so deriving swings from
/// confirmed imbalances keeps one definition rather than two that can drift apart. The continuation
/// flag the zone already carries is what module 3 needs to exclude: "Continuation Patterns (CPs)
/// will not be used to connect trendlines."
/// </para>
/// </summary>
public sealed record SwingPoint
{
    /// <summary>Bar index of the swing extreme, used for slope arithmetic.</summary>
    public required int Index { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>The extreme itself: the valley's low, or the peak's high. This is the zone's distal line.</summary>
    public required decimal Price { get; init; }

    /// <summary>Valleys come from demand zones, peaks from supply zones.</summary>
    public required ImbalanceKind Kind { get; init; }

    public bool IsValley => Kind == ImbalanceKind.Demand;

    public bool IsPeak => Kind == ImbalanceKind.Supply;
}
