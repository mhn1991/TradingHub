# TradingHub Simulator Modernisation — Phase 2 Codex Implementation Brief

## Purpose of this pass

This is the **second implementation pass** over the existing TradingHub simulator modernisation.

Do **not** create another simulator, another runner, or another disconnected dashboard workflow. Work directly against the implementation already present in the solution.

The repository already contains:

- `IBacktestApplicationService` and `BacktestApplicationService`
- file-backed simulation-job persistence
- a bounded job queue
- `StreamingComparativeEngine`
- `StrategySimulationSession`
- one-minute canonical market frames
- OANDA historical streaming/cache abstractions
- `PrefetchingCandleStream`
- shared analysis snapshots
- sequential and parallel strategy modes
- replay chunk writing
- DashboardLive simulation endpoints
- a SignalR hub
- Dashboard Simulator mode
- Legacy and Improved progressive agents
- automated tests

Preserve those foundations where they are correct.

This pass must focus on the **actual gaps and performance problems in the current code**, especially:

1. The first uncached OANDA run still downloads the entire requested range before simulation begins.
2. The prefetch low-watermark configuration is not actually used.
3. Parallel strategy mode does not use persistent strategy workers.
4. Dedicated-thread mode creates a new long-running thread for every market frame.
5. The Dashboard Simulator still polls instead of using SignalR.
6. Playback controls are mostly visual scaffolding rather than functional replay.
7. Replay is loaded only after completion and the unbounded replay endpoint materialises all chunks.
8. Strategy event output is too coarse for progressive chart overlays.
9. Progress persistence uses unowned asynchronous writes.
10. Completed jobs and their resources remain in the in-memory running-job dictionary.
11. Several configuration options are present but not fully implemented.
12. There is not yet enough runtime telemetry to identify the real one-year bottleneck.

The final result should make a first-time one-year simulation feel immediately active in the dashboard, remain bounded in memory, process strategies deterministically, and expose measured performance rather than inferred performance.

---

# 1. Mandatory initial audit

Before modifying code:

1. Read:
   - `ARCHITECTURE_SIMULATOR.md`
   - `RUN_BACKTEST.md`
   - `VALIDATION.md`
   - `TradingHub_Codex_Final_Implementation_Prompt.md`
2. Inspect the current implementation of:
   - `Simulator/Engine/StreamingComparativeEngine.cs`
   - `Simulator/Engine/StrategySimulationSession.cs`
   - `Simulator/Services/BacktestApplicationService.cs`
   - `Simulator/MarketData/OandaStreamingCandleSource.cs`
   - `Simulator/MarketData/PrefetchingCandleStream.cs`
   - `Simulator/MarketData/StreamingCandleCache.cs`
   - `Simulator/Replay/ChunkedReplayWriter.cs`
   - `DashboardLive/SimulationApi.cs`
   - `DashboardLive/SimulationHub.cs`
   - `DashboardLive/SimulationRealtimeBridge.cs`
   - `Dashboard/src/components/SimulatorPanel.vue`
   - `Dashboard/src/components/AnalysisChart.vue`
   - the progressive Agent implementations
   - the existing simulator tests
3. Run the current build and tests before changes.
4. Record:
   - current build status;
   - current test count;
   - current one-month cached benchmark;
   - current sequential versus parallel benchmark;
   - current peak memory;
   - current replay size;
   - current time to first visible dashboard replay data.
5. Keep the before/after measurements in a new report such as:
   - `SIMULATOR_PHASE2_VALIDATION.md`

Do not claim a performance improvement without recording the before and after numbers.

---

# 2. Important findings in the current implementation

Treat the following as concrete code-review findings that must be addressed.

## 2.1 First uncached run is not truly streamed to the simulator

In `OandaStreamingCandleSource.StreamAsync`, when caching is enabled and no valid cache exists, the current path:

1. downloads the entire historical range;
2. writes the full cache;
3. closes/finalises the cache;
4. reopens the cache;
5. only then yields candles to the simulator.

This means a one-year first run still appears to wait during the whole download before the simulation begins.

That contradicts the intended dashboard experience.

The corrected behaviour must be:

```text
Download first OANDA page
    ↓
validate and normalise page
    ↓
append candles to temporary cache
    ↓
yield the same candles immediately to prefetch/simulator
    ↓
download next page concurrently as capacity allows
    ↓
continue until complete
    ↓
atomically commit cache manifest and final cache file
```

The user should see:

- data-download progress;
- warm-up progress;
- simulation market time;
- replay chunks;

before the full year has downloaded.

---

## 2.2 `PrefetchLowWatermark` is currently unused

`PrefetchingCandleStream` calculates an unread count but discards it.

The current bounded channel prevents unbounded memory, but it does not implement the configured page-aware low-watermark policy.

Do not leave a configuration option that has no effect.

Implement a real low-watermark design or deliberately simplify/remove the option.

The required design is page-aware:

```text
Unread count > low watermark
    → consumer continues
    → no new page request required

Unread count <= low watermark
    → if no request is active and source is not complete
    → request/read the next page

Buffer full
    → producer waits through backpressure

End of source
    → drain remaining buffered candles
    → complete cleanly
```

The implementation must expose diagnostics:

