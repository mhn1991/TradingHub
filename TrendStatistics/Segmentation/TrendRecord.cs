using TrendStatistics.Detection;

namespace TrendStatistics.Segmentation;

/// <summary>
/// A causally detected, completed historical trend.
/// Percentage values are positive magnitudes; direction is stored separately.
/// </summary>
public sealed record TrendRecord
{
    public required string Symbol { get; init; }

    public required TrendDirection Direction { get; init; }

    public required DateTimeOffset StructuralStartTime { get; init; }

    public required decimal StructuralStartPrice { get; init; }

    public required DateTimeOffset ConfirmationTime { get; init; }

    public required decimal ConfirmationPrice { get; init; }

    public required DateTimeOffset EndTime { get; init; }

    public required decimal EndPrice { get; init; }

    public required DateTimeOffset FavorableExtremeTime { get; init; }

    public required decimal FavorableExtremePrice { get; init; }

    public required int DurationBars { get; init; }

    public required decimal DurationHours { get; init; }

    public required decimal TotalMovePct { get; init; }

    public required decimal MoveAfterConfirmationPct { get; init; }

    public required decimal AtrNormalizedMove { get; init; }

    /// <summary>
    /// ATR as a percentage of confirmation price. Unlike <see cref="AtrNormalizedMove"/>, this is
    /// known at confirmation and can therefore be used as a causal volatility-regime feature.
    /// </summary>
    public decimal VolatilityPctAtConfirmation { get; init; }

    /// <summary>
    /// Worst excursion against the trend, measured from the confirmation price, in percent.
    /// <para>
    /// Distinct from <see cref="MaximumRetracementPct"/>, which measures giveback from the running
    /// favourable extreme. A trend that runs straight up and then hands back 3% has a large
    /// retracement but never traded against a confirmation entry at all; this field is what a stop
    /// placed at confirmation would actually have had to survive.
    /// </para>
    /// <para>
    /// Replaced a former <c>MfePct</c> field that was assigned the identical value as
    /// <see cref="MoveAfterConfirmationPct"/> - MFE measured from confirmation IS the move to the
    /// favourable extreme, so the field carried no information.
    /// </para>
    /// </summary>
    public required decimal MaximumAdverseExcursionPct { get; init; }

    public required decimal MaximumRetracementPct { get; init; }

    public required int ConfirmationDelayBars { get; init; }

    public required decimal ConfirmationDelayPct { get; init; }
}
