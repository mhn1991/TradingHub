using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Sequence;

/// <summary>One permitted way into a trade: which timeframe's zone, and what it must sit inside.</summary>
public sealed record ScenarioEntry
{
    /// <summary>The timeframe whose zone the order is placed at.</summary>
    public required SequenceRole EntryTimeframe { get; init; }

    /// <summary>The timeframe whose zone it must be nested in, or null when it may stand alone.</summary>
    public SequenceRole? NestedIn { get; init; }

    public required string Description { get; init; }
}

/// <summary>What the current three-timeframe alignment permits.</summary>
public sealed record ScenarioResolution
{
    public required bool CanTrade { get; init; }

    /// <summary>Which side is allowed. Demand zones are bought; supply zones are sold.</summary>
    public ImbalanceKind? Side { get; init; }

    public IReadOnlyList<ScenarioEntry> Entries { get; init; } = [];

    public required string Reason { get; init; }

    public static ScenarioResolution No(string reason) => new() { CanTrade = false, Reason = reason };
}

/// <summary>
/// Module 11's table of permitted setups: four bullish and four bearish combinations of
/// top / middle / lower trend state, and nothing else.
/// <para>
/// The closing instruction on that table is load-bearing: "If in doubt, do nothing or wait for new
/// imbalances to be created." Module 9 says the same of everything omitted - "Any scenario not
/// included in these three slides will be considered advanced and will require a thorough
/// understanding of price action and counter-trend scenarios (not covered in the core rules)."
/// So the matrix is a whitelist, and an unlisted alignment is a refusal rather than a gap.
/// </para>
/// </summary>
public static class ScenarioMatrix
{
    public static ScenarioResolution Resolve(
        AlfonsoTrend top, AlfonsoTrend middle, AlfonsoTrend lower)
    {
        // Module 10: "If the top timeframe of your sequence is out of alignment, no set and forget
        // trades will be possible." The top timeframe owns direction, so nothing else can rescue it.
        if (top is not (AlfonsoTrend.Uptrend or AlfonsoTrend.Downtrend))
            return ScenarioResolution.No($"Top timeframe is {top}; the sequence has no direction.");

        bool bullish = top == AlfonsoTrend.Uptrend;
        AlfonsoTrend with = bullish ? AlfonsoTrend.Uptrend : AlfonsoTrend.Downtrend;
        ImbalanceKind side = bullish ? ImbalanceKind.Demand : ImbalanceKind.Supply;
        string zone = bullish ? "demand" : "supply";
        string action = bullish ? "Long" : "Short";

        // Row 1: all three aligned. Enter at the execution timeframe's own zones.
        if (middle == with && lower == with)
        {
            return new ScenarioResolution
            {
                CanTrade = true,
                Side = side,
                Reason = $"All three timeframes {with}.",
                Entries =
                [
                    new ScenarioEntry
                    {
                        EntryTimeframe = SequenceRole.Lower,
                        Description = $"{action} at lower timeframe {zone} levels."
                    }
                ]
            };
        }

        // Row 2: the execution timeframe has lost alignment, so its zones only count inside a middle
        // timeframe zone.
        if (middle == with && lower == AlfonsoTrend.OutOfAlignment)
        {
            return new ScenarioResolution
            {
                CanTrade = true,
                Side = side,
                Reason = $"Top and middle {with}, lower out of alignment.",
                Entries =
                [
                    new ScenarioEntry
                    {
                        EntryTimeframe = SequenceRole.Lower,
                        NestedIn = SequenceRole.Middle,
                        Description = $"{action} at lower {zone} levels nested at middle timeframe {zone}."
                    }
                ]
            };
        }

        // Rows 3 and 4 share an alignment and offer two ways in: drill down to the execution
        // timeframe, or take the middle timeframe zone itself. Both must sit inside a top zone.
        if (middle == AlfonsoTrend.OutOfAlignment && lower == AlfonsoTrend.OutOfAlignment)
        {
            return new ScenarioResolution
            {
                CanTrade = true,
                Side = side,
                Reason = $"Top {with}, middle and lower both out of alignment.",
                Entries =
                [
                    new ScenarioEntry
                    {
                        EntryTimeframe = SequenceRole.Lower,
                        NestedIn = SequenceRole.Top,
                        Description = $"{action} at lower timeframe {zone} zones nested at top timeframe {zone}."
                    },
                    new ScenarioEntry
                    {
                        EntryTimeframe = SequenceRole.Middle,
                        NestedIn = SequenceRole.Top,
                        Description = $"{action} at the middle timeframe {zone} level nested at top timeframe {zone}."
                    }
                ]
            };
        }

        // Everything else - a timeframe opposing the top, or an unknown state - is off the table.
        return ScenarioResolution.No(
            $"Alignment {top}/{middle}/{lower} is not one of the eight permitted setups.");
    }
}
