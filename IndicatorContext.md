# Derived indicator context

The raw ATR, RSI and Bollinger values remain available through `IndicatorSnapshot`.
Derived context is produced incrementally inside `ChartAnnotator.Indicators` and is
attached to every `AnalysisSnapshot`; strategies may consume it without recalculating
indicator history.

## Bollinger analysis

`BollingerAnalysisSnapshot` exposes:

- normalized bandwidth: `(upper - lower) / |middle| * 100`;
- `%B`: close position between the lower and upper bands;
- bandwidth change over a configurable lookback;
- recent historical percentile;
- `Contracting`, `Stable`, or `Expanding` width direction;
- `Squeeze`, `Narrow`, `Normal`, `Wide`, or `Expansion` regime;
- one-candle `SqueezeReleased` transition.

A squeeze is relative to the instrument/timeframe's own recent history rather than a
fixed pip threshold.

## RSI analysis

`RsiAnalysisSnapshot` exposes the RSI zone, short-term momentum direction, and the
latest swing-confirmed relationship:

- regular bullish divergence: lower price low, higher RSI low;
- regular bearish divergence: higher price high, lower RSI high;
- hidden bullish divergence: higher price low, lower RSI low;
- hidden bearish divergence: lower price high, higher RSI high;
- bullish convergence: price and RSI confirm upward structure;
- bearish convergence: price and RSI confirm downward structure.

Relationships are emitted only after `SwingDetector` confirms the second pivot, so
historical replay and live analysis do not reveal future information.

## ATR analysis

`AtrAnalysisSnapshot` exposes:

- ATR normalized as a percentage of current price;
- change over a configurable lookback;
- `Contracting`, `Stable`, or `Expanding` direction;
- recent historical percentile;
- `VeryLow`, `Low`, `Normal`, `High`, or `VeryHigh` volatility regime.

The normalized value is suitable for comparing volatility across instruments with
different price scales.

## Configuration

All thresholds are configured in `ChartAnnotationOptions`. The important groups are:

- `AtrAnalysis*` and `AtrDirectionThresholdPercent`;
- `RsiMomentum*`, `RsiMinimumDivergenceDifference`,
  `RsiMinimumPriceDifferenceAtr`, and `RsiSignalLifetimeCandles`;
- `BollingerWidth*`, `BollingerSqueezePercentile`, and
  `BollingerWidePercentile`.

The reference strategy was intentionally not changed. These fields are analysis
features; each strategy must explicitly decide how they affect entry, exit, or size.
