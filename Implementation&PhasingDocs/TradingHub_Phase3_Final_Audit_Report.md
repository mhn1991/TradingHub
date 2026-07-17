# TradingHub Phase 3 Final Work Audit

## Scope

This audit reviewed the uploaded `TradingHub(2).zip` against the Phase 3 precision-execution and custom-timeframe implementation brief.

The review covered:

- execution interval versus analysis-base separation;
- OANDA 5-second precision mode;
- imported 1-second data;
- custom strategy and analysis intervals;
- two-stage aggregation;
- strategy execution and failure handling;
- caching and low-watermark prefetch;
- live trades and replay;
- SignalR and Vue Simulator mode;
- performance and memory design;
- tests, validation, and packaging.

The following frontend validation was independently run:

```bash
cd Dashboard
rm -rf node_modules dist
npm ci
npm run typecheck
npm run build
```

Result:

- npm install: success;
- Vue/TypeScript typecheck: success;
- Vite production build: success;
- npm audit: 0 vulnerabilities.

The audit environment does not include the .NET SDK, so the C# build and test claims could not be independently rerun.

---

# Executive conclusion

The Phase 3 implementation contains the main structural additions:

- execution interval separated from analysis base;
- OANDA 5-second capability;
- OANDA 1-second rejection;
- imported 1-second candles;
- two-stage aggregation;
- custom trend/confirmation/entry intervals;
- shared interval parser in the API/domain;
- Legacy strategy fix retained;
- `NearestToOpenFirst` retained;
- SignalR Vite proxy retained.

However, the precision mode is not yet production-complete.

The biggest remaining problems are:

1. High-precision mode cannot be started successfully from the Dashboard.
2. Imported 1-second capability incorrectly claims bid/ask support while parsing midpoint-only rows.
3. The CLI was not modernised for Phase 3 and still hard-codes the old timeframe requirements.
4. Production low-watermark prefetch still does nothing.
5. Every 1s/5s execution candle still creates a full strategy barrier and replay row.
6. Live trades are still unavailable until final completion.
7. The Simulator still does not render the real chart.
8. Missing sub-minute candles can produce partial 1-minute candles marked complete.
9. Progress persistence and SignalR publication still contain fire-and-forget paths.
10. Cache cleanup and full cache integrity verification remain incomplete.
11. Fill-model metadata is incorrect.
12. The final ZIP is not source-only.

The code is suitable for continued development and 1-minute testing, but the 1-second/5-second modes need another focused pass before they should be trusted for large backtests.

---

# What is correctly implemented

## 1. Execution and analysis intervals are separated

`BacktestRuntimeOptions` now includes:

```text
ExecutionInterval
AnalysisBaseInterval
AnalysisIntervals
```

`SimulationTimeframeOptions` validates that the execution interval is no coarser than the analysis base and that fixed intervals divide cleanly.

This is the correct direction.

## 2. OANDA 5-second capability exists

The OANDA mapping supports:

```text
S5
S10
S15
S30
```

The capability registry rejects OANDA 1-second requests and accepts OANDA 5-second requests.

No code was found that fabricates 1-second candles from 1-minute candles.

## 3. Two-stage aggregation exists

The engine uses:

```text
execution candle
    ↓
AnalysisBaseAggregator
    ↓
completed analysis-base candle
    ↓
MultiTimeframeAggregator
```

Chart annotation runs only after analysis candles close, not on every 1-second or 5-second candle.

## 4. Custom strategy intervals are wired in the Dashboard/API path

`ProgressiveStrategyTimeframes` supports:

```text
TrendInterval
ConfirmationInterval
EntryInterval
```

The application service passes them into both Legacy and Improved strategies.

The engine unions strategy-required intervals with configured analysis intervals.

## 5. Important earlier fixes remain

The repository preserves:

