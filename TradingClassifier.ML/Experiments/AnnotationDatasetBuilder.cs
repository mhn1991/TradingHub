using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Features;
using TradingClassifier.Labels;

namespace TradingClassifier.ML.Experiments;

/// <summary>
/// Builds a dataset that includes <see cref="FeatureGroups.Analysis"/> by running the repo's own
/// <see cref="ChartAnnotationEngine"/> over history, exactly as a backtest does, and handing each
/// candle's snapshot to the feature engine alongside the raw OHLC.
/// <para>
/// This is the training-side counterpart of what the live agent already receives for free, so the
/// analysis columns are produced by one implementation in both places - the blueprint's section 27
/// requirement, which the parallel indicator maths inside <see cref="FeatureEngine"/> does not
/// currently meet.
/// </para>
/// <para>
/// It is markedly slower than <see cref="DatasetBuilder"/>: the annotation engine also computes
/// swings, zones, trendlines, channels and market structure that nothing here reads. That cost is
/// accepted rather than forked, because a trimmed copy of the engine would be a second
/// implementation to drift.
/// </para>
/// </summary>
public sealed class AnnotationDatasetBuilder
{
    private readonly ClassifierOptions _options;
    private readonly ChartAnnotationOptions _annotation;
    private readonly InstrumentKey _instrument;

    /// <summary>
    /// Higher timeframe the TrendState group is joined from. 2H by default, not the blueprint's 4H:
    /// §2.18 walk-forwarded both and 2H gave PF 1.694 against 4H's 1.174, holding almost all of its
    /// in-sample value where 4H lost a third of it.
    /// </summary>
    private readonly TimeSpan _trendTimeframe;
    private readonly BarInterval _interval;

    public AnnotationDatasetBuilder(
        ClassifierOptions options,
        BarInterval interval,
        InstrumentKey? instrument = null,
        ChartAnnotationOptions? annotation = null,
        TimeSpan? trendTimeframe = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _interval = interval;
        _instrument = instrument ?? new InstrumentKey("METAL:XAU/USD");
        _trendTimeframe = trendTimeframe ?? TimeSpan.FromHours(2);

        // Liquidity and supply/demand are the most expensive part of the engine, so they stay off
        // unless the caller actually asked for those feature groups. Leaving them off while the
        // groups are enabled is worse than slow: every column comes back constant and the ablation
        // scores the group at exactly nothing, which is indistinguishable from a real null result.
        _annotation = annotation ?? new ChartAnnotationOptions
        {
            BollingerPeriod = options.BollingerPeriod,
            BollingerStandardDeviations = options.BollingerStandardDeviations,
            HeavyAnalysisEveryCandles = 1,
            SupplyDemand = new SupplyDemandCalculationProfile
            {
                Enabled = options.EnabledGroups.HasFlag(FeatureGroups.SupplyDemand)
            },
            Liquidity = new LiquidityCalculationProfile
            {
                Enabled = options.EnabledGroups.HasFlag(FeatureGroups.Liquidity)
            },
            // MarketRegimeOptions.Enabled also defaults to false, which left all four regime
            // columns at zero while still changing the feature count — so the ladder rung moved for
            // the wrong reason (LightGBM resampling a larger pool), not because regime informed it.
            MarketRegime = new MarketRegimeOptions
            {
                Enabled = options.EnabledGroups.HasFlag(FeatureGroups.Regime)
            }
        };
    }

    private static DateTimeOffset FloorTo(DateTimeOffset value, TimeSpan span) =>
        new(value.Ticks - (value.Ticks % span.Ticks), value.Offset);

    private static TimeSpan IntervalSpan(BarInterval interval)
    {
        DateTimeOffset anchor = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return interval.AddTo(anchor) - anchor;
    }

    public ClassifierDataset Build(IReadOnlyList<ClassifierCandle> candles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candles);

