# Simulator Phase 4 validation

Validated on 2026-07-14 with .NET 10 and the repository-locked Dashboard dependencies.

## Outcome

`StructureBasedTradeManager` is now part of the production streamed comparative path. Every `StrategySimulationSession` owns its configured manager and evaluates it only when the configured management interval closes. Recommendations flow through `ExecutionCoordinator.AmendProtectiveStopAsync` and the broker-neutral protective-order contract; the manager itself remains deterministic and side-effect free.

The simulated broker performs atomic stop replacement under one state lock. It validates the current position/stop snapshot, directional risk reduction, executable side, quantity, tick size, amendment identity, and N+1 execution sequence. The target and OCO group survive replacement. A rejected or conflicting amendment leaves the old stop working.

OANDA advertises protective-stop amendment as unsupported in this build. No native atomic mapping has been implemented or certified, and there is deliberately no cancel-first fallback. Historical OANDA/imported candles also remain midpoint data plus configured spread/slippage; historical bid/ask is not claimed.

## Responsibility flow

```text
confirmed immutable analysis snapshot
  -> StructureBasedTradeManager recommendation
  -> StrategySimulationSession conflict/order gate
  -> ExecutionManager broker-state and risk-reduction validation
  -> IProtectiveOrderClient
  -> simulated atomic replacement (or honest OANDA Unsupported)
  -> journal + replay lifecycle event + trade amendment history
  -> Dashboard runtime panel / stepped stop line / performance metrics
```

R, MFE-R, MAE-R, open R, and locked R retain the original entry-to-initial-stop denominator. A current/trailing stop never changes initial risk.

## Baseline before Phase 4

| Check | Result |
| --- | --- |
| `dotnet restore TradingHub.slnx` | passed |
| Release build | passed, 0 warnings, 0 errors |
| Simulator tests | 324 passed |
| Unit tests | 58 passed |
| Broker integration | 3 credential-gated fixtures skipped, no failures |
| Dashboard clean install/typecheck/build | passed; 65 packages; JS 217.83 kB, CSS 37.93 kB |
| Phase 4 trailing-focused tests | 0 |

## Final validation

The following were run in the working tree and again from a newly extracted clean source archive:

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release --no-restore -m:1 -nodeReuse:false
dotnet test TradingHub.slnx -c Release --no-restore --nologo -m:1 -nodeReuse:false