- Legacy no-target/no-R:R risk handling;
- explicit `ExitManagementMode`;
- `NearestToOpenFirst` distance-first behaviour;
- per-strategy workers;
- strategy-failure isolation;
- async pause gate;
- SignalR Vite proxy;
- replay sequence de-duplication when switching jobs.

---

# Critical findings

## 1. Dashboard High Precision mode is currently unusable

### Files

- `Dashboard/src/components/SimulatorPanel.vue`
- `DashboardLive/SimulationApi.cs`
- `Simulator/Models/BacktestConfiguration.cs`

### Current behaviour

Selecting `HighPrecision` changes:

```text
ExecutionInterval = 1s
SourceKind = ImportedSecondCandles
```

But the Dashboard:

- has no imported-file selector;
- has no upload endpoint;
- does not send `importedCandlePath`;
- does not display a source selector.

The backend requires:

```csharp
ImportedCandlePath != null
File.Exists(ImportedCandlePath)
```

Therefore, a normal Dashboard High Precision request always fails validation.

### Additional design issue

A browser cannot safely provide a local filesystem path that the server can access.

The correct Dashboard design requires:

- a server-side file import/upload endpoint;
- selecting an already imported dataset;
- or a configured server data directory.

A plain path textbox is not sufficient for a remote or containerised host.

---

## 2. Imported 1-second source falsely advertises historical bid/ask

### Files

- `Simulator/MarketData/HistoricalMarketDataCapabilities.cs`
- `Simulator/MarketData/ImportedSecondCandleSource.cs`
- `Simulator/MarketData/MarketCandle.cs`

`ImportedSecondCandleCapabilities` says:

```csharp
SupportsHistoricalBidAsk => true
```

But the CSV parser accepts only:

```text
timestamp,open,high,low,close[,volume]
```

and creates:

```csharp
MarketCandle.FromMid(...)
```

It does not parse:

- bid OHLC;
- ask OHLC;
- quote events.

### Consequence

The capability metadata is misleading.

Change it to `false` until the source actually preserves bid/ask, or implement the documented bid/ask file format.

---

## 3. `RecordedQuotes` is not implemented

`HistoricalDataSourceKind.RecordedQuotes` maps to `ImportedSecondCandleCapabilities`, and the application service creates `ImportedSecondCandleSource`.

There is no quote-event parser or quote-to-1s aggregator.

Therefore, `RecordedQuotes` is currently only an alias for midpoint OHLC CSV, not a recorded quote source.

Remove/disable it or implement it honestly.

---

## 4. CLI Phase 3 support is incomplete

### File

`BacktestRunner/BacktestCommandOptions.cs`

The CLI still:

- has no `PrecisionMode`;
- has no `SourceKind`;
- has no `ImportedCandlePath`;
- has no `AnalysisBaseInterval`;
- has no custom trend interval;
- has no custom confirmation interval;
- has no custom entry interval;
- requires exactly `5m`, `15m`, and `1h`;
- uses its own regex parser rather than the shared `BarIntervalParser`.

### Consequences

The CLI cannot run:

```text
OANDA 5s execution / 1m analysis
imported 1s data
2h → 30m → 5m strategy
3m custom entry
```

without source changes.

This contradicts the requirement that Dashboard and CLI expose the same application-service capabilities.

---

## 5. Production low-watermark prefetch is still unused

### File

`Simulator/MarketData/PrefetchingCandleStream.cs`

The implementation calculates:

```csharp
long unread = writeSequence - readSequence;
```

then discards it:

```csharp
_ = unread;
_ = _lowWatermark;
```

The bounded channel gives backpressure, but `PrefetchLowWatermark` has no effect on production OANDA page acquisition.

### Consequence

The option remains a placebo for the normal OANDA stream.

Low-watermark logic exists only in the separate pure-paged path used mainly by fakes/tests.

The correct production design must coordinate:

- page acquisition;
- cache writing;
- source progress;
- low-watermark demand;
- bounded output.

---

## 6. Sub-minute mode still performs one strategy barrier per execution candle

### File

`Simulator/Engine/StreamingComparativeEngine.cs`