        ClassifierCandle[] ordered = candles.OrderBy(candle => candle.Timestamp).ToArray();
        ChartAnnotationEngine engine = new(_annotation);
        FeatureEngine features = new(_options);
        AtrThresholdLabelGenerator labeller = new(_options);
        // Higher-timeframe trend replay. Null unless the TrendState group is enabled, so the cost
        // is only paid when the columns are wanted.
        // One independent detector and bucket per configured trend timeframe. Each is fed only its
        // own CLOSED candles, so the causality guarantee is per-timeframe rather than shared.
        bool wantsTrend = FeatureEngine.RequiresTrendState(_options.EnabledGroups);
        int[] trendMinutes = [.. _options.TrendStateTimeframeMinutes];
        TrendStatistics.Detection.TrendDetector[] trendDetectors = wantsTrend
            ? [.. trendMinutes.Select(minutes => new TrendStatistics.Detection.TrendDetector(
                new TrendStatistics.Detection.TrendDetectorConfig
                {
                    Timeframe = TimeSpan.FromMinutes(minutes)
                }))]
            : [];
        TrendStatistics.Detection.TrendState?[] trendStates = new TrendStatistics.Detection.TrendState?[trendMinutes.Length];
        DateTimeOffset?[] bucketEnds = new DateTimeOffset?[trendMinutes.Length];
        decimal?[] bucketOpens = new decimal?[trendMinutes.Length];
        decimal[] bucketHighs = new decimal[trendMinutes.Length];
        decimal[] bucketLows = new decimal[trendMinutes.Length];
        decimal[] bucketCloses = new decimal[trendMinutes.Length];

        List<LabeledFeatureRow> rows = [];

        for (int index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClassifierCandle source = ordered[index];

            Candle candle = new()
            {
                Instrument = _instrument,
                Interval = _interval,
                // BarInterval exposes AddTo but no duration, so the open time is derived by
                // subtracting the span the interval adds to an arbitrary anchor.
                OpenTime = source.Timestamp - IntervalSpan(_interval),
                CloseTime = source.Timestamp,
                Prices = new Ohlc(source.Open, source.High, source.Low, source.Close),
                // Forwarded so the engine's volume analysis is real rather than empty. Without this
                // FeatureGroups.Volume emitted six constant columns and the ladder rung that added
                // it was byte-identical to the rung below.
                Volume = source.Volume is decimal traded
                    ? new MarketVolume(traded, VolumeKind.TickCount)
                    : null,
                IsComplete = true
            };

            AnalysisSnapshot snapshot = engine
                .ProcessAsync(new CandleClosedEvent(_instrument, _interval, candle, index + 1), null, cancellationToken)
                .AsTask().GetAwaiter().GetResult();

                // ---- causal higher-timeframe trend join ---------------------------------------
            // The detector sees ONLY closed higher-timeframe candles, bucketed as the series
            // streams. A row therefore sees the state as of the last CLOSED higher-timeframe bar,
            // never the bar it sits inside.
            //
            // This is the join V2 §4.3 and §6.2 both single out: aggregating the whole history,
            // detecting completed trends, then backfilling their direction, endpoints or
            // percentiles into earlier rows would leak the outcome into the features.
            if (wantsTrend)
            {
                for (int tf = 0; tf < trendMinutes.Length; tf++)
                {
                    TimeSpan span = TimeSpan.FromMinutes(trendMinutes[tf]);
                    DateTimeOffset bucket = FloorTo(source.Timestamp, span);
                    if (bucketEnds[tf] is null)
                    {
                        bucketEnds[tf] = bucket;
                    }
                    else if (bucket > bucketEnds[tf])
                    {
                        if (bucketOpens[tf] is decimal openPrice)
                        {
                            trendStates[tf] = trendDetectors[tf].Apply(new TrendStatistics.Data.Candle
                            {
                                Symbol = _instrument.Value,
                                OpenTime = bucketEnds[tf]!.Value,
                                Open = openPrice,
                                High = bucketHighs[tf],
                                Low = bucketLows[tf],
                                Close = bucketCloses[tf]
                            }).State;
                        }
                        bucketEnds[tf] = bucket;
                        bucketOpens[tf] = null;
                    }

                    if (bucketOpens[tf] is null)
                    {
                        bucketOpens[tf] = source.Open;
                        bucketHighs[tf] = source.High;
                        bucketLows[tf] = source.Low;
                    }
                    else
                    {
                        bucketHighs[tf] = Math.Max(bucketHighs[tf], source.High);
                        bucketLows[tf] = Math.Min(bucketLows[tf], source.Low);
                    }
                    bucketCloses[tf] = source.Close;
                }
            }

            // Full snapshot, not just Indicators: the Structure/SupportResistance/SupplyDemand/
            // Liquidity/Regime groups read swings, zones, pools and regime off the snapshot.
            FeatureVector? vector = features.Update(source, snapshot, trendStates);
            if (vector is null)
                continue;

            var labelled = labeller.Label(ordered, index, vector.LabelAtr);
            if (labelled is null)
                continue;

            rows.Add(new LabeledFeatureRow
            {
                Features = vector,
                Label = labelled.Value.Label,
                LabelExcursionAtr = labelled.Value.ExcursionAtr
            });
        }

        return new ClassifierDataset { Schema = features.Schema, Rows = rows };
    }
}
