# Continuous Alfonso trend comparison

Scope: trend detection only. No new entry, stop, target or position-management rules.

Four arms, ordered consistently in the audit and dashboard:

1. Baseline (both new trend flags off).
2. `InvalidateOnPriceStructureBreak` only — commit `55ece75`.
3. `RequireConfirmedTrendStructure` only — commit `bdb1b00`.
4. Both flags.

The experimental lower-reversal entry policy (`91c4323`) remains **off**.

## Reproduce

```sh
dotnet build BacktestRunner -c Release
bash tools/alfonso_trend_audit.sh /absolute/new/output-directory
```

The script starts one process each for Gold, Silver, NAS100/US100 (one instrument, not two),
US30, GBPJPY and EURUSD. Optional trailing names limit the markets, e.g. `gold silver`.
It uses the original 2025-11-24 through 2026-07-23 evaluation window, 21-day simulator
warm-up, existing cached 1m data, 4h/1h/15m analysis and unchanged baseline entry settings.
It refuses an existing output directory. Logs and raw audits stay under that directory.
Only after every requested simulation succeeds does it export the dashboard dataset.

Manual re-export:

```sh
python3 tools/alfonso_trend_export.py /absolute/output-directory
```

Open **Simulator → Trend comparison**, or `http://localhost:5173/#alfonso-trends` when
the development dashboard is running. Choose a market/timeframe, jump to a UTC instant,
click a candle to read all four reasons, or step through disagreements/reversal references.
Saved Alfonso tests remains the separate, earlier stop-distance trade comparison.

## Why shadows, not separate trade simulations?

The live baseline supplies the exact closed bar it just consumed to three independent
timeframe analyzers. Each shadow owns its entire zone/trend state (trendline breaks can
affect zones). Nothing feeds back into the live agent. These are four trend configurations,
but only the baseline places orders. There is deliberately no four-arm P&L comparison.

This avoids the previously documented disagreement from rebuilding candles in an external
replay. Observations happen before the agent's open-position early return and are deduplicated
by its existing per-timeframe candle guard. The dataset includes all actual agent observations,
not just entry decisions. The audit rejects enabled trend fixes on the live baseline rather
than silently mislabeling its output.

The simulator's warm-up does not imply Alfonso's internal detector received all warm-up bars:
the simulator does not evaluate agents on warm-up frames. The audit preserves that behavior
and records initial Unknown states; it does not silently seed historical structure.

## Evaluation boundaries

- Directional coverage and label changes describe behavior; becoming neutral more often is
  not itself evidence of better detection.
- Forward agreement checks direction against the close four observed candles later. Flat
  outcomes and incomplete future windows are excluded. Each arm's denominator is shown.
- The same-bar subset uses only candles where all four arms are directional, reducing the
  different-coverage comparison problem. It can still be a selected subset of the market.
- Reversal reference: a close outside the previous 12 candles' high/low range, opposite the
  last breakout direction. Recognition is measured through 12 further candles, stopping
  before another opposite reference. Tail events without 12 later samples are excluded.
- Recognition medians include detected events only. Misses remain visible in detected/total
  counts; a faster median with far fewer detections is not automatically an improvement.
- These are descriptive price-reference checks, **not ground-truth trend accuracy**, independent
  statistical trials, or evidence of tradable returns. Forward prices are used only after the
  run for evaluation. Gaps are counted in observed candles, not elapsed clock hours.

## Verification

```sh
dotnet test Simulator.Tests --filter FullyQualifiedName~Alfonso
python3 -m unittest discover -s tools -p test_alfonso_trend_export.py
cd Dashboard
npm run build
```

The audit tests check all four arms against independently configured analyzers across 1,000
causal candles on each of the three timeframes, unchanged baseline state, request wiring,
and observation/deduplication while a position is open. Export tests check neutral handling,
shared denominators, recognition misses/delays and rejection of unclosed candles.