Every 1-second or 5-second candle creates:

- a `MarketFrame`;
- worker enqueue operations;
- one completion source per strategy;
- a full cross-strategy barrier;
- replay processing.

Minute-aligned `ExecutionBatch` processing was not implemented.

### Performance consequence

A 5-second run has approximately 12 times as many execution frames as 1-minute mode.

A 1-second run has approximately 60 times as many execution frames.

At the previously observed 1-minute rate of roughly 133 frames/sec, the same per-frame cost would imply approximately:

```text
one year at 5s: around 9–10 hours
one year at 1s: around 45–50 hours
```

before accounting for larger replay output and data-source overhead.

These are rough estimates based on the observed application rate, not measured Phase 3 benchmarks.

---

## 7. Replay output writes every execution candle

### File

`Simulator/Replay/ChunkedReplayWriter.cs`

The market replay writes one row for every execution frame.

In 5-second/1-second mode, this creates a full sub-minute replay for the entire requested period.

The writer also writes one strategy state row per strategy per execution frame.

### Consequences

- very large replay output;
- high compression and disk cost;
- many replay chunks;
- unnecessary browser network traffic;
- mostly duplicated balance/equity values;
- poor scalability for year-long 1s/5s runs.

The requested design was:

```text
main chart replay: 1m+
execution detail: only around trades by default
```

That has not been implemented.

---

## 8. Live trades are still not live

### Files

- `Simulator/Replay/ChunkedReplayWriter.cs`
- `DashboardLive/SimulationApi.cs`
- `Dashboard/src/components/SimulatorPanel.vue`

Completed trades are accumulated in memory:

```csharp
state.Trades.Add(trade)
```

but `trades.json.gz` is written only in `CompleteAsync`.

The API reads only `trades.json.gz`.

### Consequence

The Dashboard can show:

```text
41 completed trades
```

in strategy progress while still showing:

```text
Live trades (0 payloads)
```

The statement “Completed trades stream while the simulation is still running” is still false.

There is also no `TradeCompleted` SignalR event.

---

## 9. The Simulator still has no real chart

### File

`Dashboard/src/components/SimulatorPanel.vue`

The playback panel displays text:

```text
sequence
time
OHLC
warm-up
```

It does not use `AnalysisChart.vue`.

It does not render:

- candlesticks;
- setup markers;
- confirmation markers;
- signal markers;
- entries;
- stops;
- targets;
- exits;
- trade paths;
- swings;
- zones;
- trendlines;
- channels;
- structure.

This remains one of the largest user-facing gaps.

---

## 10. Missing sub-minute candles can create a partial minute marked complete

### Files

- `ChartAnnotator/MarketData/MultiTimeframeAggregator.cs`
- `Simulator/MarketData/AnalysisBaseAggregator.cs`
- `Simulator/MarketData/MarketDataQualityTracker.cs`

With `ResetIncompleteBuckets`, a gap resets the current bucket.

If the next execution candle is still inside the same 1-minute bucket, aggregation starts again using the later candle but preserves the bucket’s original minute start.

At the minute boundary, this partial candle is emitted with:

```csharp
IsComplete = true
```

even though some 1s/5s members were missing.

`MarketDataQualityTracker.RecordIncompleteAggregate()` is never called anywhere.

### Consequence

ChartAnnotator and strategies may consume partial 1-minute OHLC as a complete analysis candle.

The system must either:

- reject the incomplete minute;
- mark it incomplete and suppress strategy analysis;
- or apply a clearly documented source-specific rule.

It must not silently mark partial data complete.

---

# High-priority findings

## 11. Progress persistence is still fire-and-forget

### Files

- `Simulator/Services/BacktestApplicationService.cs`
- `Simulator/Jobs/CoalescingJobSnapshotStore.cs`

Progress callbacks still call:

```csharp
_ = PersistAsync(...)
```

The snapshot store starts a `Task.Run` for every non-terminal publication attempt.

