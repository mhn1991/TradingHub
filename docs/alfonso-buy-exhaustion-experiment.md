# Silver: closed-5m BB/RSI/CCI buy filter

## Rule and scope

Feature commit: `edaff34`. Opt-in flag: `--alfonso-block-exhausted-buys-5m`.

On the latest completed 5m candle:

```text
high >= Bollinger upper AND (RSI > 70 OR CCI >= 100)
```

Uses the existing warmed engine indicators: Bollinger 20 periods / 2 population standard deviations, Wilder RSI 14, Lambert CCI 20. Indicators include the just-closed candle. A missing value cannot satisfy its threshold; either available oscillator may satisfy the OR clause. Future, delayed and stale snapshots cannot trigger the filter.

The condition blocks otherwise eligible new buys at the existing 15m entry cadence and cancels still-unfilled pending buys at each closed 5m review. It does not directly filter sells, reverse direction, cancel partial fills, or manage filled positions. Existing pending trend/structure checks retain priority when both cancellation rules trigger. No cooldown is added: a later eligible 15m setup can submit again after the condition clears. Order availability and later opportunities can therefore change indirectly.

This is a testable buy-exhaustion hypothesis, not proof that price will reverse. A fill before the next completed 5m candle cannot be prevented retrospectively.

## Reproduction

Silver (`METAL:XAG/USD`), 24 November 2025–23 July 2026, 21-day warmup, 1m execution. Control is the completed `.cache/alfonso-pending-5m-20260907-r3` replay, including both trend fixes, 15m/5m alignment, structural stops, opposing-zone targets and 5m pending revalidation. Only the new filter is added.

```bash
dotnet test Simulator.Tests --no-restore --filter FullyQualifiedName~Alfonso -v quiet
dotnet build BacktestRunner -c Release --no-restore -v quiet
bash tools/alfonso_lower_alignment_test.sh .cache/alfonso-buy-exhaustion-20260908-r2 \
  --alfonso-revalidate-pending-5m --alfonso-block-exhausted-buys-5m
python3 tools/alfonso_buy_exhaustion_report.py \
  .cache/alfonso-pending-5m-20260907-r3 .cache/alfonso-buy-exhaustion-20260908-r2
```

The replay script refuses existing output directories; choose a new directory to rerun. The initial unsuffixed output directory contains only a failed sandbox launch, not a completed result.

Validation: 342 focused Alfonso tests pass, including 17 new threshold, missing-value, causality, option-wiring and pending-order tests. Release build succeeds with zero warnings/errors. The comparison validator checks completion markers, trade/P&L reconciliation, matching inputs and execution assumptions, aligned 15m/5m candidate timestamps, and exact threshold/closed-candle evidence for every exhaustion decision. Duplicate journal event types are not counted as separate actions.

## Replay result

| Arm | Trades | Wins / losses | Net USD | Profit factor | Maximum drawdown USD |
| --- | ---: | ---: | ---: | ---: | ---: |
| Existing pending guard | 4 | 3 / 1 | +3.89 | 1.011 | 348.20 |
| Pending guard + buy filter | 4 | 3 / 1 | +3.89 | 1.011 | 348.20 |

**No incremental performance improvement.** All four trade records are exactly equal, including quantity, brackets, execution times, excursions and net P/L. No fills were added or removed. March 27 (+164.31), May 7 (+64.05), June 17 (-348.20), and July 20 (+123.73) remain.

The filter did activate:

| Decision time UTC | Action | Closed candle high | BB upper | RSI | CCI |
| --- | --- | ---: | ---: | ---: | ---: |
| February 4, 14:00 | Block submission | 91.94350 | 91.61620 | 65.99 | 192.83 |
| February 25, 06:00 | Block submission | 91.25675 | 91.07140 | 71.24 | 149.21 |
| February 25, 08:20 | Cancel pending buy | 91.02035 | 90.78051 | 67.64 | 321.14 |
| April 14, 07:15 | Block submission | 77.91700 | 77.74907 | 71.72 | 192.31 |

These indicator values come from the engine's decision journal. February 4 was already prevented from filling by the existing pending guard; blocking its initial submission earlier adds no further realised saving. Candidate logs show 3 `BuyExhaustion`, 32 `Entered` (submissions, not fills), and 1,960 `TooFarToFill`, versus control's 33 submissions and 1,918 distance rejections. Do not subtract three from 33 and assume 30 submissions: later opportunities can be reconsidered after a block/cancellation.

### Why June 17 still lost

The order was submitted at 18:00 UTC and filled at 18:01. On the last completed 5m candle (17:55–18:00), an independent recalculation from the same full cached warmup history gives approximately:

- High **71.35800**, upper BB **71.44081**: the band-touch requirement is **false**.
- RSI **70.52**, CCI **124.40**: the oscillator requirement is **true**.
- Combined condition is false, so the rule correctly permits the buy. A later 5m warning cannot retrospectively prevent the 18:01 fill.

The recalculation uses complete UTC-aligned 5m buckets from cached 1m data, the same warmup start, and the chart's BB/RSI/CCI formulas; its values for blocked decisions agree with the engine journal to rounding. The non-blocked June values are reconstructed, not newly logged engine diagnostics.

The completed replay processed **253,035** execution candles with input hash `fb5f4eeeb27bfb331a4baac0bf84081aa416ec615458e2fe61739e92ed7f9fc9`, identical to control. New simulation: `f8bd4eda-f795-43ee-8651-ce810c746d66`; control: `a0672756-574f-47ce-8e3d-d459f062fcac`. Detailed validated output is `.cache/alfonso-buy-exhaustion-20260908-r2/comparison.json`.

Keep this optional: this one-instrument, four-trade, in-sample replay establishes correct rule execution, not a reversal predictor or a performance advantage. Thresholds were not changed after seeing the result.

## Dashboard publication

The filtered run and pending-guard control are published under **Saved Alfonso tests → Latest completed tests** (`/#alfonso-tests`). Each has four identical filled trades. The original comparisons remain available in the comparison selector.

`tools/alfonso_lower_alignment_test.sh` now automatically validates and publishes successful runs to `Dashboard/public/data/backtests/alfonso-latest-tests.json`. Run labels default to the experiment directory name. The archive keeps previous runs, deduplicates by simulation ID, and uses a lock plus atomic replacement for concurrent test processes. Failed/incomplete results are not published. Click **Refresh tests** on an already-open dashboard; no rerun is needed.

To retry a failed export or publish an existing completed run with a friendly label:

```bash
python3 tools/alfonso_publish_completed.py .cache/alfonso-buy-exhaustion-20260908-r2 --label 'Silver · BB/RSI/CCI buy filter + pending guard'
```

This automation applies to the lower-alignment shell runner, not arbitrary direct `dotnet` launches or other experiment scripts. A separately deployed static dashboard still needs its updated public data deployed.