- current unread count;
- peak unread count;
- configured capacity;
- low watermark;
- pages requested;
- source waits;
- consumer waits;
- producer completion;
- source completion;
- cache/source mode.

A bounded `Channel<T>` may still be used, but the low-watermark must control page acquisition rather than being a dead field.

Consider introducing:

```csharp
public interface IPagedHistoricalCandleSource
{
    ValueTask<HistoricalCandlePage> ReadPageAsync(
        HistoricalPageRequest request,
        CancellationToken cancellationToken = default);
}
```

with:

```csharp
public sealed record HistoricalCandlePage
{
    public required IReadOnlyList<MarketCandle> Candles { get; init; }

    public required DateTimeOffset? NextCursor { get; init; }

    public required bool IsComplete { get; init; }

    public required int PageNumber { get; init; }
}
```

The OANDA source can implement this interface, while cache and fake test sources can provide equivalent page readers.

---

## 2.3 Current parallel strategy execution is not a persistent worker model

In the current `StreamingComparativeEngine.ProcessParallelAsync`:

- task mode calls `ProcessFrameAsync` directly for each session and then `Task.WhenAll`;
- dedicated-thread mode calls `Task.Factory.StartNew(... LongRunning ...)` **for every market frame**.

Creating a new long-running task/OS thread for every one-minute candle is incorrect and potentially catastrophic over hundreds of thousands of frames.

Replace this with persistent workers created once per simulation.

Required architecture:

```text
Create strategy sessions
    ↓
Create one StrategyWorkerHost per strategy
    ↓
Start each worker once
    ↓
For every MarketFrame:
        publish one envelope to every worker
        await acknowledgements from every worker
        validate sequence
        commit replay
    ↓
Complete worker channels
    ↓
await worker termination
    ↓
dispose sessions
```

Create a model similar to:

```csharp
public sealed record StrategyFrameEnvelope
{
    public required MarketFrame Frame { get; init; }

    public required TaskCompletionSource<StrategyFrameResult>
        Completion { get; init; }
}
```

Use:

```csharp
TaskCreationOptions.RunContinuationsAsynchronously
```

for completion sources.

Create:

```csharp
public interface IStrategyWorkerHost : IAsyncDisposable
{
    string StrategyId { get; }

    ValueTask<Task<StrategyFrameResult>> EnqueueAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default);

    StrategyWorkerMetrics SnapshotMetrics();

    Task Completion { get; }
}
```

Each worker owns a bounded channel using `StrategyChannelCapacity`.

### Task mode

Start one normal persistent worker task per strategy.

### Dedicated-thread mode

Start one dedicated long-running worker per strategy **once for the entire simulation**, not once per frame.

### Sequential mode

Keep a simple sequential path for comparison and determinism testing.

### Barrier

The market clock must still wait for all active strategy results for frame `N` before moving to frame `N + 1`.

---

# 3. Reduce allocations in the one-minute hot path

The current engine creates new collections for every base candle, including:

- a new `HashSet<BarInterval>`;
- a new `Dictionary<BarInterval, AnalysisSnapshot>` copied from the latest snapshots;
- task arrays/LINQ arrays for strategy execution.

For a year of one-minute data, these allocations are significant.

Optimise carefully without changing results.

## 3.1 Snapshot state

Analysis snapshots change only when a configured analysis interval closes.

Do not copy the complete snapshot dictionary on every one-minute frame when no analysis interval closed.

Use an immutable snapshot-set object that is replaced only when analysis state changes.

For example:

```csharp
public sealed record AnalysisSnapshotSet
{
    public required long Version { get; init; }

    public required IReadOnlyDictionary<
        BarInterval,
        AnalysisSnapshot> Snapshots { get; init; }
}
```

Reuse the same `AnalysisSnapshotSet` reference for base frames until a 5m, 15m, or 1h snapshot changes.

Ensure the underlying dictionary is truly immutable, not merely exposed through `IReadOnlyDictionary`.

## 3.2 Closed interval representation

Avoid allocating a general-purpose hash set per minute.

The configured interval count is small.

Use one of:

- a compact immutable array;
- a small value type;
- a flags/bitset mapping created from configured intervals.

Preserve readability and correctness.

## 3.3 Strategy scheduling

The broker/order lifecycle still needs each one-minute frame.

However, Agent evaluation only needs to run when the strategy trigger interval closes or a position-management condition requires it.

Keep order/position processing per minute, but avoid unnecessary Agent analysis construction when:

- warm-up suppresses trading;
- no required interval changed;
- no open position requires strategy-driven exit evaluation.

Benchmark before and after.

## 3.4 Avoid repeated LINQ in hot loops

Review:

- strategy-frame task creation;
- snapshot filtering;
- open-order/position lookup;
- trade-transition capture;
- replay-row creation;
- progress snapshots.

Replace repeated full enumeration where a bounded direct lookup is clearer.

Do not reduce correctness or readability merely to remove all LINQ.

---

# 4. Stream to cache and simulator simultaneously

Introduce a safe incremental cache writer.

Suggested abstraction:

```csharp
public interface IStreamingCandleCacheWriter : IAsyncDisposable
{
    ValueTask AppendAsync(
        MarketCandle candle,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(
        StreamingCacheCommitMetadata metadata,
        CancellationToken cancellationToken = default);

    ValueTask AbortAsync(
        CancellationToken cancellationToken = default);
}
```