Source progress and engine progress can mutate `job.Snapshot` concurrently.

### Risks

- revision races;
- lost fields;
- unnecessary task creation;
- unobserved non-terminal persistence failures;
- stale in-memory state.

A single ordered job-state reducer/persistence actor is still needed.

---

## 12. SignalR publication is fire-and-forget

### File

`DashboardLive/SimulationRealtimeBridge.cs`

The bridge uses:

```csharp
_ = _publisher.PublishAsync(snapshot);
```

SignalR publication failures are not observed.

This is less severe than simulation correctness, but it weakens the live-dashboard reliability.

---

## 13. Dedicated-thread mode still does not guarantee thread affinity

### File

`Simulator/Engine/StrategyWorkerHost.cs`

The implementation starts an async method through:

```csharp
TaskCreationOptions.LongRunning
```

After an incomplete `await`, continuations can resume on a thread-pool thread.

Therefore, it is not proven that one OS thread processes the complete strategy run.

Rename/remove the mode or implement a genuinely synchronous dedicated worker loop.

---

## 14. Cache writer lifecycle is unsafe on early cancellation/enumerator disposal

### File

`Simulator/MarketData/OandaStreamingCandleSource.cs`

The cache writer is not owned by a comprehensive `try/catch/finally`.

If:

- the consumer stops enumeration early;
- cancellation occurs during a yield;
- OANDA paging throws;
- the engine fails while consuming;

the temporary cache writer may not be aborted/disposed immediately.

This can leave:

- open handles;
- stale `.tmp` files;
- incomplete gzip streams.

---

## 15. Cache validation is not full integrity validation

### File

`Simulator/MarketData/StreamingCacheWriter.cs`

Manifest validation checks metadata, then reads only the first decompressed line.

It does not verify:

- full gzip completion;
- full content hash;
- candle count;
- first timestamp;
- last timestamp;
- chronological order;
- duplicate rows.

A cache truncated after the first row can pass initial validation.

---

## 16. Cache hash algorithms are inconsistent

`OandaStreamingCandleSource` builds the committed manifest hash from a custom fingerprint:

```text
openTime|open|high|low|close
```

`StreamingCandleCacheWriter` independently hashes the serialised row JSON, including different fields.

The writer’s own hash is not used for commit.

If full cache validation is later added against the writer’s row stream, the current manifest hash may not match.

Use one canonical fingerprint implementation for:

- source stream;
- cache writer;
- cache validation;
- input hash, where appropriate.

---

## 17. Fill-model result metadata is incorrect

### File

`Simulator/Engine/StreamingComparativeEngine.cs`

The result currently returns:

```csharp
UseHistoricalBidAsk
    ? MidpointPlusConfiguredSpread
    : SyntheticSpreadModel
```

This is semantically reversed/incomplete.

The engine never returns:

```text
HistoricalBidAsk
```

and the normal OANDA midpoint-plus-spread run is labelled `SyntheticSpreadModel`.

Until bid/ask is implemented, normal OANDA results should be labelled:

```text
MidpointPlusConfiguredSpread
```

---

## 18. Capability errors can become HTTP 500

### File

`DashboardLive/SimulationApi.cs`

`POST /api/simulations` catches `ArgumentException`.

`HistoricalGranularityNotSupportedException` inherits directly from `Exception`, not `ArgumentException`.

An unsupported request such as OANDA + 1s can therefore become a server error instead of a structured 400 response.

Return problem details containing:

- error code;
- source;
- requested interval;
- supported intervals;
- suggested alternative.

---

## 19. Pause/resume terminal-state errors can become HTTP 500

The pause/resume endpoints catch only `KeyNotFoundException`.

`InvalidOperationException` for completed or non-running jobs is not mapped to HTTP 409.

---

## 20. Source progress page count is always zero

`OandaStreamingCandleSource.RaiseProgress` receives a page count but discards it.

`CandleDownloadProgress` has no page-count property.

