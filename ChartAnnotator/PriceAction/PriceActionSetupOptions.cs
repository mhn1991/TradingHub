using ChartAnnotator.Models;

namespace ChartAnnotator.PriceAction;

/// <summary>
/// Options for same-timeframe composite price-action setups.
/// See <c>PRICE_ACTION_SETUPS_AND_MTF.md</c> for the full design.
/// </summary>
public sealed record PriceActionSetupOptions
{
    public int SweepFollowThroughBars { get; init; } = 5;
    public int ChoChFollowThroughBars { get; init; } = 8;
    public decimal MinimumSetupConfidence { get; init; } = 55m;
    public IReadOnlyList<PriceActionSetupType> EnabledSetups { get; init; } =
    [
        PriceActionSetupType.BullishBreakRetestHold,
        PriceActionSetupType.BearishBreakRetestHold,
        PriceActionSetupType.BullishChoChRetestHold,
        PriceActionSetupType.BearishChoChRetestHold,
        PriceActionSetupType.BullishSweepDisplacement,
        PriceActionSetupType.BearishSweepDisplacement,
        PriceActionSetupType.BullishSweepChoCh,
        PriceActionSetupType.BearishSweepChoCh
    ];

    public void Validate()
    {
        if (SweepFollowThroughBars < 1 ||
            ChoChFollowThroughBars < 1 ||
            MinimumSetupConfidence is < 0m or > 100m ||
            EnabledSetups is null)
        {
            throw new ArgumentOutOfRangeException(nameof(PriceActionSetupOptions));
        }
    }

    public bool IsEnabled(PriceActionSetupType type) =>
        EnabledSetups.Contains(type);
}