Required behaviour:

- write to a temporary cache file;
- update count, first timestamp, last timestamp, and hash incrementally;
- yield the candle to the simulator after successful normalisation and cache append;
- never expose an incomplete temporary file as a valid cache;
- atomically commit final data and metadata;
- remove or quarantine temporary files on failure;
- recover stale temporary files at startup;
- validate sidecar metadata and content hash before using a cache;
- detect truncated gzip/JSONL files;
- verify first/last timestamps and candle count;
- include price components in cache identity;
- include schema/data-source version;
- do not reread the newly written full-year cache merely to run the current simulation.

The current run and later cached run must produce the same normalised candle fingerprint.

---

# 5. Fix cache integrity and metadata

The current cache header contains placeholder values for:

- candle count;
- first candle;
- last candle;
- hash.

A sidecar is written, but current validation mostly checks the header and does not fully verify the sidecar/content.

Make cache validity explicit.

Use one authoritative metadata format, for example:

```csharp
public sealed record StreamingCacheManifest
{
    public required int SchemaVersion { get; init; }

    public required string DataSourceVersion { get; init; }

    public required string Broker { get; init; }

    public required string Environment { get; init; }

    public required string Instrument { get; init; }

    public required string Interval { get; init; }

    public required DateTimeOffset From { get; init; }

    public required DateTimeOffset To { get; init; }

    public required string PriceComponents { get; init; }

    public required long CandleCount { get; init; }

    public required DateTimeOffset FirstCandle { get; init; }

    public required DateTimeOffset LastCandle { get; init; }

    public required string ContentHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required bool Complete { get; init; }
}
```

Validation must reject:

- missing data file;
- missing manifest;
- incomplete manifest;
- mismatched request;
- wrong price components;
- wrong schema;
- count mismatch;
- timestamp mismatch;
- corrupted gzip;
- hash mismatch.

Add tests for all these cases.

---

# 6. Wire actual data-source progress into jobs

`OandaStreamingCandleSource` exposes progress events, but the current application-service path does not meaningfully connect them to `SimulationJobSnapshot`.

The job should accurately distinguish:

```text
PreparingData
DownloadingData
LoadingCache
WarmingUp
Running
Exporting
Completed
```

Do not infer “downloading” from whether the current candle is in warm-up.

Subscribe to the historical source’s progress interface/event.

Progress should contain:

```csharp
public sealed record HistoricalSourceProgress
{
    public required string Phase { get; init; }

    public required bool FromCache { get; init; }

    public required long CandlesRead { get; init; }

    public required int PagesRead { get; init; }

    public DateTimeOffset? LatestCandle { get; init; }

    public long? EstimatedCandles { get; init; }

    public decimal? Percent { get; init; }
}
```

Publish data-source progress through:

- job snapshots;
- SignalR;
- the dashboard runtime panel.

The dashboard should show a separate distinction between:

- downloading source data;
- loading cache data;
- warming indicators;
- running evaluation;
- exporting final summaries.

---

# 7. Replace unsafe progress persistence

The current progress callback starts repository writes without awaiting them.

This can lead to:

- overlapping writes;
- stale snapshots being written after newer snapshots;
- excessive disk writes;
- unobserved failures;
- disagreement between in-memory and persisted state.

Implement a coalescing persistence path.

Options include:

- a single per-job snapshot persistence worker;
- a central bounded/coalescing channel keyed by job ID;
- a throttled awaited save operation.

Requirements:

- only one write per job may execute at a time;
- newer snapshots supersede older pending snapshots;
- terminal snapshots are always persisted synchronously/awaited;
- persistence failures are observed and reported;
- progress persistence frequency is configurable;
- SignalR updates need not wait on every disk write;
- job-state ordering must remain monotonic.

Add a monotonic revision to snapshots:

```csharp
public required long Revision { get; init; }
```

The repository must not overwrite revision `N + 1` with revision `N`.

---

# 8. Fix completed-job resource lifecycle

The current in-memory running-job dictionary retains completed jobs and their:

- cancellation token sources;
- pause gates;
- result objects;
- request objects;
- full strategy results.

Implement clear lifecycle ownership.

After a terminal result is persisted:

1. persist final snapshot;
2. persist final result/output metadata;
3. notify clients;
4. remove the job from `_running`;
5. dispose cancellation/pause resources;
6. retain only the file-backed job record and replay outputs.

Controls must respond correctly:

- pausing a completed job → conflict/bad request;
- resuming a completed job → conflict/bad request;
- cancelling a completed job → no-op or conflict, documented consistently;
- pausing a queued job → either supported explicitly or rejected clearly;
- cancelling a queued job → remove/skip from queue and mark cancelled.

Do not require the complete result to remain in memory for later dashboard access.

For `RunToCompletionAsync`, use a completion task stored by the running job rather than polling every 100 ms.

For example:

```csharp
public TaskCompletionSource<ComparativeSimulationResult> Completion { get; }
```

The CLI can await that completion directly.

---

# 9. Replace blocking pause gate with async frame-boundary pause

