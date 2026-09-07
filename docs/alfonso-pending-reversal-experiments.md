# Silver: 5m pending guard and reversal shadow experiments

## Scope and selection

Two opt-in experiments against the completed 15m-direction / independent-5m-confirmation baseline. Silver (`METAL:XAG/USD`), 24 November 2025–23 July 2026, 21-day warmup, 1m execution. Entry zones, structural stops, opposing-zone targets, sizing rules and both existing trend fixes are unchanged. Neither option is enabled by default.

- `b1d2456`: `--alfonso-revalidate-pending-5m`. On each closed 5m candle, cancel a pending Alfonso plan if the 5m trend no longer agrees, or the close breaks the latest confirmed 2/2 swing against it. New entries still require a closed 15m candle. Filled positions remain bracket-owned; partial fills are excluded from the new cancellation path.
- `1a7f7da`: `--alfonso-reversal-shadow-log PATH`. Observe both directions without placing, cancelling or managing trades. This option can run without the pending guard. Cherry-picking this commit alone onto `3fb401b` requires resolving adjacent configuration/agent wiring conflicts; the shadow detector itself does not depend on the guard.

The shadow hypothesis freezes previously confirmed 2/2 swing references at the initial sweep. References must be no older than four hours. It requires a sweep and reclaim, a later close beyond the opposite swing, then a still-later retest that holds that broken level. A new adverse extreme, failed retest or expiry after 12 subsequent bars rejects the setup. This is a fixed first hypothesis, not a rule fitted to the February example.

For each confirmed observation, the log measures the next 12 closed 5m bars from a hypothetical entry at the confirmation close, with the sweep extreme as the risk reference. Labels distinguish 1R-first, stop-first, ambiguous same-bar touches and neither within the horizon. These are **not simulated trades or net returns**: no spread, slippage, commissions, order execution, sizing, opposing-zone target or portfolio constraints are applied. MFE/MAE cover the whole measurement horizon, including after a first-touch label.

## Pending-guard result

| Arm | Trades | Wins | Net USD | Profit factor | Maximum drawdown USD |
| --- | ---: | ---: | ---: | ---: | ---: |
| Baseline | 6 | 4 | -225.20 | 0.676 | 467.20 |
| 5m pending guard | 4 | 3 | +3.89 | 1.011 | 348.20 |

The improvement is **$229.08**, not evidence of a reliable trading edge from four trades in one in-sample instrument.

- February 4: cancelled the buy at **14:35 UTC**, because the actual 5m detector reported `OutOfAlignment`. The original fill was 14:40, followed by a $347.70 loss. This is a warning before the fill, not a retrospective cancellation or a claim to predict every reversal.
- January 30: also cancelled the sell at **10:20 UTC**, before its original 10:28 fill. That removed a $118.68 winner.
- No new fills. The four retained fills have identical sides, entry prices, stops, targets, exit times and exit reasons. Three quantities change slightly because percentage-risk sizing uses the changed account balance.
- June 17's losing buy remains. It filled at 18:01 after the 18:00 order, before another closed 5m review was possible.
- 21 distinct guard cancellation timestamps; 17 fall between 15m entry closes. All recorded guard cancellations cite trend disagreement; this run does not isolate an additional benefit from the explicit swing-close clause. The journal records each cancellation decision twice, so raw log lines must not be counted as independent cancellations.
- Both baseline and guard log 1,951 candidate observations: 1,918 `TooFarToFill` and 33 `Entered`. Here `Entered` means order submission, not a filled trade.

## Completed shadow-only result

All actual trades match the baseline exactly, and the candidate CSV is byte-for-byte identical. Shadow logging therefore leaves this replay's decisions and performance unchanged: six trades, four wins, net -$225.20. All three arms completed the same 253,035 execution candles with matching input hashes.

The detector logged 4,630 sweeps, 523 structure breaks and 181 confirmed observations. All 181 completed their forward measurement: 46 reached hypothetical 1R first, 55 reached the stop reference first, 79 reached neither within 12 bars, and one touched both in the same bar. These diagnostic labels do not establish profitability and do not justify enabling reversal entries. The validator passed exact trade/candidate parity, timestamp causality, stage ordering and P/L reconciliation checks.

## February 5 missed reversal

The shadow detector **does not confirm a buy at 05:50 UTC**:

1. It starts a buy-side sweep at 05:00, using the prior 76.5665 low and 77.9644 opposite swing high.
2. It records reclaim at 05:05.
3. A new lower low invalidates that setup at 05:20.
4. It does not establish a new qualifying sweep/break/retest sequence for the subsequent 05:50 reversal. There is no confirmed buy between 04:00 and 08:00.

Thus the conservative pivot-based first version fails the user's missed-entry example. It should remain logging-only. Loosening it to track developing lows or restart a sweep after successive lows would be a separate experiment, with new false-signal risks; that change was not silently added after seeing this result.

## Verification and reproduction

335 focused Alfonso/risk tests pass; Release runner build passes. Tests cover the opt-in configuration, causal 2/2 pivots, both directions, wick versus close breaks, stale/future/duplicate candles, the pending/filled boundary, ordered reversal stages, expiry, failed retests and ambiguous intrabar outcomes. After decoupling a shadow-only configuration test from the guard, its nine tests also pass. This final test-only change did not change the replayed production code (`2314f87`, retained as final commit `1a7f7da`).

Baseline: `.cache/alfonso-lower-alignment-20260907`, simulation `398a3485-1ef1-44ff-b6fe-e478d71bce21`.

Guard: `.cache/alfonso-pending-5m-20260907-r3`, simulation `a0672756-574f-47ce-8e3d-d459f062fcac`. The runner revision marker is `b1d2456`; its Release binary also contained the then-uncommitted shadow implementation, disabled in this arm. Earlier output directories record a CLI parsing failure and a sandbox permission failure, not completed tests.

Shadow: `.cache/alfonso-reversal-shadow-20260907`, simulation `0daf60d7-f9c1-475e-b05f-68c0193cb4d8`; observations in `reversal-shadow.ndjson`, validated comparison in `comparison.json`.

Reproduce after building `BacktestRunner` in Release, using new output directories:

```bash
bash tools/alfonso_lower_alignment_test.sh .cache/new-guard --alfonso-revalidate-pending-5m
bash tools/alfonso_lower_alignment_test.sh .cache/new-shadow --alfonso-reversal-shadow-log /mnt/storage/TradingHub/.cache/new-shadow/reversal-shadow.ndjson
python3 tools/alfonso_reversal_experiments_report.py .cache/alfonso-lower-alignment-20260907 .cache/alfonso-pending-5m-20260907-r3 .cache/alfonso-reversal-shadow-20260907
```

The report validator requires completed runs, matching input hashes and 253,035 execution candles; checks P/L reconciliation, candidate trend agreement and 15m cadence; compares retained trade geometry; requires exact shadow/baseline trade and candidate-log parity; and validates shadow stage ordering and reference timestamps. No dashboard data or live strategy defaults are changed by these experiments.
