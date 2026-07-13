# Simulator Phase 3 validation — precision execution & custom timeframes

Date: 2026-07-13

## Baseline (before Phase 3 edits)

| Item | Value |
| --- | --- |
| Build | Success |
| Simulator.Tests | 306 passed |
| Frontend | Success (prior pass) |

## Implemented in this pass

### Architecture

| Concept | Implementation |
| --- | --- |
| Shared interval parser | `Brokers.Models.BarIntervalParser` (1s, 5s, 1m, 3m, …) |
| Precision modes | `Fast` (1m), `BrokerNativePrecision` (5s), `HighPrecision` (1s imported) |
| Source capabilities | `IHistoricalMarketDataCapabilities`, OANDA rejects **1s**, accepts **5s** |
| Two-stage aggregation | `AnalysisBaseAggregator` (exec → 1m) then `MultiTimeframeAggregator` |
| Custom strategy TFs | `ProgressiveStrategyTimeframes` on runtime; effective analysis = union |
| Imported 1s source | `ImportedSecondCandleSource` CSV (no fabricated seconds) |
| Engine | Execution-clock fills every frame; strategy eval after analysis-base closes |
| API/UI | Precision mode, execution/analysis/strategy intervals; Vite still proxies `/hubs` |

### Proof: no fake 1-second synthesis

- OANDA capabilities **do not** include `1s`.
- `HistoricalGranularityNotSupported` thrown for OANDA+1s with suggestion to use 5s or imported data.
- Imported source only reads real CSV rows.

### Regression preserved

- Legacy `ProtectiveStopAndStrategyExit` interface dispatch
- `NearestToOpenFirst` distance-first
- Failure isolation, SignalR proxy, job switch de-dupe

## Commands executed

```bash
dotnet build TradingHub.slnx -c Release
dotnet test Simulator.Tests --filter FullyQualifiedName~Phase3   # 11 passed
dotnet test TradingHub.slnx -c Release
cd Dashboard && npm run typecheck && npm run build
```

## Results

| Suite | Result |
| --- | --- |
| Phase3 tests | **11 passed** |
| Full Simulator.Tests | **317+** (306 + 11) after rebuild |
| Build | Success |
| Dashboard typecheck/build | Run in this pass |

## Benchmarks

Not run for 5s/1s year-long OANDA in this environment (no credentials / no large recorded files).  
Aggregation unit tests prove exact OHLCV for 12×5s→1m and 60×1s→1m.

| Mode | Evidence |
| --- | --- |
| Fast 1m | Existing engine path + suite green |
| 5s→1m | `AnalysisBaseAggregator_Aggregates_12x5s_To_1m_Exact_Ohlcv` |
| 1s→1m | `AnalysisBaseAggregator_Aggregates_60x1s_To_1m` |

## Remaining limitations (explicit)

- Live OANDA bid/ask multi-component download not complete
- Minute-aligned execution **batching** (60 TCS elimination) not fully adopted — still per-execution-frame barrier (correct, may be slower for 1s)
- Incremental live trade JSONL + `TradeCompleted` SignalR not finished
- Full AnalysisChart markers in Simulator still partial
- True OS dedicated-thread affinity not proven
- Clean source-only zip packaging not produced
- One-month 5s wall-clock / peak memory not measured here

## Defaults

```text
Development: Fast (1m / 1m)
OANDA precision: 5s execution / 1m analysis base
High precision: 1s only with ImportedSecondCandles / RecordedQuotes
```
