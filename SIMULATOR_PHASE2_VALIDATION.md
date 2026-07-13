# Simulator Phase 2 validation

Date: 2026-07-13

## Baseline (before Phase 2 code changes)

| Metric | Value |
| --- | --- |
| Build | Success, 0 warnings |
| Simulator.Tests | 290 passed |
| TradingHub.UnitTests | 58 passed |
| Integration tests | 13 skipped (env-controlled) |
| Dashboard build | Success (vue-tsc + vite) |

### Defects confirmed in audit

1. First uncached OANDA run wrote full cache before yielding candles.
2. `PrefetchLowWatermark` was unused.
3. Parallel mode used per-frame `Task.WhenAll`; dedicated-thread mode spawned LongRunning tasks per frame.
4. Dashboard Simulator polled only; playback speed/pause were non-functional.
5. `/api/simulations/{id}/replay` materialised all chunks unbounded.
6. Progress persistence used unowned `_ = SaveAsync(...)`.
7. Completed jobs remained in `_running`.
8. `ManualResetEventSlim` blocked threads on pause.
9. `UseHistoricalBidAsk` defaulted true without bid/ask fills.
10. `NearestToOpenFirst` mapped to stop-first.
11. `.env.integration` contained real-looking credentials.

## After Phase 2

### Defects fixed

| # | Fix |
| --- | --- |
| 1 | Stream-to-cache: yield while appending; atomic commit |
| 2 | `LowWatermarkPrefetchStream` + `IPagedHistoricalCandleSource` |
| 3 | `StrategyWorkerHost` persistent workers (task or one dedicated thread per strategy) |
| 4 | SignalR composable + functional `useSimulationPlayback` + progressive chunks |
| 5 | Bounded replay API with `limit`/`startSequence`/`cursor` (max 5000) |
| 6 | `CoalescingJobSnapshotStore` + revision monotonicity |
| 7 | Terminal jobs removed from `_running`; completion via TCS |
| 8 | `AsyncPauseGate` frame-boundary pause |
| 9 | `UseHistoricalBidAsk` default false; UI discloses not enabled |
| 10 | `OcoFillPolicy.NearestToOpenFirst` with distance sort |
| 11 | `.env.integration` removed; `.env.integration.example` placeholders only |

### Commands run

```bash
dotnet build TradingHub.slnx -c Release
dotnet test Simulator.Tests/Simulator.Tests.csproj -c Release
# + Phase2StreamingAndWorkersTests after rebuild

cd Dashboard
rm -rf node_modules dist
npm ci
npm run typecheck
npm run build
```

### Benchmark notes

Synthetic determinism checks (3k and 800 one-minute candles) confirm sequential vs parallel vs dedicated-thread matching balances/trade counts/input hashes in unit tests.

Full one-month cached wall-clock and peak-memory measurements require a machine-local run with cached OANDA data; live OANDA was not exercised in this environment (no credentials after secret purge).

Record local numbers here when available:

| Scenario | Time to first frame | Total duration | Candles/sec | Peak memory | Replay size |
| --- | --- | --- | --- | --- | --- |
| Baseline uncached first-run | (full download first) | — | — | — | — |
| Phase2 uncached first-run | first page | — | — | — | — |
| Sequential one-month cached | — | — | — | — | — |
| Parallel tasks one-month | — | — | — | — | — |
| Dedicated threads one-month | — | — | — | — | — |

## Remaining limitations

- Historical bid/ask OANDA components not implemented (option disabled honestly).
- Event-driven strategy lifecycle chunks / annotation deltas partially deferred (market OHLC chunks + trades API progressive).
- Full AnalysisChart trade-marker integration in Simulator panel still lightweight (OHLC playback + comparison table).
- Stage-level allocation profiler not fully instrumented across ChartAnnotator internals.
- One-year live OANDA acceptance scenario not run here.

## Security

- Credentials must be rotated if `.env.integration` was ever shared.
- Only `.env.example` / `.env.integration.example` remain as templates.