The current `ManualResetEventSlim` pause path blocks a thread.

Implement an asynchronous pause primitive such as:

```csharp
public interface IAsyncPauseGate
{
    bool IsPaused { get; }

    ValueTask WaitIfPausedAsync(
        CancellationToken cancellationToken = default);

    void Pause();

    void Resume();
}
```

Pause semantics:

- pause takes effect only at a safe frame boundary;
- a frame must not be paused halfway through fills, account mutation, replay commit, or strategy barrier;
- once frame `N` is committed, pause may block before frame `N + 1`;
- cancellation must release paused waits;
- pausing visual playback remains independent.

Add tests for pausing while:

- source is active;
- cache is loading;
- warm-up is active;
- strategies are processing;
- the job is already paused;
- cancellation occurs during pause.

---

# 10. Implement real persistent strategy workers

Create a reusable `StrategyWorkerHost`.

Each worker should:

- own exactly one `StrategySimulationSession`;
- own one bounded input channel;
- start once;
- read frames in sequence;
- reject duplicate/out-of-order sequence;
- process one frame at a time;
- complete the frame acknowledgement;
- capture failure context;
- stop on cancellation or channel completion;
- expose metrics.

Metrics must include real:

- processed frames;
- average processing time;
- max processing time;
- total processing time;
- channel occupancy;
- peak occupancy;
- producer wait time;
- worker idle time;
- barrier wait contribution;
- failures.

The `StrategyChannelCapacity` runtime option must actually control these channels.

The current configuration must no longer contain unused strategy-channel settings.

---

# 11. Deterministic input identity

The current input stream ID includes a random GUID, so two runs over the same cached candle stream receive different stream identifiers.

Use separate concepts:

```text
SimulationId
    identifies one run

InputRequestId
    deterministic identity of broker/environment/instrument/range/components/schema

InputHash
    hash of the actual normalised emitted candles
```

`InputRequestId` should be deterministic.

`InputHash` remains the final proof that sequential and parallel runs consumed identical data.

Include both in:

- job snapshot;
- manifest;
- strategy results;
- comparison report.

---

# 12. Strengthen determinism tests

The current sequential/parallel test compares primarily:

- input hash;
- processed count;
- trade count;
- net profit;
- final balance.

That is not sufficient.

Create a canonical deterministic result fingerprint covering, in stable order:

- setup IDs;
- setup timestamps;
- confirmation timestamps;
- signal timestamps;
- decision IDs;
- actions;
- order IDs where deterministic IDs are expected;
- order eligibility sequence;
- fill timestamps;
- entry prices;
- stop prices;
- target prices;
- exit timestamps;
- exit prices;
- exit reasons;
- commissions;
- gross/net P/L;
- realised R;
- final balance/equity;
- ledger entries;
- data-quality report.

Run the same scenario in:

- sequential mode;
- parallel task mode;
- persistent dedicated-thread mode.

The canonical fingerprints must match.

If broker-generated IDs are intentionally random, exclude raw IDs from the canonical hash and compare their semantic relationships instead.

---

# 13. Fix dedicated-thread mode

Dedicated-thread mode must create:

```text
one dedicated worker per strategy per simulation
```

It must not create:

```text
one dedicated thread per strategy per one-minute frame
```

Add a test that processes thousands of frames and verifies:

- the number of worker threads remains bounded;
- each strategy uses one persistent worker;
- all frames are processed in order;
- cancellation joins the worker;
- no thread remains after disposal.

Expose worker thread/task mode in metrics.

---

# 14. Fix or remove partially implemented runtime options

Audit every `BacktestRuntimeOptions` property.

At minimum address:

## `PrefetchLowWatermark`

Must control page prefetch or be removed.

## `StrategyChannelCapacity`

Must control persistent worker channels.

## `WarmupBaseCandleCount`

It currently does not meaningfully alter `ResolveWarmupFrom`.

Implement correct behaviour or remove it.

If implemented, define precedence clearly:

```text
warmup start = earlier of:
    From - WarmupDays
    enough base-candle time estimate for WarmupBaseCandleCount
```

For FX gaps/weekends, readiness-based warm-up may be more reliable than calendar estimation.

## `NearestToOpenFirst`

The current OCO mapping effectively falls back to stop-first.

Implement the actual policy:

When both stop and target are touched in one execution candle:

1. compare distance from candle open to executable stop price;
2. compare distance from candle open to executable target price;
3. execute the nearer boundary;
4. define deterministic tie behaviour;
5. account for long/short bid/ask execution price.

Add explicit tests.

## `UseHistoricalBidAsk`

Either:

- implement actual OANDA bid/ask candle retrieval in this pass; or
- clearly disable the option in the UI and result metadata until implemented.

Do not allow a checkbox/configuration that suggests historical bid/ask was used when it was not.

## `MaximumParallelStrategies`

Continue validating it, and display queue/resource limits in the dashboard.

---

# 15. SignalR client integration

The server has a SignalR hub, but the Vue Simulator panel currently polls.

Add:

```text
@microsoft/signalr
```

to Dashboard dependencies.

Create a simulation realtime client/composable:

```ts
useSimulationRealtime(simulationId)
```

Required behaviour:

- establish hub connection;
- subscribe to simulation group;
- process:
  - `SimulationStatusChanged`;
  - `SimulationProgressChanged`;
  - `SimulationCompleted`;
  - `SimulationFailed`;
  - new replay/trade event notifications;
- reconnect automatically;
- re-subscribe after reconnect;
- ignore stale snapshot revisions;
- dispose when job selection changes or component unmounts;
- fall back to slow polling when SignalR is unavailable.

Polling should be a fallback, not the primary path.

Use the snapshot `Revision` to prevent older events replacing newer state.

---

# 16. Implement actual progressive dashboard replay

The current `SimulatorPanel.vue` contains playback state, but:

- `playbackSpeed` does not drive a timer;
- `playbackPaused` does not advance replay;
- replay is loaded only after job completion;
- the panel displays text for one OHLC row rather than a real chart;
- the complete `/replay` endpoint is used.

Implement functional playback.

## Required playback engine

Create a composable such as:

```ts
useSimulationPlayback()
```

It should support:

- play;
- pause;
- step forward;
- step back;
- speed control;
- jump to time;
- jump to sequence;
- jump to setup;
- jump to confirmation;
- jump to entry;
- jump to exit;
- follow latest available frame;
- stop following when the user scrubs backward;
- resume follow-live mode.

Use `requestAnimationFrame` or a stable timer and calculate advancement based on elapsed wall-clock time.

Do not create one timer per candle.

## Progressive chunks

While a simulation is running:

1. list available replay chunks;
2. fetch only chunks not already loaded;
3. append them in sequence;
4. retain a bounded chart window;
5. persist a chunk index/cursor;
6. continue loading as `ReplayChunkAvailable` notifications arrive.

Do not wait for `job.isComplete`.

## Real chart

Use the existing `AnalysisChart.vue` where practical.

Convert compact replay rows into the chart contract.

Display:

- candles;
- setup markers;
- confirmation markers;
- signal markers;
- entry;
- stop;
- target;
- exit;
- entry-to-exit line;
- P/L and R labels.

Do not display an entire year of one-minute candles at once.

Maintain:

- a full-range downsampled overview;
- a detailed bounded window near the playback cursor;
- optional 1m detail around active trades.

---

# 17. Replace the unbounded replay endpoint

The current:

```text
GET /api/simulations/{id}/replay
```

reads every gzip chunk, deserialises it, appends all rows to a list, and returns the entire result.

This defeats chunked replay and may consume large server/browser memory.

Change the API.

Accept one or more of:

```text
from/to
startSequence/endSequence
cursor
limit
chunkId
```

Enforce a maximum response size.

Example:

```text
GET /api/simulations/{id}/replay?startSequence=100000&limit=5000
```

Response:

```json
{
  "simulationId": "...",
  "startSequence": 100000,
  "nextCursor": "chunk-000021:offset-250",
  "hasMore": true,
  "rows": []
}
```

Keep direct chunk endpoints for efficient progressive loading.

Deprecate or reject an unbounded full-year request.

---

# 18. Add replay chunk metadata/index

Do not discover replay chunks only by enumerating filenames.

Maintain an atomic chunk index:

```csharp
public sealed record ReplayChunkDescriptor
{
    public required string ChunkId { get; init; }

    public required string RelativePath { get; init; }

    public required long FirstSequence { get; init; }

    public required long LastSequence { get; init; }

    public required DateTimeOffset FirstTime { get; init; }

    public required DateTimeOffset LastTime { get; init; }

    public required int RowCount { get; init; }

    public required long CompressedBytes { get; init; }

    public required string ContentHash { get; init; }

    public required bool Complete { get; init; }
}
```

After a chunk is atomically written:

1. calculate descriptor;
2. atomically update index;
3. publish `ReplayChunkAvailable`;
4. allow clients to fetch it.

Use asynchronous endpoints.

Remove sync-over-async calls such as `.GetAwaiter().GetResult()` inside request handlers.

Validate chunk IDs and prevent path traversal.

---

# 19. Make strategy replay event-driven instead of per-frame duplication

The current strategy event writer records balance/equity/open-position counts for every base frame for every strategy.

For a year and multiple strategies this duplicates large amounts of mostly unchanged data.

Split output into:

## Periodic performance samples

Downsample configurable equity/balance samples, for example:

- every N minutes;
- on material equity change;
- on trade lifecycle event;
- on day boundary.

## Event-driven strategy lifecycle

Record events only when something changes:

```csharp
public enum StrategyReplayEventType
{
    SetupCreated,
    SetupAdvanced,
    SetupExpired,
    SetupInvalidated,
    SignalCreated,
    OrderSubmitted,
    OrderRejected,
    OrderFilled,
    PositionOpened,
    StopUpdated,
    TargetUpdated,
    PartialExit,
    StrategyCloseRequested,
    PositionClosed,
    TradeCompleted
}
```

Each event should contain:

- sequence;
- event time;
- strategy;
- setup ID;
- order/position identity;
- side;
- price;
- quantity;
- stop;
- target;
- reason;
- relevant confidence;
- source timeframe.

The Dashboard should receive lifecycle events before simulation completion.

---

# 20. Incremental trade journal output

The current `trades.json.gz` sidecar is mainly written at simulation completion.