`BacktestApplicationService` therefore sets:

```csharp
PagesRead = 0
```

for every source-progress update.

---

## 21. Long closures can truncate OANDA paging

`DownloadPagesAsync` exits after more than eight consecutive empty pages.

For 5-second pages, this covers only a limited closed-market duration.

A long holiday, unusual closure, or a requested range beginning well before market reopening can cause the source to stop before `request.To`.

Empty pages should advance until the end range or a source-defined terminal condition, not an arbitrary count alone.

---

## 22. OANDA client lifetime is not disposed per job

`CreateDefaultStream` creates a new OANDA broker client and returns only its market-data client through `OandaStreamingCandleSource`.

The owning broker object is not retained or disposed.

Repeated simulation jobs can leak broker/network resources.

Use an owned source wrapper implementing `IAsyncDisposable`, or inject a process-lifetime broker client explicitly.

---

# Medium-priority findings

## 23. Phase 3 source capability registry contains misleading mappings

- `RecordedQuotes` maps to imported candle capabilities.
- `BinanceCandles` maps to inline-test capabilities.
- `InlineTestData` without actual `InlineCandles` falls through to an OANDA source.
- `BinanceCandles` passes capability checks but is later rejected as not wired.

Capabilities and source construction should agree exactly.

---

## 24. Deterministic seed remains unused

`DeterministicSeed` is accepted by:

- Dashboard;
- API;
- CLI;
- runtime options;

but is not consumed by strategy, engine, source, or analysis construction.

Remove it or use it for actual stochastic components.

---

## 25. Input identity is incomplete

The code has:

```text
SimulationId
InputRequestId
InputHash
```

but not a separate `SimulationConfigurationId`.

`InputRequestId` includes `AnalysisBaseInterval`, which does not change the downloaded candle data, while the strategy/risk configuration is not separately fingerprinted.

The final manifest also omits several reproducibility fields:

- source kind;
- precision mode;
- analysis base;
- strategy timeframes;
- spread;
- slippage;
- commission;
- ambiguity policy.

---

## 26. MFE and MAE are not implemented

`SimulatedTradeRecord` contains no MFE/MAE fields.

The strategy session does not update excursion statistics while a position is open.

This is particularly important for evaluating whether 5s/1s precision materially improves execution analysis.

---

## 27. Rich performance metrics are incomplete in Simulator mode

The runtime comparison shows:

- balance;
- equity;
- net P/L;
- trades;
- open positions.

It does not show:

- win rate;
- profit factor;
- average/median R;
- maximum drawdown;
- expectancy;
- average holding time;
- exit distribution;
- MFE/MAE.

---

## 28. Strategy replay remains per-frame state, not lifecycle events

There are no event types such as:

```text
SetupCreated
SetupAdvanced
SignalCreated
RiskRejected
OrderFilled
PositionOpened
StopUpdated
PositionClosed
TradeCompleted
```

The current event chunk is mostly repeated numeric state.

This prevents detailed setup and trade visualisation.

---

## 29. No replay chunk metadata index

Chunks are discovered by enumerating filenames.

There is no descriptor containing:

- first/last sequence;
- first/last time;
- row count;
- compressed size;
- hash;
- completion state.

Range requests repeatedly read chunks from the beginning until enough rows are found.

---

## 30. Replay loading still has unnecessary network behaviour

The browser retains only 2,000 rows, which is good for memory.

However, it still marks and loads every newly created execution chunk during the full job.

In 1s/5s mode, that means substantial network traffic merely to discard older rows.

The client should request:

- analysis replay;
- a current bounded tail;
- or a selected range.

---

## 31. High-precision imported source lacks data-quality validation

The imported parser silently skips malformed rows.

It does not explicitly validate:

- OHLC invariants;
- interval alignment;
- duplicate timestamps;
- ordering;
- bid/ask schema;
- source metadata;
- file hash.

Some problems will later be observed by other components, but import should produce a clear validation report rather than silently dropping rows.

