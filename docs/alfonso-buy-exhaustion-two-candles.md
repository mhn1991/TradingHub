# Two-candle BB/RSI/CCI buy filter

The opt-in `--alfonso-block-exhausted-buys-5m` rule now checks the latest completed 5m candle **or the immediately preceding consecutive 5m candle**:

```text
(current.high >= current.BBUpper AND (current.RSI > 70 OR current.CCI >= 100))
OR
(previous.high >= previous.BBUpper AND (previous.RSI > 70 OR previous.CCI >= 100))
```

Each candle uses its own indicators; a band touch on one candle cannot combine with an extreme oscillator on the other. The still-forming candle is never used. A missing 5m bar clears the carryover, and duplicate evaluations do not extend it. Per-instrument state records closed-candle results even between 15m entry checks.

This changes only the opt-in buy filter's lookback. New entries still use the 15m cadence; unfilled buy orders are reviewed every 5m; existing pending-guard priority, sell behavior, stops, targets and filled-position handling are unchanged. Journal reasons retain the exact matching candle and its indicator values. The comparison report identifies whether that candle was current or previous.

The original single-candle results remain under their original simulation IDs on the dashboard; they are not results for this change. New replays use a new output directory and publish automatically through `tools/alfonso_lower_alignment_test.sh`.

## Completed Silver replay

Code commit: `34902cf`. All 348 Alfonso tests passed; the Release build passed with no warnings or errors.

The November 24, 2025–July 23, 2026 replay processed the same 253,035 execution candles as the single-candle control (input hash `fb5f4eeeb27bfb331a4baac0bf84081aa416ec615458e2fe61739e92ed7f9fc9`). All other strategy settings, including the prior fixes and pending guard, were retained.

| Rule | Trades | Wins / losses | Net USD |
| --- | ---: | ---: | ---: |
| Single closed candle | 4 | 3 / 1 | +3.89 |
| Current or previous closed candle | 3 | 3 / 0 | +352.49 |

The June 17, 2026 18:00 UTC buy submission was blocked by the previous 17:50–17:55 candle: high 71.39650 >= upper band 71.37248, RSI 70.06917, CCI 152.94977. Its subsequent 18:01 losing fill (-348.20 USD in the control) was removed without replacement fills. The three winning trades retained their entry/stop/target and exit geometry. The July winner's net increased slightly because avoiding the June loss changed equity-based position sizing.

New simulation: `26898729-b84b-4ef2-9dd7-915437ab96c2`. It was automatically published under **Latest completed tests**, labelled `alfonso-buy-exhaustion-two-candles-20260908`. The existing single-candle run and pending-guard control remain available.

Artifacts and validated comparison: `.cache/alfonso-buy-exhaustion-two-candles-20260908/` (`comparison-to-single.json`). This is a small, in-sample result, not evidence of reliable future performance.
