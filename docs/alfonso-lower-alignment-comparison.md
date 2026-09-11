# First lower-timeframe alignment test

Completed 7 September 2026. Runtime/test/runner commit: `3fb401b`.

## Scope

One Silver (METAL:XAG/USD) replay, 24 November 2025–23 July 2026, 21-day warmup, 1-minute fills. The new `--alfonso-entry-policy lower-aligned` policy requires a directional 15m trend and matching independently calculated 5m trend. Unknown, out-of-alignment, or conflicting trends cannot admit an order.

The agent consumes every closed 5m candle; orders and pending-order reviews remain on 15m closes. Agreement is checked at decision time, not continuously at each 1m fill. Both existing trend-detector fixes remain enabled. Higher trends no longer select direction or impose scenario-specific nesting. Higher-timeframe zone/range filters remain; control agreement is disabled in both arms as before. This is a replacement direction/scenario policy, not a pure additional filter on the original matrix.

15m entry zones, 48-candle structural stops, opposing-zone targets, sizing and execution settings are unchanged. No new higher-timeframe reversal/proximity warning rules are included. The default policy remains Core.

## Results

| Policy | Trades | Wins | Net USD | Profit factor | Max drawdown USD |
| --- | ---: | ---: | ---: | ---: | ---: |
| Previous: both trend fixes + opposing-zone targets | 3 | 1 | +191.76 | 1.278 | 690.63 |
| New: 15m direction + 5m agreement | 6 | 4 | -225.20 | 0.676 | 467.20 |

The new policy returned $416.96 less despite a higher win rate and lower drawdown. Four winners totalled $469.32; two losers totalled $694.51. This small in-sample test does not demonstrate improved trend recognition or profitability.

| New fill (UTC, 2026) | Side | Recorded 15m / 5m at order decision | Exit | Net USD |
| --- | --- | --- | --- | ---: |
| Jan 30 10:28 | Sell | Downtrend / Downtrend | Target | +118.68 |
| Feb 04 14:40 | Buy | Uptrend / Uptrend | Stop | -347.70 |
| Mar 27 19:47 | Buy | Uptrend / Uptrend | Target | +163.26 |
| May 07 13:14 | Buy | Uptrend / Uptrend | Target | +64.05 |
| Jun 17 18:01 | Buy | Uptrend / Uptrend | Stop | -346.82 |
| Jul 20 12:01 | Buy | Uptrend / Uptrend | Target | +123.33 |

None of the three previous control trades is present in the new run, including the July 17 03:21 sell and the April 16 winning buy. All three control decisions recorded 15m OutOfAlignment, which the new rule disallows. Avoiding the July loss alone is not evidence of improvement: the large April winner was also missed. January's new trade still fills on January 30, so this result does not resolve the earlier January 29 entry-timing concern.

## Reproducibility and checks

- New simulation: `398a3485-1ef1-44ff-b6fe-e478d71bce21`.
- Control: `37af51da-b2a4-411f-abd4-60ab23cd1c2c` (reused completed `both-zone` run).
- Same input hash: `fb5f4eeeb27bfb331a4baac0bf84081aa416ec615458e2fe61739e92ed7f9fc9`; 253,035 test candles.
- New outputs: `.cache/alfonso-lower-alignment-20260907`.
- 317 focused Alfonso/risk tests pass; Release runner build passes.
- 21 exporter tests, dashboard typecheck and trade-drawing test pass.
- Export validates completion, identical input, every logged candidate's direction/agreement/current 5m close and 15m decision cadence, causal stop/target provenance, trade matching and P/L totals.
- 1,951 candidate observations: 1,918 TooFarToFill, 33 Entered (order placement, not filled trades). Repeated observations are not unique opportunities; scenario refusals are not included in this CSV.

## Dashboard

Open `http://localhost:5173/#alfonso-tests`, comparison **Silver · 15m direction with 5m confirmation**. New and control trades include 4h, 1h, 15m, 5m and 1m candle views, existing drawing/zone controls, and post-exit context. Only the new run has recorded 5m trend labels; the control's 5m candles do not imply historical confirmation was calculated then.

Export command: `python3 tools/alfonso_lower_alignment_export.py .cache/alfonso-lower-alignment-20260907`.

Next review should focus on whether the recorded 15m/5m direction at each decision matches visible structure, especially the two losing buys. No further strategy changes were made based on these results.
