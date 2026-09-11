# Silver: opposing-zone targets versus fixed R

## Rule and scope

Opt-in change `4f2e6d3`: `--alfonso-opposing-zone-target`.
For a buy, take profit at the proximal edge of the nearest supply zone above
the planned entry. For a sell, use the nearest demand zone below it. Select
from the entry timeframe (15m in these tests), not from 1m fill candles or a
higher timeframe. The confirmation candle must have closed by the decision.
Eliminated zones are excluded; surviving fresh, tested and used-up reaction
levels are included, even if they would not qualify for a new entry themselves.

No opposing zone means skip the setup, logged as `NoOpposingZone`. There is no
fixed-R fallback, minimum target distance, 1R cap, or extra target buffer.
The target is frozen when the pending bracket is placed, like the existing
set-and-forget stop. Its proximal/distal prices, base times, confirmation close
and state are recorded in `targetSource` for inspection on the dashboard.

Four full historical Silver runs use no trend fix, price-break invalidation,
confirmed structure, and both fixes. Each uses the same 24 November 2025–23 July
2026 period and 21-day warm-up as the eight completed fixed-R controls. Stops
remain 15m confirmed swings over 48 closed candles; core entries, 1m fills,
sizing, costs and bracket-only management are unchanged. Starting balance is
$100,000 with fixed-fractional 0.5% equity-risk sizing and the same margin caps.

The new arms explicitly use `--minimum-rr 0`: this disables the reward floor,
not the required stop/target geometry or sizing checks. The old controls use
a 0.75R floor. The existing profit-margin entry filter is disabled in **both**
batches (`--alfonso-profit-margin 0`). Other users of the new target option
must deliberately configure these filters if they want to allow nearby zones;
the option alone does not override them. Production defaults remain unchanged.

This is a whole-strategy rerun, not a hindsight replacement of losing trades:
skipped setups and changed exit times can change later entries and quantities.
Results are in-sample and do not establish forward profitability.

## Artifacts and reproduction

- New full replays: `.cache/alfonso-opposing-target-20260907`.
- Fixed-R controls: `.cache/alfonso-structural-stop-20260907-r2` and
  `.cache/alfonso-trend-trades-20260907`.
- Runtime change and runner: `4f2e6d3` (isolated from existing dashboard edits).
- Focused checks before launch: 311 Alfonso and exit/risk tests passed;
  Release BacktestRunner build passed with no warnings or errors.

```bash
dotnet build BacktestRunner -c Release
bash tools/alfonso_opposing_target_ab.sh /path/to/new-results
python3 tools/alfonso_opposing_target_export.py /path/to/new-results
```

The exporter reuses the eight verified controls in
`Dashboard/public/data/backtests/alfonso-trend-trades.json`, validates all four
new completion markers, matching candle hashes/counts, causal target and stop
provenance, CSV decision geometry and net P/L, then exports twelve dashboard
cards. Each includes original candle history at 1m, 15m, 1h and 4h, with up to
200 candles after exit where the available history permits.

## Results

All four replays completed on 7 September 2026. Each processed 253,035 base
candles with identical input hash
`fb5f4eeeb27bfb331a4baac0bf84081aa416ec615458e2fe61739e92ed7f9fc9`.
Amounts below are USD net P/L, not percentages.

| Trend policy | Fixed 0.75R net | Fixed 1R net | Opposing-zone net | Zone wins / trades | Zone profit factor | Zone max drawdown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| No trend fix | -310.91 | -149.11 | -2,914.74 | 6 / 17 | 0.245 | 2,914.74 |
| Price-break invalidation | -188.67 | -358.75 | -1,430.32 | 3 / 10 | 0.408 | 2,069.70 |
| Confirmed structure | +110.44 | -360.43 | +531.85 | 1 / 2 | 2.517 | 350.54 |
| Both trend fixes | -228.30 | -697.47 | +191.76 | 1 / 3 | 1.278 | 690.63 |

This is **not a consistent improvement**. The first two policies deteriorated
substantially; the other two improved on only two and three trades respectively.
Both profitable runs share the same April 16 winning buy, so they are not
independent evidence. That buy targeted supply at 79.71950 and made $882.40.

Targets can be much farther away, not just nearer: planned reward distances
ranged from 0.186R to 24.983R without trend fixes, 0.186R to 6.881R with
price-break invalidation, 2.290R to 2.741R with confirmed structure, and 2.290R
to 6.881R with both fixes. There were eight `NoOpposingZone` candidate
observations in each of the first two runs and none in the others. These are
observations, not eight distinct filled trades removed from each control.

### July 17, 03:21 UTC

The highlighted trade is a **sell**, so its opposing target is demand, not
supply. All four new runs still take this trade and lose at the initial stop.
The decision at 03:00 selected a tested 15m demand zone with proximal
**53.17595**, distal 53.01195, base **27 November 2025, 13:45 UTC**, and
confirmation close 14:30 UTC that day. This is approximately 2.290R from the
55.5465 entry with the unchanged 56.581875 stop.

Price only reached 54.8605 before reversing; the trade exited at 56.5875331875
on 20 July, 01:00 UTC. With both trend fixes, its net loss is $349.50. The
detector did not provide a nearer eligible opposing 15m zone at the decision.
This does **not** prove no chart reaction level existed around 55, or that a
zone identified later was available before entry. It shows the new target
selector alone does not resolve the example. The old surviving zone and absent
nearby target warrant a separate zone-detection/lifecycle investigation before
treating this policy as an improvement. No zone-engine change was bundled here.

### Dashboard and verification

Open `http://localhost:5173/#alfonso-tests` and select
**Silver · opposing-zone targets vs fixed R**. The default card is
**Both trend fixes · opposing zone**; the other three new cards and all eight
fixed-R controls remain selectable. The export contains 104 trades in total
(32 new, 72 controls), including target-zone provenance and chart history.

All four completed outputs passed the exporter's candle-hash, decision,
target/stop causality and net-P/L checks. The exporter regression suite passed
15 tests, in addition to the 311 focused runtime tests before launch. Dashboard
typechecking passed. A browser check verified all twelve cards and trade counts,
target provenance, all four timeframes, 200 post-exit bars for the sampled trade,
zone hide/show controls and access to older comparisons, with no runtime errors.
