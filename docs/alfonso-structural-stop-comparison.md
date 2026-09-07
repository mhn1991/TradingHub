# Alfonso structural-stop and fractional-target experiment

## Scope and policy

Silver (METAL:XAG/USD), 2025-11-24 through 2026-07-23, with 21 warm-up days.
The execution chart is 15m; fills are simulated on 1m candles. The original
4h/1h/15m trend settings and core entry rules are unchanged. This is an opt-in
experiment, not a change to the default strategy or a validated live-trading policy.

`--alfonso-structural-stop --alfonso-stop-lookback 48` selects the highest
confirmed swing high for sells, or lowest confirmed swing low for buys, among
the previous 48 closed execution candles. A swing requires two strictly lower
highs / higher lows on each side. Thus its confirmation requires two subsequent
closed candles; no future candles enter the decision. Equal-price pivots prefer
the most recent confirmed anchor.

This deliberately selects the window's extreme, **not simply the most recent minor
pivot**. Protection cannot move inside the original zone. Padding remains 25% of
the original zone width. With no confirmed swing, the zone stop is retained and
the fallback is recorded in `stopSource`. Anchor timestamps identify candle opens.
48 candles is an initial hypothesis motivated by the reviewed Silver example;
it is not an optimized or independently validated lookback.

Targets use the actual planned entry-to-stop distance. `--alfonso-reward 0.75`
or `--alfonso-reward 1` selects the proposed target. Also specify
`--minimum-rr 0.75`: the simulator's existing 1.5R minimum otherwise rejects
these orders. The experiment lowers only this reward gate, not the cash-risk
limits. All six arms use the same 0.75R gate; 3R arms are diagnostic controls.
Risk-based sizing and fixed bracket exits remain enabled, without trailing or
partial exits. The original `--alfonso-profit-margin 0` setting is retained;
the separate opposing-zone margin filter still uses zone geometry when enabled.

## Completed Silver results

All six runs completed on the same 253,035 input candles. Net P/L and maximum
drawdown below are in USD, starting from a $100,000 account.

| Stop policy | Target | Trades | Winners | Net P/L | Profit factor | Max drawdown |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Original zone | 3R | 19 | 3 (15.8%) | -2,958.69 | 0.490 | 3,246.88 |
| Original zone | 0.75R | 19 | 8 (42.1%) | -2,194.83 | 0.467 | 2,713.91 |
| Original zone | 1R | 19 | 8 (42.1%) | -1,544.17 | 0.625 | 2,633.34 |
| Structural 15m | 3R | 7 | 2 (28.6%) | -39.26 | 0.978 | 1,478.96 |
| Structural 15m | 0.75R | 18 | 10 (55.6%) | -310.91 | 0.892 | 1,139.95 |
| Structural 15m | 1R | 18 | 9 (50.0%) | -149.11 | 0.954 | 1,400.36 |

The stop change improves this sample materially. Of the requested small targets,
1R has the smaller loss, while 0.75R has the lower drawdown and higher win rate.
**None of the six variants is profitable over the full window.** Reducing the
target alone does not resolve the original stop-placement problem.

The structural 3R control is not directly comparable by net P/L alone: a short
opened on January 30 stayed open until July 23 and was liquidated at the end of
the simulation for +$767.81, not at its target. It therefore contains only seven
of the original 19 signals. The two smaller-target structural arms each contain
18 original signals and no new ones. All filled structural trades have confirmed
15m anchors; none uses the fallback.

### First Silver trade

All variants sell at 65.99550 on December 17, 2025 at 14:57 UTC. The structural
anchor is the **05:45-open / 06:00-close** 15m candle, high 66.53510. This is the
extreme around the user's 06:00 reference; the 06:00-open candle is a different
candle. Adding the existing zone-width padding places the stop at 66.5583625.

| Variant | Stop | Target | Quantity | Exit UTC | Net P/L USD |
| --- | ---: | ---: | ---: | --- | ---: |
| Original zone, 3R | 66.1118125 | 65.6465625 | 2,648 | 14:59 stop | -332.50 |
| Structural, 0.75R | 66.5583625 | 65.573353125 | 604 | 15:20 target | +253.39 |
| Structural, 1R | 66.5583625 | 65.4326375 | 604 | 15:54 target | +338.38 |

Position size decreases with the wider stop: planned cash risk stays approximately
$350 (original $349.94 versus structural $349.54). These are actual replay exits,
not hypothetical target-touch calculations. The structural 3R version of this
first trade still stops out later, at 16:59, for -$345.59.

## Reproduction and verification

```bash
dotnet build BacktestRunner -c Release
bash tools/alfonso_structural_stop_ab.sh /absolute/path/to/new-results
```

The script refuses to overwrite an existing directory. Each variant runs in an
independent process and produces `simulation-result.json`, `manifest.json`,
`improved-progressive.json` and detailed simulation records.

Corrected run directory: `.cache/alfonso-structural-stop-20260907-r2`.
Earlier `.cache/alfonso-structural-stop-20260906` contains an exact 19-trade
baseline reproduction, but its fractional-target arms were blocked by the 1.5R
gate and structural arms failed CLI parsing. Those arms are not performance evidence.

291 focused Alfonso tests pass. Release build: zero warnings/errors. CLI flag
preflight also passes. Unit coverage includes closed-bar confirmation, lookback
expiry, duplicate/old bars, buy/sell symmetry, fallback, zone protection, fractional
target geometry, and retention of cash-risk limits after lowering the reward gate.

Completed-artifact checks also verify every bracket's requested reward multiple,
unchanged fixed stops, precise target labels, and anchor confirmation before the
signal. The corrected zone-3R control reproduces all 19 original trades exactly
in direction, signal/fill/exit times, prices, quantity and net P/L. All six share
input hash `fb5f4eeeb27bfb331a4baac0bf84081aa416ec615458e2fe61739e92ed7f9fc9`.

## Commits

- `5d91245`: opt-in structural-stop implementation and regression tests.
- `37a67b2`: required CLI boolean-flag registration for the stop option.
- `7c908ae`: fractional-target geometry tests, precise target labels, comparison runner.
- `34f47ad`: reward-gate experiment configuration, risk-limit regression tests and runner preflight.

The stop feature needs both stop commits. Fractional targets already existed as
configuration; target-only experiments can use the original zone policy with
`--alfonso-reward 0.75 --minimum-rr 0.75`, independently of enabling structural stops.

## Interpretation limits

Wider stops and closer targets can change holding duration and subsequent order
availability even when trend and entry rules are identical. The full strategy
runs are therefore not guaranteed to contain identical trade lists. This window
was used to identify the problem, so results are in-sample and require separate
instrument and out-of-sample testing before considering default promotion.