Write completed trades incrementally.

Options:

- append-only JSONL chunks;
- one trade chunk per bounded batch;
- an indexed event store.

The trade API must return completed trades during a running simulation.

The Dashboard should update the trade journal immediately after each trade closes.

Do not hold all trade records solely in memory until completion.

A final consolidated trade file may still be generated during export.

---

# 21. Add compact annotation deltas for chart overlays

The market replay currently contains compact OHLC data but not enough annotation information to reproduce the full simulator chart.

Do not dump a full `AnalysisSnapshot` on every minute.

Instead, write annotation deltas when an analysis interval closes or structure changes.

Example:

```csharp
public sealed record AnnotationReplayDelta
{
    public required long Sequence { get; init; }

    public required DateTimeOffset AvailableAt { get; init; }

    public required string Interval { get; init; }

    public required long SnapshotVersion { get; init; }

    public IReadOnlyList<SwingPointDto>? NewSwings { get; init; }

    public IReadOnlyList<PriceZoneDto>? Zones { get; init; }

    public IReadOnlyList<TrendlineDto>? Trendlines { get; init; }

    public IReadOnlyList<PriceChannelDto>? Channels { get; init; }

    public MarketStructureDto? Structure { get; init; }

    public IndicatorReplayDto? Indicators { get; init; }

    public decimal? Confidence { get; init; }
}
```

Use stable IDs or geometric fingerprints so the dashboard can:

- add;
- update;
- remove;

annotations correctly.

Support selected chart intervals:

- 5m;
- 15m;
- 1h.

---

# 22. Performance instrumentation

Add low-overhead metrics around the actual stages:

```text
source wait
OANDA HTTP
cache write
cache read
prefetch wait
aggregation
ChartAnnotationEngine total
ATR/RSI/Bollinger
SwingDetector
MarketStructureAnalyzer
SupportResistanceDetector
RansacTrendlineDetector
ChannelDetector
ConfidenceScorer
market-frame construction
strategy worker processing
broker execution
barrier wait
replay serialisation
replay compression
job-state persistence
SignalR publishing
```

Expose aggregate metrics:

```csharp
public sealed record BacktestStageMetrics
{
    public required string Stage { get; init; }

    public required long InvocationCount { get; init; }

    public required TimeSpan TotalDuration { get; init; }

    public required TimeSpan AverageDuration { get; init; }

    public required TimeSpan MaximumDuration { get; init; }

    public required long AllocatedBytes { get; init; }
}
```

Allocation measurement may be sampled if exact per-stage measurement is too expensive.

Include:

- stage metrics endpoint;
- final validation report;
- optional dashboard diagnostics panel.

Do not turn on verbose per-candle logging.

---

# 23. Optimise heavy chart analysis only after measurement

The current ChartAnnotator already schedules heavy analysis periodically and on structural events.

Do not blindly rewrite DBSCAN or RANSAC.

First measure:

- invocation count by timeframe;
- average/maximum duration;
- percentage of total runtime;
- swing count/pivot count;
- allocations.

Then optimise the measured bottleneck.

Possible improvements, only if measurements support them:

- avoid repeated sorting of unchanged swing snapshots;
- cache price-sorted and time-sorted pivot views;
- incremental support/resistance updates;
- cap RANSAC candidate window by recency and structure segment;
- skip channel recomputation when neither trendlines nor relevant close state changed;
- reuse small arrays/buffers;
- avoid recalculating duplicate lines;
- maintain stable deterministic results.

Add regression tests before changing structural algorithms.

---

# 24. Improve source and API error handling

OANDA retry logic should distinguish:

- transient transport failure;
- rate limiting;
- authentication failure;
- invalid instrument;
- invalid date range;
- permanent broker rejection.

Do not retry permanent errors five times.

Respect `Retry-After` where available.

Return useful job errors without exposing credentials or sensitive HTTP headers.

The API should return structured problem details.

---

# 25. Historical bid/ask implementation

After the streaming/concurrency/dashboard fixes are complete, implement historical bid/ask if the current Brokers/OANDA API can support it safely.

Required behaviour:

- request OANDA bid, ask, and midpoint components;
- preserve them in `MarketCandle`;
- include price components in cache key/manifest;
- use ask for buy entries;
- use bid for sell entries;
- use executable side for exits;
- use bid/ask high/low correctly for stop/target detection;
- identify fill model as `HistoricalBidAsk`.

If historical bid/ask cannot be implemented in this pass:

- disable the option in Dashboard;
- set result metadata honestly;
- remove misleading defaults;
- document the missing broker API work precisely.

Do not report `HistoricalBidAsk` unless bid/ask candles actually drove fills.

---

# 26. Correct ambiguous intrabar policies

Implement all policies fully.

## ConservativeStopFirst

Stop executes when both stop and target are touched.

## OptimisticTargetFirst

Target executes when both are touched.

## NearestToOpenFirst

Choose the executable boundary nearest to the candle open.

For long positions:

- compare executable bid-side stop and target distances.

For short positions:

- compare executable ask-side stop and target distances.

Define deterministic tie behaviour, preferably conservative.

Record:

- whether ambiguity occurred;
- selected policy;
- chosen boundary;
- candle time;
- stop distance;
- target distance.

