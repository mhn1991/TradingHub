# TradingHub streamed simulator architecture

## Dashboard-first workflow

1. Open the Dashboard and select **Simulator**.
2. Configure instrument, range, strategies, costs, warm-up, and execution mode.
3. Click **Run Simulation**.
4. `POST /api/simulations` returns a `simulationId` immediately.
5. The UI polls `GET /api/simulations/{id}` (and can subscribe to SignalR `/hubs/simulations`) for progress.
6. Visual playback is independent of backend compute pause/resume.

CLI and Dashboard both call `IBacktestApplicationService`.

```text
Dashboard API ─┐
               ├── BacktestApplicationService
CLI ───────────┘
```

## Project boundaries

| Project | Responsibility |
| --- | --- |
| Brokers / OANDA | Historical and live market-data retrieval |
| Simulator | Historical clock, MarketFrame production, fills, jobs, orchestration |
| ChartAnnotator | Indicators, swings, structure, annotation snapshots |
| Agent | Multi-timeframe setup progression and trade intent |
| RiskManager / ExecutionManager | Risk approval and order lifecycle |
| DashboardLive | Simulation API, job queue host, SignalR |
| Dashboard | Configuration, progress, playback, comparison UI |
| BacktestRunner | Thin CLI adapter over the application service |

## Application service and jobs

- `IBacktestApplicationService` starts, queries, pauses, resumes, and cancels jobs.
- `FileSimulationJobRepository` persists snapshots under `.cache/simulation-jobs`.
- A bounded `Channel` job queue owns background workers (no fire-and-forget).
- Refresh-safe: status, progress, errors, and output paths survive browser reload.

## One-minute canonical clock

Simulation advances on completed 1m candles only:

```text
Read 1m candle
  → process existing orders/fills (per strategy worker)
  → aggregate 5m/15m/1h
  → update shared annotation snapshots
  → build immutable MarketFrame
  → evaluate strategies (sequential or parallel barrier)
  → create orders eligible from next candle
  → write compact replay events
```

No lookahead: orders created on frame `N` fill no earlier than frame `N+1`.

## Streaming source, cache, prefetch

- `IHistoricalCandleStream` yields `MarketCandle` without materialising a full year.
- `OandaStreamingCandleSource` pages OANDA, de-duplicates boundaries, retries transient failures, and writes/reads a versioned compressed cache.
- `PrefetchingCandleStream` uses a bounded channel with absolute sequences (never overwrites unread data).
- `MarketDataQualityTracker` reports duplicates, gaps, weekend vs session gaps, and an input hash.

## Shared analysis and parallel workers

- Default `AnalysisSharingMode.SharedImmutableSnapshots` computes annotations once per closed interval.
- Each strategy owns isolated account/order/position/risk/journal state (`StrategySimulationSession`).
- `StrategyExecutionMode.Sequential` and `ParallelWorkers` process the same `MarketFrame`.
- Parallel mode awaits every worker before advancing (`Task.WhenAll` barrier). Sequence mismatches throw.

## Warm-up

- Stream starts at `From - WarmupDays`.
- During warm-up: aggregate and annotate only.
- Trading and result collection begin at the requested evaluation start.

## Replay output

```text
simulations/{simulationId}/
  manifest.json
  market/chunk-*.json.gz
  strategies/{id}/events-*.json.gz
  strategies/{id}/trades.json.gz
  strategies/{id}/performance.json.gz
  COMPLETE | INCOMPLETE
```

A single ordered writer commits after the per-frame barrier.

## API surface

```text
POST   /api/simulations
GET    /api/simulations
GET    /api/simulations/{id}
POST   /api/simulations/{id}/pause|resume|cancel
GET    /api/simulations/{id}/strategies|trades|performance
GET    /api/simulations/{id}/replay[?from=&to=]
GET    /api/simulations/{id}/replay/chunks[/{chunkId}]
SignalR /hubs/simulations
```

## Correctness principles

- No lookahead
- One chronological base-candle clock
- No unread ring-buffer overwrite
- Same immutable frame for all strategies
- Independent strategy state
- Deterministic per-frame barrier
- No fabricated missing candles
- No credentials in repository/output
- Correctness over raw concurrency

## Phase 3 — precision execution & custom timeframes

- **Execution interval** (fills/stops/OCO) is separate from **analysis base** (default 1m for ChartAnnotator).
- **Precision modes:** Fast `1m`, OANDA native `5s`, High `1s` only with imported/recorded data.
- **OANDA does not support 1s** historical candles in this stack; requests fail with `HistoricalGranularityNotSupported`.
- **Two-stage aggregation:** execution → analysis base → multi-TF (no fabricated seconds).
- **Custom strategy stack:** trend / confirmation / entry intervals; effective analysis = requested ∪ strategy requirements ∪ analysis base.
- Shared parser: `BarIntervalParser`.

## Final corrective notes (Legacy exit mode)

Legacy must declare `ExitManagementMode` through `ITradingAgent` as
`ProtectiveStopAndStrategyExit`. There is **no** default interface implementation
returning Bracket — that incorrectly forced R:R/take-profit requirements and
blocked Legacy entries with `TakeProfitPrice = null`.

## Phase 2 additions

- **True first-run streaming**: OANDA pages yield to the simulator while a temporary cache is appended; commit is atomic.
- **Low-watermark prefetch**: `IPagedHistoricalCandleSource` + `LowWatermarkPrefetchStream`.
- **Persistent strategy workers**: `StrategyWorkerHost` (task or one dedicated thread per strategy for the whole run).
- **Async frame-boundary pause**: `AsyncPauseGate`.
- **Coalesced job persistence** with monotonic `Revision`.
- **Completed job cleanup** from in-memory `_running`; CLI awaits completion TCS.
- **Bounded replay API** (`limit`/`startSequence`/`cursor`, max 5000).
- **SignalR client** in Dashboard with polling fallback; functional playback composable.
- **NearestToOpenFirst** OCO policy; historical bid/ask option disabled until implemented.

See `SIMULATOR_PHASE2_VALIDATION.md`.

## Known limitations

- Historical bid/ask OANDA components not implemented (option off / documented).
- Full AnalysisChart trade-marker integration in Simulator panel is still lightweight.
- Event-driven strategy lifecycle / annotation delta chunks are partial (OHLC chunks + progressive trades API).
- Stage-level allocation profiling across ChartAnnotator internals is incomplete.
- `Simulator` is not fully AOT-compatible due to JSON job/cache/replay I/O.
