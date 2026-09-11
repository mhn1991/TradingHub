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

## Results: 6 September 2026

Full-window engine runs are preserved in `.cache/alfonso-trend-audit-20260906-r3`,
using audit revision `24ededd`. All six reached the requested end date. Runner summary
finalization then failed because it constructed the strategy a second time and attempted
to recreate the already-existing audit file. Commit `63b2b5f` fixes that lifecycle issue
by reusing the executed agents' instrument metadata; it does not change trend detection.

The dashboard export was explicitly recovered from completed engine replays:

```sh
python3 tools/alfonso_trend_export.py .cache/alfonso-trend-audit-20260906-r3 --completed-replay
```

Recovery requires a completed manifest and marker, a full-window performance result, and
no strategy failure artifact. It retains the original failed runner job status and displays
a recovery note. These are completed engine results, not six successful runner finalizations.
The exporter tests reject incomplete, early-ending and failed replays.

Against the original baseline, all six input hashes, processed candle counts, financial
totals and all 114 trades' execution/economic fields match. The only observed trade-text
difference is culture-dependent spacing before `%` in `stopSource`. All 342 saved entry-time
trend labels (three per trade) also match the continuous audit baseline.

### Cross-market findings

The 4h reversal-reference recognition counts are below. These are descriptive checks,
not a ground-truth accuracy score; the definition and limitations above apply.

| Instrument | Baseline | Price break | Confirmed | Both |
| --- | ---: | ---: | ---: | ---: |
| Gold | 13/21 | 13/21 | 3/21 | 3/21 |
| Silver | 16/25 | 15/25 | 4/25 | 4/25 |
| NAS100 / US100 | 19/29 | 17/29 | 4/29 | 3/29 |
| US30 | 16/27 | 13/27 | 6/27 | 4/27 |
| GBPJPY | 18/34 | 17/34 | 8/34 | 8/34 |
| EURUSD | 16/28 | 14/28 | 2/28 | 2/28 |

- Strict confirmation greatly reduces directional coverage: on 4h, baseline coverage is
  58–72%, confirmed-only 11–30%, and both 9–23%. It often substitutes neutral states for
  direction rather than recognizing the new trend. The 1h and 15m comparisons show the
  same broad reduction in reference-event recognition.
- Price-break invalidation removes specific stale directions but does not consistently
  improve forward agreement or reversal recognition across markets. Effects are path-dependent:
  US30's baseline is unusually neutral on 1h/15m (15%/13% coverage); price-break-only instead
  raises coverage to 52%/53% and reference recognition from 18/123 to 74/123 on 1h and
  63/527 to 325/527 on 15m. It is not simply a universal neutralizing filter.
- Neither flag, nor their combination, is established as a general trend-detection fix.
  Keep them opt-in. This comparison does not change entries or demonstrate profitability.

### Gold examples to inspect in the dashboard

- **5 January 2026, 14:30 UTC (second trade's signal):** baseline 4h is Down, while
  price-break-only is out of alignment after a close of 4421.790 broke the confirmed
  swing at 4404.375. The 15m baseline is Down but price-break-only is Up. The filter
  removes the stale higher-timeframe bearish label without establishing a new 4h Up trend.
- **29 January 2026, 08:00 UTC:** baseline 4h remains Down; price-break-only is out of
  alignment; confirmed-only and both are Unknown. All four 1h states are Up, but all
  four 15m states are neutral. The stale 4h direction is visible, but neither proposed
  change fully recognizes the bullish alignment or proves the missed entry is recovered.

Verification: 274 targeted simulator tests and five exporter tests passed. Browser checks
covered all 18 instrument/timeframe selections, four reason cards, causal UTC jumps,
history-boundary notices, disagreement/reference navigation, candle selection and visible
chart dimensions. The comparison component fits a 390px mobile screen; the existing global
top navigation still overflows on mobile and was left outside this trend-only change.