Include this in the trade journal.

---

# 27. Job recovery and restart behaviour

File-backed jobs survive browser refresh, but define server restart behaviour.

On service startup, scan persisted jobs.

For jobs left in:

```text
Queued
PreparingData
DownloadingData
LoadingCache
WarmingUp
Running
Paused
Exporting
Cancelling
```

choose and implement one policy:

## Minimum acceptable policy

Mark them:

```text
Failed / Interrupted
```

with a clear reason and preserve partial output.

## Optional advanced policy

Resume from the last committed market/replay sequence if the source/cache and state snapshots support deterministic restoration.

Do not leave stale jobs appearing permanently “Running” after process restart.

Add cleanup/retention configuration for old jobs and replay outputs.

---

# 28. Security and repository hygiene

The supplied repository archive contains `.env.integration`.

Treat all credentials in that file as compromised.

Required actions:

1. Remove `.env.integration` from the repository and deliverables.
2. Ensure it is not tracked in Git.
3. Keep only `.env.example` or `.env.integration.example` with placeholders.
4. Document that existing credentials must be rotated.
5. Search source, logs, tests, cache metadata, replay files, and documentation for secrets.
6. Do not print tokens in errors.
7. Do not include:
   - `bin/`;
   - `obj/`;
   - `node_modules/`;
   - `dist/`;
   - `.cache/`;
   - generated replay data;
   - credential files;
   in the final source archive.

The current ZIP also contains build outputs and dependencies. Produce a clean source-only deliverable.

---

# 29. Dashboard dependency and build hygiene

Add SignalR dependency through `package.json` and lockfile.

Validate from a clean state:

```bash
cd Dashboard
rm -rf node_modules dist
npm ci
npm run typecheck
npm run build
```

Do not rely on a bundled `node_modules` directory.

Add frontend tests if a test framework is introduced, but avoid unnecessary framework churn.

---

# 30. Required frontend behaviour tests

Add tests or deterministic component/composable tests for:

- SignalR snapshot update;
- reconnection and re-subscription;
- fallback polling;
- stale revision ignored;
- new chunk notification;
- loading only missing chunks;
- playback timer advances according to speed;
- pause stops visual advancement;
- step forward/back;
- follow-latest mode;
- scrubbing disables follow-latest;
- jump to trade/setup/exit;
- bounded chart-window retention;
- no full-year replay load;
- component disposal closes connection/timer.

If Vue component tests are too large for this pass, at minimum test the composables and pure replay state reducers.

---

# 31. Required backend tests

## First-run true streaming

Use a fake paged source with gates:

- page 1 becomes available;
- later pages remain blocked;
- verify simulator processes/yields page 1 before page 2/full source completes;
- verify replay chunk becomes visible before source completion.

## Incremental cache

Verify:

- current simulation consumes candles while temporary cache is being written;
- successful completion atomically commits cache;
- cancellation leaves no valid cache;
- corrupted temporary files are ignored/cleaned;
- later cached run produces the same input hash.

## Low-watermark

Verify:

- next page is not requested too early;
- next page is requested at/below watermark;
- no duplicate page request;
- no unread overwrite;
- backpressure works;
- peak occupancy remains bounded.

## Persistent workers

Verify:

- one task/thread per strategy for the whole run;
- frame order;
- exact once processing;
- channel capacity;
- barrier;
- failure propagation;
- clean shutdown;
- no surviving workers.

## Job persistence

Verify:

- revision monotonicity;
- coalescing;
- terminal state always persisted;
- stale save cannot overwrite newer save;
- completed job removed from `_running`;
- controls reject terminal jobs;
- restart marks interrupted jobs.

## Replay API

Verify:

- bounded range/limit;
- cursor;
- chunk metadata;
- path traversal rejected;
- sync-over-async removed;
- partial chunks visible while running;
- unbounded request rejected or capped.

## Live trades

Verify a completed trade is visible from the API before the simulation completes.

## Pause

Verify pause only occurs between committed frames.

## Determinism

Compare complete canonical fingerprints across all modes.

## Intrabar policies

Test every policy with long and short positions.

## Warmup count

Test `WarmupBaseCandleCount` if retained.

---

# 32. Benchmark requirements

Create a reproducible benchmark command or project.

Run at least:

## Synthetic benchmark

- 500,000 one-minute candles;
- two strategies;
- shared analysis;
- no network;
- replay enabled.

## Cached one-month benchmark

- one month of one-minute candles;
- Legacy and Improved strategies;
- sequential;
- parallel tasks;
- persistent dedicated threads.

## First-run streaming benchmark

Use a fake paged source with realistic latency and verify time to first processed frame.

Record:

```text
time to first source page
time to first warm-up frame
time to first evaluation frame
time to first replay chunk
total duration
candles/sec
peak memory
total allocations
GC counts
cache write time
cache read time
annotation time
strategy time
barrier wait
replay write time
job persistence time
```

The final report must explain whether parallel mode is actually faster for the two current strategies.

If sequential is faster, keep parallel mode available but consider using sequential as the default for lightweight strategy sets.

Do not choose defaults based only on architectural preference.

---

# 33. Performance acceptance criteria

Use measured baseline values from the current repository.

