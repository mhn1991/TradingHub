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
