# Price Action, Warm-up Calibration, and Diagnostics

## Implemented price-action evidence

`ChartAnnotator.PriceAction.PriceActionAnalyzer` runs only on completed analysis candles and emits deterministic evidence for:

- bullish/bearish break of structure (close-confirmed, ATR margin);
- bullish/bearish change of character;
- stateful break-and-retest confirmation;
- rejection at confirmed swings or DBSCAN zones;
- calibrated displacement candles;
- observable liquidity sweeps of confirmed swings;
- Bollinger compression-to-expansion breakouts;
- confirmed impulse-leg measurements.

Each event contains its confirmation time/sequence, reference level, ATR, confidence, reason code, and explanation. Rejected candidates are also recorded in `PriceActionSnapshot.Diagnostics`, for example:

- `BreakNotClosed`;
- `RetestNotReached`;
- `RetestTooDeep`;
- `RejectionAwayFromStructure`;
- `CalibrationNotReady`;
- `DisplacementBodyTooSmall`;
- `NoConfirmedSweep`;
- `ExpansionDidNotBreakEnvelope`.

This allows the system to distinguish “the detector did not run” from “the detector ran and correctly rejected the candidate.”

## Agent integration

The progressive strategies support three modes:

- `Disabled`: price action does not affect entry decisions;
- `Soft` (default): aligned evidence adjusts confidence and strong opposing evidence can reject a setup;
- `Required`: entry waits for a confirmed trigger such as a retest, rejection, displacement, or compression breakout.

The Dashboard exposes the mode, minimum confidence, and opposing-evidence filter. `Soft` is the recommended research default because it avoids making every correlated signal mandatory.

## ADX/DMI

The shared indicator snapshot now includes incremental Wilder ADX, +DI, and -DI. Structure supplies direction; ADX/DMI supplies directional-strength context. It is included in confidence scoring but is not treated as an entry trigger by itself.

## Timeframe-specific warm-up calibration

Each `ChartKey` (instrument + analysis interval) owns its own price-action analyzer and calibration history. Warm-up collects bounded distributions such as:

- candle body / ATR;
- candle range / ATR;
- wick/body ratio;
- 70th/90th-percentile range measurements.

At the first non-warm-up execution frame, the simulator freezes every analyzer’s profile. Evaluation candles cannot alter those thresholds. This avoids lookahead and gives every timeframe its own scale.

ATR is already calculated independently per timeframe. A multiplier such as `0.25 ATR` therefore produces a different absolute buffer on 1m, 5m, 2h, and daily data. Outcome-sensitive parameters such as break-even R and structure-activation R are intentionally not auto-optimised during the evaluation period; they should be selected by walk-forward calibration and then frozen.

## Trade-management diagnostics

Every management-interval evaluation now emits `TradeManagementEvaluated`, including:

- initial/current stop;
- proposed stop, where applicable;
- open profit R;
- locked profit R;
- analysis interval and snapshot version;
- action (`Hold`, `MoveStop`, or `Exit`);
- explanation.

Stop-amendment request/accept/reject events remain separate. Together, these events show whether trailing ran, why it held, which structural candidate it selected, and whether ExecutionManager applied the amendment.

## Chart

Replay frames include price-action snapshots. The chart displays significant bullish/bearish price-action markers and hover information includes ADX and the current price-action bias. The normal chart stays at the analysis-base interval; sub-minute execution detail remains scoped around trades.

## Validation discipline

For strategy comparison, keep separate windows:

1. warm-up/calibration;
2. walk-forward evaluation;
3. final untouched holdout.

Compare trailing enabled/disabled and price-action `Disabled`/`Soft`/`Required` using net P/L, drawdown, profit factor, average R, MFE captured, giveback from MFE, and exit distribution. Do not optimise thresholds on the final holdout.