---

## 32. Tests do not prove the complete Phase 3 workflow

The new Phase 3 tests verify:

- parser behaviour;
- capability flags;
- exact 5s→1m aggregation;
- exact 1s→1m aggregation;
- timeframe union.

They do not prove:

- Dashboard High Precision import;
- full 5s engine run;
- full 1s imported engine run;
- no-lookahead in sub-minute mode;
- intraminute stop/target ordering;
- missing-second behaviour;
- cache round-trip at 5s;
- live trade delivery;
- real chart replay;
- batching equivalence;
- performance/memory limits.

The Legacy synthetic test also does not strongly assert that a trade was opened; it calculates `anyTradeActivity` but never asserts it.

The manual Legacy run demonstrates activity, but the automated regression test remains weak.

---

# Packaging audit

The ZIP does not contain normal .NET `bin`/`obj` build output, which is an improvement.

However, it still contains approximately:

```text
1,765 node_modules entries
721 dist entries
14 .cache entries
```

The final deliverable is not source-only.

Exclude:

```text
node_modules/
dist/
.cache/
generated simulation output/
temporary cache files/
```

Keep:

```text
package-lock.json
.env.example
source files
tests
documentation
```

---

# Validation performed independently

## Frontend

Passed:

```bash
npm ci
npm run typecheck
npm run build
```

Production bundle:

```text
index HTML: approximately 0.53 kB
CSS: approximately 37.93 kB
JavaScript: approximately 214.31 kB
```

No npm vulnerabilities were reported.

## .NET

Not independently run because `dotnet` is unavailable in the audit environment.

The repository claims:

- build success;
- Phase 3 tests passing;
- full Simulator test count above 317.

These claims remain plausible but unverified in this audit.

---

# Recommended correction order

## Immediate correctness

1. Make Dashboard High Precision import usable.
2. Correct imported-source bid/ask capability metadata.
3. Fix missing-execution-candle aggregation so partial minutes are not marked complete.
4. Correct fill-model metadata.
5. Map capability and terminal-state errors to structured HTTP responses.
6. Fix cache writer disposal and full cache validation.

## Precision performance

7. Implement minute-aligned execution batches.
8. Stop writing every execution frame to normal chart replay.
9. Store sub-minute detail only around trades by default.
10. Implement actual low-watermark page acquisition.
11. Measure one-day, one-week, and one-month 5s performance.

## Dashboard completion

12. Implement incremental live trades.
13. Add lifecycle events.
14. Integrate `AnalysisChart.vue`.
15. Add setup/entry/stop/target/exit markers.
16. Add selected-trade sub-minute zoom.

## Consistency and maintenance

17. Modernise the CLI for all Phase 3 settings.
18. Serialise job-state updates.
19. Resolve dedicated-thread semantics.
20. Add MFE/MAE and rich metrics.
21. Align capability registry with actual source factories.
22. Remove unused deterministic seed.
23. Produce a clean source-only ZIP.

---

# Overall assessment

| Area | Assessment |
| --- | --- |
| Legacy strategy fix | Working |
| Custom strategy intervals | Implemented in Dashboard/API path |
| OANDA 5s capability | Implemented |
| Genuine 1s import foundation | Partially implemented |
| Dashboard 1s usability | Not working |
| Two-stage aggregation | Implemented, but gap correctness needs fixing |
| No fake 1s interpolation | Correct |
| Sub-minute execution performance | Not optimised |
| Production low-watermark | Not implemented |
| Live trades | Not implemented |
| Real simulator chart | Not implemented |
| MFE/MAE | Not implemented |
| Cache integrity | Partial |
| Frontend build | Passed |
| C# build/tests | Not independently verified |
| Source-only packaging | Not complete |

The project is now a strong 1-minute simulator with a useful sub-minute foundation. The next pass should focus less on adding new abstractions and more on completing the precision data path, making the Dashboard features real, and proving performance with measured 5-second runs.
