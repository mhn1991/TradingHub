namespace TradingClassifier.Features;

/// <summary>
/// The minimum candle the blueprint's section 3 specifies. Deliberately not the repo's
/// <c>Brokers.Models.Candle</c>: section 1 restricts the model to OHLC and indicators derived from
/// it, and a type that cannot carry volume, spread or broker metadata makes that restriction
/// impossible to violate by accident.
/// </summary>
public readonly record struct ClassifierCandle(
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    // Optional because the fetched OANDA candle files carry OHLC only; simulation replay chunks do
    // carry it. Null means "this source has no volume", which is why FeatureGroups.Volume must be
    // checked against the data rather than assumed available.
    decimal? Volume = null)
{
    public decimal Range => High - Low;
    public decimal Body => Math.Abs(Close - Open);
    public decimal UpperWick => High - Math.Max(Open, Close);
    public decimal LowerWick => Math.Min(Open, Close) - Low;
}

/// <summary>
/// One row of model input. <see cref="Values"/> is positionally aligned to the
/// <see cref="FeatureSchema"/> that produced it.
/// </summary>
public sealed record FeatureVector
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The close of the candle this row describes. Carried for label generation and backtesting,
    /// never fed to the model - section 4 is explicit that raw price level is not a feature.
    /// </summary>
    public required decimal Close { get; init; }

    /// <summary>
    /// ATR at this candle, for the section 11 volatility-scaled label threshold. Also not a model
    /// input; the ATR *features* are the normalised <c>atr{n}_pct</c> columns inside
    /// <see cref="Values"/>.
    /// </summary>
    public required decimal LabelAtr { get; init; }

    public required float[] Values { get; init; }
}

/// <summary>A feature row with its generated target - see sections 9 to 12.</summary>
public sealed record LabeledFeatureRow
{
    public required FeatureVector Features { get; init; }
    public required TradeLabel Label { get; init; }

    /// <summary>
    /// The move that produced the label, in ATR multiples. Kept for diagnostics: it is what makes
    /// a confusion matrix interpretable ("the SELLs it missed were all marginal").
    /// </summary>
    public required decimal LabelExcursionAtr { get; init; }
}

/// <summary>
/// Section 10's encoding. The numeric values are part of the contract - they are the ML.NET key
/// values the trained model emits probabilities for, in this order.
/// </summary>
public enum TradeLabel
{
    Sell = 0,
    NoTrade = 1,
    Buy = 2
}