Required qualitative improvements:

1. A first uncached run begins processing after the first bounded page/chunk, not after the full range downloads.
2. Dedicated-thread mode does not create per-frame threads.
3. Memory remains bounded as the requested range grows.
4. The dashboard receives partial replay and trade updates before completion.
5. The server never materialises an unbounded full-year replay response.
6. Sequential and parallel results remain identical.
7. Progress persistence does not create overlapping unobserved writes.
8. Completed jobs release in-memory resources.
9. Replay size per strategy is reduced by event-driven output/downsampling.
10. The UI playback controls actually work.

Set numeric acceptance thresholds only after recording baseline and hardware/environment.

---

# 34. Dashboard user experience acceptance scenario

The following must work:

1. Start `DashboardLive`.
2. Open Dashboard Simulator.
3. Configure GBP/JPY for a one-year range.
4. Select Legacy and Improved.
5. Click Run.
6. Receive a job ID immediately.
7. See actual status:
   - downloading;
   - cache loading;
   - warm-up;
   - running.
8. See current historical time change before the full download completes.
9. See candles/sec and source page progress.
10. See replay candles appear while the simulation is running.
11. See strategy setup events appear.
12. See trades appear as soon as they close.
13. Pause visual playback while computation continues.
14. Resume visual playback.
15. Change speed.
16. Scrub backwards.
17. Resume follow-latest mode.
18. Pause backend simulation at a safe frame boundary.
19. Resume backend simulation.
20. Refresh the browser and reconnect to the active job.
21. SignalR reconnects and re-subscribes.
22. Cancel a job cleanly.
23. Run to completion.
24. Compare metrics.
25. Click a trade and replay setup-to-exit.
26. Run again from cache.
27. Compare cached and initial input hashes.
28. Run sequentially and in parallel.
29. Verify canonical result fingerprints match.

---

# 35. CLI behaviour

The CLI remains a thin application-service adapter.

Improve it to display:

- source phase;
- pages;
- current market time;
- processed candles;
- candles/sec;
- active strategies;
- completed trades;
- cache mode;
- output directory;
- final stage metrics.

It should await the job completion task directly, not poll job status every 100 ms.

Support cancellation through Ctrl+C.

Do not duplicate simulator logic.

---

# 36. Documentation updates

Update:

- `ARCHITECTURE_SIMULATOR.md`
- `RUN_BACKTEST.md`
- `VALIDATION.md`

Add:

- `SIMULATOR_PHASE2_VALIDATION.md`
- optional `SIMULATOR_PERFORMANCE.md`

Document:

- true first-run stream-to-cache behaviour;
- low-watermark paging;
- persistent strategy workers;
- task versus dedicated thread;
- per-frame barrier;
- async pause;
- SignalR frontend;
- replay chunk index;
- event-driven strategy replay;
- incremental trades;
- annotation deltas;
- API pagination/cursors;
- stage metrics;
- restart behaviour;
- security cleanup;
- measured limitations.

---

# 37. Build and validation commands

Run from a clean repository:

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
```

Frontend:

```bash
cd Dashboard
rm -rf node_modules dist
npm ci
npm run typecheck
npm run build
```

Search unfinished code:

```bash
grep -RInE \
  'TODO|FIXME|NotImplementedException|throw new NotSupportedException' \
  --include='*.cs' \
  --include='*.ts' \
  --include='*.vue' \
  --exclude-dir=bin \
  --exclude-dir=obj \
  --exclude-dir=node_modules \
  .
```

Run the benchmark and determinism commands added in this pass.

Do not claim success unless commands were executed.

If the environment lacks credentials, run all fake-source and cache-source integration tests and state that live OANDA validation was not run.

---

# 38. Required final response from Codex

The final response must include:

1. Summary of files changed by project.
2. Exact defects fixed.
3. Before/after benchmark table.
4. Time to first processed frame before/after.
5. Sequential versus parallel comparison.
6. Dedicated-thread worker-count evidence.
7. Peak memory before/after.
8. Replay output size before/after.
9. Build result.
10. Test result and total passing tests.
11. Dashboard build result.
12. Clean-source packaging confirmation.
13. Secret scan result.
14. Remaining limitations.
15. Exact dashboard run instructions.
16. Exact CLI run instructions.
17. Any requirement not completed, stated explicitly.

Do not return only another architecture summary.

---

# 39. Priority order

Implement in this order:

1. Fix per-frame dedicated-thread creation.
2. Implement persistent strategy workers and actual bounded channels.
3. Stream cache writes and simulation concurrently on first run.
4. Implement actual low-watermark page prefetch.
5. Connect real source progress.
6. Fix progress persistence ordering.
7. Fix job completion/resource cleanup.
8. Replace blocking pause with async frame-boundary pause.
9. Add replay chunk index and bounded APIs.
10. Add incremental trade and lifecycle event output.
11. Wire SignalR in Vue.
12. Implement real progressive playback.
13. Integrate chart overlays.
14. Add stage metrics and benchmarks.
15. Optimise measured hot paths.
16. Implement historical bid/ask or disable misleading option.
17. Clean secrets and generated artefacts.
18. Update tests and documentation.

Correctness, deterministic results, and bounded memory remain more important than maximum concurrency.
