using TradingClassifier.Configuration;
using TradingClassifier.Features;

namespace TradingClassifier.Labels;

/// <summary>Section 30's <c>ILabelGenerator</c>.</summary>
public interface ILabelGenerator
{
    /// <summary>
    /// Labels the candle at <paramref name="index"/> using candles strictly after it. Returns null
    /// when the horizon runs past the end of the series - section 26's "remove rows without a
    /// future target".
    /// </summary>
    (TradeLabel Label, decimal ExcursionAtr)? Label(
        IReadOnlyList<ClassifierCandle> candles,
        int index,
        decimal atr);
}

/// <summary>
/// Sections 9 to 12.
/// <para>
/// This is the only type in the library allowed to read candles after <c>t</c>, and it exists
/// separately from <see cref="FeatureEngine"/> precisely so that section 25's rule has a visible
/// boundary: features never see the future because the code that does is not reachable from them.
/// Its output is the target column alone - it never contributes a model input.
/// </para>
/// </summary>
public sealed class AtrThresholdLabelGenerator : ILabelGenerator
{
    private readonly int _horizon;
    private readonly decimal _multiplier;
    private readonly bool _useMaximumExcursion;
    private readonly TimeSpan? _expectedSpacing;
    private readonly int _gapToleranceBars;

    /// <param name="options">Validated classifier options.</param>
    /// <param name="expectedSpacing">
    /// The series' nominal bar spacing. When supplied, rows whose label window spans materially
    /// more wall-clock time than <c>horizon x spacing</c> - because bars are missing - are dropped
    /// instead of being labelled over a longer horizon than intended.
    /// </param>
    /// <param name="gapToleranceBars">How many missing bars to tolerate before dropping a row.</param>
    public AtrThresholdLabelGenerator(
        ClassifierOptions options,
        TimeSpan? expectedSpacing = null,
        int gapToleranceBars = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _horizon = options.PredictionHorizon;
        _multiplier = options.AtrTargetMultiplier;
        _useMaximumExcursion = options.UseMaximumExcursionLabels;
        _expectedSpacing = expectedSpacing;
        _gapToleranceBars = Math.Max(0, gapToleranceBars);
    }

    public (TradeLabel Label, decimal ExcursionAtr)? Label(
        IReadOnlyList<ClassifierCandle> candles,
        int index,
        decimal atr)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (index < 0 || index >= candles.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        int target = index + _horizon;
        if (target >= candles.Count)
            return null;

        // A missing bar makes "index + horizon" span more wall-clock time than intended: on a
        // gap-heavy series "10 candles ahead" can be hours rather than minutes, so the label would
        // silently describe a different prediction problem for those rows. Such rows are dropped
        // rather than mislabelled.
        if (_expectedSpacing is TimeSpan spacing)
        {
            TimeSpan elapsed = candles[target].Timestamp - candles[index].Timestamp;
            TimeSpan expected = spacing * _horizon;
            if (elapsed > expected + (spacing * _gapToleranceBars))
                return null;
        }

        // A zero or negative ATR would make every move "significant". It only happens on degenerate
        // data (a completely flat warm-up window), and such a row carries no usable target.
        if (atr <= 0m)
            return null;

        decimal threshold = atr * _multiplier;
        decimal close = candles[index].Close;

        return _useMaximumExcursion
            ? LabelByExcursion(candles, index, target, close, threshold, atr)
            : LabelByFutureClose(candles[target].Close - close, threshold, atr);
    }

    /// <summary>Section 11, the V1 target: where the close actually ended up.</summary>
    private static (TradeLabel, decimal) LabelByFutureClose(decimal change, decimal threshold, decimal atr)
    {
        TradeLabel label = change > threshold
            ? TradeLabel.Buy
            : change < -threshold
                ? TradeLabel.Sell
                : TradeLabel.NoTrade;
        return (label, change / atr);
    }

    /// <summary>
    /// Section 12's improved target: the best and worst the window offered, so an opportunity that
    /// was later retraced still counts.
    /// </summary>
    private static (TradeLabel, decimal) LabelByExcursion(
        IReadOnlyList<ClassifierCandle> candles,
        int index,
        int target,
        decimal close,
        decimal threshold,
        decimal atr)
    {
        decimal futureHigh = decimal.MinValue;
        decimal futureLow = decimal.MaxValue;
        for (int cursor = index + 1; cursor <= target; cursor++)
        {
            if (candles[cursor].High > futureHigh) futureHigh = candles[cursor].High;
            if (candles[cursor].Low < futureLow) futureLow = candles[cursor].Low;
        }

        decimal upMove = futureHigh - close;
        decimal downMove = close - futureLow;
        bool upQualifies = upMove > threshold;
        bool downQualifies = downMove > threshold;

        // Both directions clearing the threshold means the window whipsawed. Candle extremes carry
        // no ordering, so which came first is unknowable here - and a label that guessed would be
        // teaching the model a coin flip. Section 12 wants opportunity detection, not path
        // reconstruction, so this is NO_TRADE.
        if (upQualifies == downQualifies)
            return (TradeLabel.NoTrade, (upMove - downMove) / atr);

        return upQualifies
            ? (TradeLabel.Buy, upMove / atr)
            : (TradeLabel.Sell, -downMove / atr);
    }
}
