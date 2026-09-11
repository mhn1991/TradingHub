# Silver: trend fixes in actual trading replays

## Scope

These are actual order-producing historical simulations, not the earlier shadow
trend-label audit. They are not live or forward tests.

Six new runs use each trend fix separately and both together, at 0.75R and 1R.
Two completed structural-stop controls are reused with both trend flags off.
All arms use Silver, 24 November 2025–23 July 2026, 21 warm-up days, the same
cached candles, 1m fills, 4h/1h/15m analysis, core entries, structural 15m stops
with a 48-candle lookback, and a 0.75R minimum-reward gate. Initial balance is
$100,000. Trailing, mechanical protection, partial exits and other position
reductions remain disabled. Defaults and entry logic were not changed.

- Price-break invalidation (`55ece75`): `--alfonso-invalidate-price-structure`.
- Confirmed structure (`bdb1b00`): `--alfonso-confirm-trend-structure`.
- Both: the two flags together. The lower-reversal entry experiment remains off.

Confirmed structure uses zone-derived peaks/valleys, not arbitrary chart pivots.
It requires both rising or both falling series alongside accomplishment evidence
to establish/reverse a trend. Price-break invalidation can neutralize a stale
trend without immediately establishing the opposite direction. Neither flag
is a direct instruction to enter on a reversal.

New artifacts: `.cache/alfonso-trend-trades-20260907` (code revision `a9abd95`).
Controls: `.cache/alfonso-structural-stop-20260907-r2/structural-{0.75,1}r`.
New runs include candidate CSV logs for matching each filled trade to its
recorded decision. Controls retain their saved explanations; missing scores
are not reconstructed or invented.

## Completed results

All eight arms completed with the same input hash
`fb5f4eeeb27bfb331a4baac0bf84081aa416ec615458e2fe61739e92ed7f9fc9`
and 253,035 processed base candles. Values below are USD, net of recorded fees.

| Trend policy | Target | Trades | Winners | Net P/L | Profit factor | Max drawdown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| No trend fix | 0.75R | 18 | 10 | -310.91 | 0.892 | 1,139.95 |
| No trend fix | 1R | 18 | 9 | -149.11 | 0.954 | 1,400.36 |
| Price-break invalidation | 0.75R | 11 | 6 | -188.67 | 0.892 | 1,132.53 |
| Price-break invalidation | 1R | 11 | 5 | -358.75 | 0.828 | 1,737.82 |
| Confirmed structure | 0.75R | 3 | 2 | +110.44 | 1.316 | 349.50 |
| Confirmed structure | 1R | 3 | 1 | -360.43 | 0.470 | 679.58 |
| Both | 0.75R | 4 | 2 | -228.30 | 0.668 | 688.23 |
| Both | 1R | 4 | 1 | -697.47 | 0.314 | 1,016.62 |

Every fixed arm removes the disputed December 23, 2025 15:45 UTC sell signal
(filled 16:55 UTC), which remains in both controls. Avoiding this stale-trend
entry does not produce consistently better whole-window performance:

- Price-break at 0.75R reduces the dollar loss, but profit factor and average R
  are effectively unchanged; it takes fewer trades. At 1R it worsens both net
  P/L and maximum drawdown versus its corresponding control.
- Confirmed structure at 0.75R is the only profitable arm, but three trades
  cannot establish a robust improvement. Its 1R counterpart loses money.
- Combining the flags is not better than confirmed structure alone in this
  sample. At 1R it has the largest net loss of these eight arms.

No experimental flag has been promoted to the default. The next useful review
is the new/remaining entries and missed opportunities alongside the existing
continuous trend audit, rather than selecting a winner from these small samples.

Verification: 291 Alfonso regression tests passed; Release BacktestRunner build
had zero warnings/errors. Sixteen dashboard export tests passed. Saved outcomes,
trade counts, input parity, unamended bracket R geometry and causal pivot
confirmation were checked across all eight arms. The export contains 72 trades;
all 36 new-arm trades matched a unique recorded candidate decision.

## Reproduce

```sh
dotnet build BacktestRunner -c Release --no-restore
bash tools/alfonso_trend_trade_ab.sh /absolute/new/output-directory
python3 tools/alfonso_trend_trade_validate.py /absolute/new/output-directory .cache/alfonso-structural-stop-20260907-r2
python3 tools/alfonso_trend_trade_export.py /absolute/new/output-directory .cache/alfonso-structural-stop-20260907-r2
```

Dashboard: `http://localhost:5173/#alfonso-tests`, comparison
**Silver · trend fixes with structural stops**. Older comparisons are retained.

## Interpretation boundaries

Fewer trades or avoiding a particular losing trade does not prove improved
trend recognition. P/L also depends on unchanged entry/exit rules and which
orders occupy the account. The 0.75R and 1R arms need not have identical entries:
different exit times can change which later setups are eligible. This is a
single instrument, already-reviewed historical window, not independent
validation or grounds to promote an experimental flag to the default.