cd Dashboard
rm -rf node_modules dist
npm ci
npm run typecheck
npm run build
```

| Check | Final result |
| --- | --- |
| Release build | passed, 0 warnings, 0 errors |
| Simulator tests | 350 passed, 0 failed, 0 skipped |
| Unit tests | 58 passed, 0 failed, 0 skipped |
| Broker integration | 3 credential-gated fixtures skipped, no failures |
| Phase 4 focused trailing suite | 23 passed |
| Dashboard typecheck | passed |
| Dashboard production build | passed; JS 231.98 kB (70.86 kB gzip), CSS 38.91 kB (8.62 kB gzip) |
| Extracted-archive restore/build/tests/frontend | passed |

The 26-test simulator increase includes 23 focused Phase 4 cases, the strengthened streamed Legacy regression, corrected low-watermark hysteresis diagnostics coverage, and persisted Phase 3 job-snapshot compatibility coverage.

## Correctness coverage

- Long/short break-even below and exactly at threshold.
- Entry/estimated-exit commission, spread, two-sided slippage, and ATR break-even buffer.
- Confirmed swing and support/resistance-zone candidates, candidate precedence, and unconfirmed-swing rejection.
- ATR buffer, minimum ATR/tick improvement, cooldown, stale-snapshot rejection, executable-side validation, and never-widen behavior.
- Optional adverse-structure exit.
- Initial-risk denominator after the current stop has moved.
- Long/short simulator replacement, retained old stop on rejection, duplicate idempotency, conflicting duplicate rejection, and entry-only safety-lock bypass.
- OCO preservation when the target or replacement stop fills.
- Same-frame no-lookahead at 1m, 5s, and 1s.
- Production `StrategySimulationSession` amendment, precise `BreakEvenStop` classification, replay events, and immutable initial stop.
- Sequential versus persistent task-worker lifecycle/stop-history SHA-256 fingerprint equality.
- Legacy no-target entry activity, submitted/filled order assertion, no missing-target R:R rejection, streamed trailing activation, favorable stop movement, and unchanged initial risk.
- Hysteresis prefetch fills beyond `lowWatermark + 1`, remains bounded by capacity, and reports page/source/consumer waits.
- Phase 3 job snapshots without Phase 4 trailing metrics deserialize with zero defaults and survive restart interruption persistence.

## Protective-order and failure behavior

- Broker capability metadata distinguishes native amendment, atomic replacement, and dependent-OCO support.
- ExecutionManager checks exact position ID/instrument/side/quantity and current stop ID/type/side/price before mutation.
- Long prices normalize down to tick; short prices normalize up to tick, retaining conservative protection.
- Entry safety locks do not block risk-reducing amendments or exits.
- Unsupported brokers retain the existing stop and return `Unsupported`/`NotSent`.
- Rejected broker/state validation retains the existing stop and does not trip entry safety.
- Unknown/uncertain execution outcomes trip safety for reconciliation.

## Replay, API, and Dashboard

- Trades are appended to NDJSON with an atomically replaced byte-offset index.
- `/trades` is cursor/limit based and seeks indexed byte ranges instead of rereading all trades.
- SignalR appends the completed trade directly; cursor reconciliation de-duplicates on simulation, strategy, setup, and close time.
- Execution detail is indexed by strategy/setup and retains configurable pre-entry rows, every pending/open execution row, and configurable post-exit rows.
- Replay records explicit setup, signal, order, fill, position, amendment, activation, stop/target, close, and completion events.
- The chart draws a discrete stepped stop path with accepted/rejected markers and amendment tooltips.
- Runtime shows entry, immutable initial stop, current stop, target, current/maximum/locked R, mode, last action/reason, and next management close.
- Legacy and Improved have separate controls/defaults and performance metrics for activations, amendment outcomes, locked R, giveback, and trailing exits.
- Imported CSVs use opaque server IDs, streamed validation, interval matching, metadata, seven-day expiry, listing/reuse, and deletion. Client-supplied server paths are rejected.

## Job state and workers

One owned channel per running job orders source progress, engine progress, status/control changes, trade notifications, and terminal state. Its reducer alone advances the snapshot revision, publishes ordered SignalR state, and submits coalesced persistence. Terminal state is flushed and awaited. Constructor/progress paths no longer use `.GetAwaiter().GetResult()`.

The misleading `DedicatedThread` compatibility option was removed from enum/API/CLI/Dashboard/docs. Parallel execution uses persistent bounded task workers; sequential mode remains available.

## Deterministic benchmark

The benchmark uses generated midpoint candles, two isolated deterministic strategy sessions, parallel workers, 1m management, bounded replay, and the same input in disabled/enabled runs. Cache bytes are zero because input is generated inline. Replay output size and total engine time were measured; replay-only wall time is not separately instrumented. Enabled/disabled runs share the same input hash for each interval.

| Data shape | Period | Trailing | Frames | Frames/s | Analysis/s | Mgmt eval | Amend accepted/rejected | Barrier ms | Peak working set | Allocated | GC 0/1/2 | Replay bytes | Runtime |
| --- | ---: | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- | ---: | ---: |
| 1m | 31 days | off | 44,640 | 8,174 | 8,174 | 8 | 0 / 0 | 82.8 | 137.7 MB | 1.12 GB | 124/38/13 | 12,043,962 | 5.461 s |
| 1m | 31 days | on | 44,640 | 16,579 | 16,579 | 8 | 2 / 0 | 46.6 | 136.5 MB | 1.09 GB | 120/37/12 | 12,047,972 | 2.693 s |
| OANDA-native 5s shape | 31 days | off | 535,680 | 72,168 | 6,014 | 8 | 0 / 0 | 54.0 | 175.2 MB | 6.78 GB | 709/179/35 | 13,511,399 | 7.423 s |
| OANDA-native 5s shape | 31 days | on | 535,680 | 49,240 | 4,103 | 8 | 2 / 0 | 80.8 | 174.4 MB | 6.92 GB | 723/179/36 | 13,515,436 | 10.879 s |
| imported 1s shape | 1 day | off | 86,400 | 27,638 | 461 | 8 | 0 / 0 | 6.9 | 101.2 MB | 1.08 GB | 111/12/5 | 502,009 | 3.126 s |
| imported 1s shape | 1 day | on | 86,400 | 28,826 | 480 | 8 | 2 / 0 | 6.7 | 103.3 MB | 1.08 GB | 111/14/5 | 506,088 | 2.997 s |

Run-order/JIT/filesystem effects make the small enabled/off timing differences non-causal; this table does not claim that trailing improves throughput. It demonstrates that management evaluated eight times for the deliberately short two-position lifecycle, not on every execution second. A full-month 1s run (2,678,400 frames) exceeded the execution environment's single-process limit before completion, so the completed 1s result is reported honestly as a one-day deterministic sample. Full-month 1m and 5s runs completed.

The production CLI adds `--trailing-comparison`, which runs actual Legacy and Improved once with trailing disabled and once with their configured policies. `trailing-comparison.json` contains net P/L, drawdown, profit factor, win rate, average/median R, MFE/MAE/capture, exact exit counts, amendments, locked R, and MFE giveback.

## Search and packaging audit

The unfinished-code scan found no `TODO`, `FIXME`, or `NotImplementedException`. Remaining `NotSupportedException` sites are intentional validation for unsupported agent actions, sources, broker JSON types, IG operations, or test-helper intervals.

The workspace contains an existing ignored local `.env.integration`; it was not opened or modified and is excluded from the source archive. The archive preserves `.env.example` and `.env.integration.example`. Archive listing checks found no `.git`, `.idea`, `.cache`, `bin`, `obj`, `node_modules`, `dist`, simulation output, or non-example `.env` files.

Clean source archive: `TradingHub_Phase4_Source.zip`.

## Validation not claimed

- Live OANDA amendment was not exercised because it is intentionally unsupported.
- Credential-gated broker integration fixtures were not run.
- Historical bid/ask was not enabled.
- The full interactive 20-step browser acceptance scenario was not manually performed; production frontend compilation, API/replay contracts, streamed integration tests, and deterministic chart data paths were validated automatically.
