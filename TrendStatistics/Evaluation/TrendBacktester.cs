using TrendStatistics.Data;
using TrendStatistics.Detection;
using TrendStatistics.Profiles;
using TrendStatistics.Runtime;
using TrendStatistics.Segmentation;
using TrendStatistics.Trading;

namespace TrendStatistics.Evaluation;

/// <summary>Costs applied to every simulated round trip - section 64 requires them.</summary>
public sealed record TrendCostModel
{
    /// <summary>Round-trip cost as a percentage of price. Gold is roughly 0.03% on OANDA.</summary>
    public decimal RoundTripPct { get; init; }

    public static TrendCostModel Free => new();
}

/// <summary>
/// Section 51's <c>TrendBacktester</c>: runs the swing strategy over a candle series against a
/// <b>frozen</b> profile.
/// <para>
/// Freezing is the point. Section 48 requires that the test period never influence the trend
/// statistics, bootstrap quantiles, or entry and exit thresholds before it is evaluated, so this
/// type deliberately takes a profile it cannot modify and never rebuilds one from the bars it is
/// trading.
/// </para>
/// <para>
/// The detector still runs from the first supplied candle so its state is warm, but trades are only
/// opened inside <c>tradeFrom</c>..<c>tradeTo</c>. Warming the detector on the test window itself
/// would produce a different - and better - trend segmentation than live trading could achieve.
/// </para>
/// </summary>
public sealed class TrendBacktester(TrendDetectorConfig config, TrendCostModel? costs = null)
{
    private readonly TrendDetectorConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly TrendCostModel _costs = costs ?? TrendCostModel.Free;

    public IReadOnlyList<SwingTrade> Run(
        IReadOnlyList<Candle> candles,
        ProfileRepository frozenProfiles,
        RegimeClassifier? frozenRegimes,
        SwingEntryOptions entryOptions,
        DateTimeOffset tradeFrom,
        DateTimeOffset tradeTo,
        int minimumSamples)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(frozenProfiles);
        ArgumentNullException.ThrowIfNull(entryOptions);
        entryOptions.Validate();

        TrendDetector detector = new(_config);
        SwingSignalGenerator generator = new(entryOptions);
        List<SwingTrade> trades = [];

        decimal? entryPrice = null;
        TrendDirection? entryDirection = null;
        DateTimeOffset entryTime = default;
        decimal entryStructuralStart = 0m;
        decimal? postEntryExtreme = null;
        int entryBar = 0;
        int bar = 0;
        Candle? lastCandle = null;

        foreach (Candle candle in candles)
        {
            bar++;
            lastCandle = candle;
            TrendDetectorUpdate update = detector.Apply(candle);

            if (entryDirection is TrendDirection openDirection)
            {
                decimal favorable = openDirection == TrendDirection.Bullish ? candle.High : candle.Low;
                postEntryExtreme = postEntryExtreme is null
                    ? favorable
                    : openDirection == TrendDirection.Bullish
                        ? Math.Max(postEntryExtreme.Value, favorable)
                        : Math.Min(postEntryExtreme.Value, favorable);
            }

            if (update.CompletedTrend is TrendRecord finished)
            {
                if (entryPrice is decimal open && entryDirection is TrendDirection direction)
                {
                    trades.Add(new SwingTrade(
                        direction, entryTime, open, CandleCloseTime(candle), candle.Close,
                        entryStructuralStart, finished.FavorableExtremePrice, bar - entryBar,
                        postEntryExtreme));
                    entryPrice = null;
                    entryDirection = null;
                    postEntryExtreme = null;
                }
                continue;
            }

            if (entryPrice is not null)
                continue;

            TrendState state = update.State;
            if (state.Phase is not (TrendPhase.Confirmed or TrendPhase.Mature)
                || state.Direction is not TrendDirection live)
                continue;
            DateTimeOffset closeTime = CandleCloseTime(candle);
            if (closeTime < tradeFrom || closeTime >= tradeTo)
                continue;

            // Section 60: prefer the conditioned profile, fall back when it is too thin.
            ProfileKey key = frozenRegimes is null
                ? ProfileKey.Unconditional(candle.Symbol, live)
                : new ProfileKey(candle.Symbol, live,
                    frozenRegimes.Classify(state.VolatilityPctAtConfirmation));

            DirectionTrendProfile? profile = frozenProfiles.GetWithFallback(key, minimumSamples);
            if (profile is null || !profile.IsReliable(minimumSamples))
                continue;

            if (!generator.Evaluate(state, profile).IsActionable)
                continue;

            entryPrice = ApplyCosts(candle.Close, live);
            entryDirection = live;
            entryTime = closeTime;
            entryStructuralStart = state.StructuralStartPrice ?? candle.Close;
            // Entry occurs at this candle's close. Its earlier high/low was not available after
            // entry and including it would overstate post-entry MFE (and understate giveback).
            postEntryExtreme = entryPrice;
            entryBar = bar;
        }

        // A final open trade is real exposure, not a missing observation. Mark it to market rather
        // than dropping it and biasing results toward trades whose exits happened to be observed.
        if (lastCandle is not null
            && entryPrice is decimal finalEntry
            && entryDirection is TrendDirection finalDirection)
        {
            TrendState finalState = detector.CurrentState;
            trades.Add(new SwingTrade(
                finalDirection,
                entryTime,
                finalEntry,
                CandleCloseTime(lastCandle),
                lastCandle.Close,
                entryStructuralStart,
                finalState.FavorableExtremePrice ?? postEntryExtreme ?? lastCandle.Close,
                bar - entryBar + 1,
                postEntryExtreme));
        }

        return trades;
    }

    /// <summary>
    /// Charges the full round trip at entry, which is equivalent to splitting it across both legs
    /// and keeps the exit price honest for the excursion metrics.
    /// </summary>
    private decimal ApplyCosts(decimal price, TrendDirection direction)
    {
        if (_costs.RoundTripPct == 0m)
            return price;
        decimal adjustment = price * _costs.RoundTripPct / 100m;
        return direction == TrendDirection.Bullish ? price + adjustment : price - adjustment;
    }

    private DateTimeOffset CandleCloseTime(Candle candle) => candle.OpenTime + _config.Timeframe;
}
