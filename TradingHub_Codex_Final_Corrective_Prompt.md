# TradingHub Simulator — Final Corrective Codex Implementation Brief

## Role

Act as a senior .NET architect, quantitative backtesting engineer, concurrency specialist, performance engineer, test engineer, and Vue/ASP.NET Core full-stack developer.

Work directly against the existing TradingHub solution contained in the supplied repository.

Do **not** create:

- a second simulator;
- a second backtest engine;
- a disconnected dashboard workflow;
- duplicate Agent, RiskManager, ExecutionManager, or ChartAnnotator implementations;
- a replacement solution that bypasses the existing projects.

Improve and correct the implementation already present.

The repository already contains the modernised simulator foundation, including:

- `IBacktestApplicationService`;
- `BacktestApplicationService`;
- persisted simulation jobs;
- bounded job processing;
- `StreamingComparativeEngine`;
- `StrategySimulationSession`;
- one-minute canonical market frames;
- OANDA historical paging/cache abstractions;
- low-watermark prefetch;
- sequential and parallel strategy modes;
- persistent strategy worker hosts;
- replay chunk writing;
- DashboardLive API endpoints;
- SignalR server support;
- Vue Simulator mode;
- Legacy and Improved progressive strategies;
- automated tests.

Preserve the good parts, but correct the remaining functional, architectural, concurrency, replay, UI, and validation defects described below.

This is a **corrective and completion pass**, not another architecture-only pass.

---

# 1. Required working method

Before changing code:

1. Read:
   - `ARCHITECTURE_SIMULATOR.md`
   - `RUN_BACKTEST.md`
   - `VALIDATION.md`
   - `SIMULATOR_PHASE2_VALIDATION.md`, if present
   - `TradingHub_Codex_Final_Implementation_Prompt.md`
   - `TradingHub_Codex_Phase2_Performance_Live_Dashboard_Prompt.md`
2. Inspect the actual implementation, not only documentation.
3. Run the current build and tests before changes.
4. Record the current state in a new report:
   - `SIMULATOR_FINAL_VALIDATION.md`
5. Make changes incrementally.
6. Add a regression test for every confirmed defect.
7. Re-run the complete solution build, tests, frontend typecheck, frontend build, and benchmark scenarios.
8. Do not report a requirement as complete unless the implementation and tests prove it.

When documentation conflicts with code, treat the code as the current truth and fix the discrepancy.

---

# 2. Primary outcomes

At the end of this pass, the system must:

1. Use one real unified historical-data path for:
   - OANDA paging;
   - cache reading;
   - incremental cache writing;
   - bounded prefetch;
   - progress reporting;
   - cancellation;
   - data-quality tracking.
2. Allow the Legacy strategy to place trades without requiring a take-profit or reward/risk prediction.
3. Correctly support:
   - `StopEntireComparison`;
   - `StopFailedStrategyOnly`;
   - shared analysis;
   - independent per-strategy analysis, or remove the misleading option.
4. Use one persistent task or one persistent dedicated thread per strategy, not one thread per frame.
5. Make completed trades and lifecycle events visible while the simulation is still running.
6. Make SignalR work in the documented Vue development setup.
7. Render a real progressive chart replay in Simulator mode.
8. Prevent stale, duplicated, or cross-job replay data.
9. Serialise and persist job progress safely.
10. Release completed-job resources.
11. Implement all exposed runtime options honestly or remove/disable them.
12. Produce measured performance and determinism evidence.
13. Produce a clean source-only deliverable.

---

# 3. Highest-priority defect: Legacy strategy cannot place trades

## 3.1 Intended Legacy behaviour

The Legacy Progressive strategy is intentionally different from the Improved strategy.

It should use:

```text
1h setup
    ↓
15m confirmation
    ↓
5m entry
    ↓
protective emergency stop
    ↓
remain open until:
    - reverse strategy close;
    - emergency stop;
    - safety close;
    - end-of-simulation liquidation.
```

The Legacy strategy does **not** require a fixed take-profit.

Therefore, a normal Legacy trade decision may legitimately contain:

```csharp
StopLossPrice = someProtectiveStop;
TakeProfitPrice = null;
ExpectedRewardRisk = null;
```

This must not be rejected merely because reward/risk cannot be calculated.

---

## 3.2 Suspected interface-dispatch defect

Inspect the current definitions of:

- `ITradingAgent`;
- `ProgressiveStrategyBase`;
- `LegacyProgressiveAgent`;
- `ImprovedProgressiveAgent`;
- any strategy factory;
- the code that derives `PreTradeRiskOptions`.

The current implementation appears to rely on a default interface property similar to:

```csharp
public interface ITradingAgent
{
    AgentExitManagementMode ExitManagementMode =>
        AgentExitManagementMode.Bracket;
}
```

The base progressive class implements `ITradingAgent`, while the Legacy derived class declares a property with the same name.

This can cause interface-based access:

```csharp
ITradingAgent agent = legacyAgent;
AgentExitManagementMode mode = agent.ExitManagementMode;
```

to resolve through the base/interface mapping rather than the derived Legacy property.

The result can be:

```text
Legacy agent
    ↓
incorrectly classified as Bracket
    ↓
Bracket risk defaults require minimum R:R
    ↓
Legacy has no take-profit
    ↓
RiskManager cannot calculate R:R
    ↓
order rejected
```

Verify this against the actual code and compiled behaviour.

Do not merely change the test to match the current result.

---

## 3.3 Required design correction

Remove the default interface implementation for exit-management mode.

Use an explicit contract:

```csharp
public interface ITradingAgent
{
    string Name { get; }

    IReadOnlySet<BarInterval> RequiredIntervals { get; }

    BarInterval TriggerInterval { get; }

    AgentExitManagementMode ExitManagementMode { get; }

    Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default);
}
```

In the progressive base class:

```csharp
public abstract class ProgressiveStrategyBase : ITradingAgent
{
    public abstract string Name { get; }

    public abstract AgentExitManagementMode ExitManagementMode { get; }

    // Existing common progressive logic...
}
```

In Legacy:

```csharp
public override AgentExitManagementMode ExitManagementMode =>
    AgentExitManagementMode.ProtectiveStopAndStrategyExit;
```

In Improved:

```csharp
public override AgentExitManagementMode ExitManagementMode =>
    AgentExitManagementMode.Bracket;
```

Other strategies must explicitly declare their intended exit mode.

Do not silently default every strategy to bracket behaviour.

---

## 3.4 Required risk configuration

The strategy/session factory must explicitly derive risk rules from the effective exit mode.

For the Legacy mode:

```csharp
new PreTradeRiskOptions
{
    RequireStopLoss = true,
    RequireTakeProfit = false,
    MinimumRewardRiskRatio = null
};
```

For bracket strategies:

```csharp
new PreTradeRiskOptions
{
    RequireStopLoss = true,
    RequireTakeProfit = true,
    MinimumRewardRiskRatio = configuredMinimumRewardRisk
};
```

The Legacy strategy must still pass:

- quantity checks;
- account balance checks;
- exposure checks;
- stop-loss validity;
- margin checks;
- safety checks.

The correction must not disable all risk management.

It should disable only target/R:R requirements that do not apply to the Legacy exit model.

---

## 3.5 Required Legacy regression tests

Add at least these tests.

### Interface dispatch

```csharp
[Test]
public void LegacyAgent_ExposesProtectiveStopAndStrategyExit_ThroughInterface()
{
    ITradingAgent agent = CreateLegacyAgent();

    Assert.That(
        agent.ExitManagementMode,
        Is.EqualTo(
            AgentExitManagementMode.ProtectiveStopAndStrategyExit));
}
```

### Improved mapping

```csharp
[Test]
public void ImprovedAgent_ExposesBracketMode_ThroughInterface()
{
    ITradingAgent agent = CreateImprovedAgent();

    Assert.That(
        agent.ExitManagementMode,
        Is.EqualTo(AgentExitManagementMode.Bracket));
}
```

### Legacy risk acceptance

Construct a valid Legacy decision containing:

```text
valid side
valid quantity
valid stop
no target
no expected R:R
```

Verify it is not rejected for:

```text
Stop-loss and take-profit prices are required to evaluate reward/risk.
```

### Improved rejection

Verify an Improved bracket trade with no target is rejected.

### End-to-end Legacy pipeline

Prove:

```text
1h setup
    ↓
15m confirmation
    ↓
5m signal
    ↓
risk approval without target
    ↓
order submitted
    ↓
position opened
```

### Legacy exit lifecycle

Verify:

- protective stop closes the trade;
- reverse strategy close closes the trade;
- end-of-simulation closes the trade;
- no fixed target is required.

### Rejection-journal assertion

Verify the Legacy journal does not contain an R:R-related rejection when the only missing field is the take-profit.

---

# 4. Unify the OANDA paging, caching, progress, and prefetch path

## 4.1 Current defect

Inspect:

- `StreamingComparativeEngine.CreatePrefetchStream`;
- `OandaStreamingCandleSource.StreamAsync`;
- `OandaStreamingCandleSource.ReadPageAsync`;
- `LowWatermarkPrefetchStream`;
- `BacktestApplicationService`.

The current engine prefers the `IPagedHistoricalCandleSource` path.

The page wrapper calls:

```csharp
ReadPageAsync(...)
```

But the new cache-reading, incremental cache-writing, and progress logic exists primarily in:

```csharp
StreamAsync(...)
```

Therefore, the normal OANDA job may bypass:

- cache reading;
- cache writing;
- `RefreshCache`;
- `NoCache`;
- source progress events.

This must be corrected.

---

## 4.2 Required unified architecture

There must be one actual production path:

```text
BacktestApplicationService
    ↓
historical source coordinator
    ↓
cache decision
    ├── valid cache → paged cache reader
    └── no valid cache → paged OANDA source
                         + incremental temporary cache writer
    ↓
low-watermark bounded prefetch
    ↓
StreamingComparativeEngine
```

Do not maintain separate partially overlapping paths where one supports caching and another supports low-watermark paging.

Suggested contracts:

```csharp
public interface IPagedHistoricalCandleSource
{
    ValueTask<HistoricalCandlePage> ReadPageAsync(
        HistoricalPageRequest request,
        CancellationToken cancellationToken = default);
}
```

```csharp
public interface IHistoricalCandleCoordinator
{
    IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        IProgress<HistoricalSourceProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
```

The coordinator should own:

- cache selection;
- page cursor;
- low-watermark acquisition;
- incremental cache writer;
- source progress;
- normalisation;
- duplicate removal;
- data-quality tracking.

---

## 4.3 First-run behaviour

On a first uncached run:

```text
request page 1
    ↓
normalise
    ↓
append to temporary cache
    ↓
yield immediately to simulator
    ↓
request later pages according to low-watermark/backpressure
```

The simulator must not wait until the full year is downloaded before processing begins.

The current simulation must not reread the newly written full cache merely to process the same candles.

---

## 4.4 Cached-run behaviour

On a valid cache:

```text
validate manifest and data file
    ↓
stream cached pages/chunks
    ↓
use same normalised MarketCandle representation
    ↓
produce same InputHash
```

Repeat runs must avoid OANDA requests unless refresh is explicitly requested.

---

## 4.5 Required tests

Add production-path integration tests proving:

- first OANDA/fake page reaches the engine before all pages complete;
- cache is written while the same run processes candles;
- second run reads the cache;
- `NoCache` avoids cache read/write;
- `RefreshCache` ignores existing cache and replaces it atomically;
- source progress reaches the job snapshot;
- cancellation aborts the temporary cache;
- the final committed cache produces the same input hash.

Do not test only a standalone fake paged source while leaving the application-service default path untested.

---

# 5. Implement real low-watermark page prefetch

Ensure `PrefetchLowWatermark` actually controls when the next page is acquired.

Required semantics:

```text
unread > low watermark
    → no additional page request

unread <= low watermark
    → request next page if:
        - no page request is active;
        - source is not complete;
        - capacity is available

buffer full
    → producer waits

source complete
    → drain remaining data
    → complete normally
```

Expose:

- pages requested;
- pages completed;
- unread count;
- peak unread count;
- producer waits;
- consumer waits;
- capacity;
- low watermark;
- cache/source mode.

Add tests for:

- exact threshold;
- no duplicate page request;
- no unread overwrite;
- bounded memory;
- slow consumer;
- slow source;
- cancellation;
- final partial page.

---

# 6. Correct persistent strategy workers

## 6.1 Task mode

Use one persistent async worker task per strategy.

The worker must:

- start once;
- own one `StrategySimulationSession`;
- own one bounded channel;
- process frames sequentially;
- reject duplicate/out-of-order sequences;
- produce one result per accepted frame;
- complete cleanly on channel completion/cancellation.

---

## 6.2 Dedicated-thread mode

The current `LongRunning` async loop does not guarantee that continuations stay on the same OS thread.

Choose one honest implementation.

### Option A — true dedicated thread

Use one synchronous blocking worker loop on one long-running thread.

For example, use blocking reads around a dedicated queue or carefully bridge the channel without allowing the worker body to migrate.

### Option B — remove/rename

If a true dedicated thread is not worth the complexity, remove or rename the option so it does not promise thread affinity.

Do not call it `DedicatedThread` unless tests prove one persistent thread identity.

---

## 6.3 Barrier

Retain:

```text
publish frame N to every active strategy
    ↓
await every active result
    ↓
validate sequence N
    ↓
commit replay
    ↓
advance to N + 1
```

---

## 6.4 Metrics

Capture:

- worker mode;
- processed frames;
- total duration;
- average duration;
- maximum duration;
- channel occupancy;
- peak occupancy;
- producer wait;
- worker idle time;
- failure count;
- thread ID in true dedicated-thread mode.

---

## 6.5 Tests

Verify:

- one persistent worker per strategy;
- bounded number of threads;
- exact-once frame processing;
- no frame reordering;
- cancellation joins workers;
- no worker survives disposal;
- task and dedicated modes produce identical canonical results.

---

# 7. Implement `StopFailedStrategyOnly`

## 7.1 Current defect

A worker exception faults the frame task.

`Task.WhenAll` then throws before the later failure-policy code can continue.

The sequential path also propagates directly.

Therefore:

```text
StopFailedStrategyOnly
```

currently behaves like:

```text
StopEntireComparison
```

---

## 7.2 Required behaviour

### `StopEntireComparison`

- cancel all active workers;
- mark the job failed;
- retain partial replay as incomplete;
- report strategy, sequence, timestamp, setup, position, pending orders, and exception.

### `StopFailedStrategyOnly`

- mark only that strategy failed/incomplete;
- remove it from future barriers;
- record its last processed sequence;
- retain its partial output;
- continue remaining strategies;
- exclude the failed strategy from full-period comparison;
- display the incomplete state clearly.

Do not silently convert failures into successful empty results.

---

## 7.3 Tests

Add sequential and parallel tests for both policies.

Use a deterministic test strategy that throws at a known sequence.

---

# 8. Fix or remove `IndependentPerStrategy` analysis mode

The current engine updates per-strategy annotators but strategies still read the centrally shared:

```csharp
frame.Snapshots
```

This means the mode is not actually independent.

Choose one:

## Full implementation

Create a strategy-specific frame/context containing that session’s own snapshot set.

For example:

```csharp
public sealed record StrategyMarketFrame
{
    public required MarketFrame SharedMarket { get; init; }

    public required AnalysisSnapshotSet Analysis { get; init; }
}
```

## Removal

Remove/disable `IndependentPerStrategy` from:

- runtime options;
- CLI;
- API;
- Dashboard;
- documentation.

Do not retain a misleading mode that only performs duplicate calculations.

Add tests if retained.

---

# 9. Correct all exposed runtime options

Audit every option and ensure it has a real effect.

## `PrefetchLowWatermark`

Must control page acquisition.

## `StrategyChannelCapacity`

Must control actual worker channels.

## `WarmupBaseCandleCount`

Implement correct behaviour or remove it.

If retained, define precedence with `WarmupDays`.

Prefer readiness-based warm-up where possible.

## `NearestToOpenFirst`

Implement real nearest-boundary selection.

## `UseHistoricalBidAsk`

Either implement actual OANDA bid/ask components or disable/remove the option until complete.

## `DeterministicSeed`

It is currently accepted but apparently unused.

Either use it for real stochastic components or remove it from:

- CLI;
- API;
- runtime options;
- Dashboard.

Do not expose placebo configuration.

---

# 10. Correct `NearestToOpenFirst`

## 10.1 Current defect

The current order sorting applies stop/target priority before distance.

Therefore, stop wins even when target is closer.

---

## 10.2 Required algorithm

When both stop and target are touched in the same candle:

### Conservative

Stop first.

### Optimistic

Target first.

### Nearest to open

1. Determine the executable candle-open side.
2. Calculate absolute distance to executable stop.
3. Calculate absolute distance to executable target.
4. Select the smaller distance.
5. On exact tie, use stop-first.

For long positions, use the appropriate bid-side execution.

For short positions, use the appropriate ask-side execution.

When historical bid/ask is unavailable, use the configured synthetic executable prices consistently.

---

## 10.3 Required journal fields

Record:

- ambiguity occurred;
- selected policy;
- open price;
- stop distance;
- target distance;
- chosen boundary;
- tie status.

---

## 10.4 Tests

Add long and short tests for:

- stop nearer;
- target nearer;
- exact tie;
- synthetic spread;
- historical bid/ask when available.

---

# 11. Make source progress authoritative

The job must accurately report:

```text
PreparingData
DownloadingData
LoadingCache
WarmingUp
Running
Exporting
Completed
```

Do not infer source state only from candle timestamps.

Wire actual coordinator/source progress into:

- job snapshots;
- SignalR;
- CLI;
- Dashboard.

Progress should contain:

```csharp
public sealed record HistoricalSourceProgress
{
    public required HistoricalSourcePhase Phase { get; init; }

    public required bool FromCache { get; init; }

    public required long CandlesRead { get; init; }

    public required int PagesRead { get; init; }

    public DateTimeOffset? LatestCandle { get; init; }

    public long? EstimatedCandles { get; init; }

    public decimal? Percent { get; init; }
}
```

Use a trading-session-aware estimate where possible rather than raw calendar minutes.

---

# 12. Serialise job state updates and persistence

## 12.1 Current problem

There are still fire-and-forget calls such as:

```csharp
_ = PersistAsync(...);
```

Source and engine callbacks can update the same snapshot concurrently.

The coalescing store may create many competing `Task.Run` operations.

---

## 12.2 Required model

Use one serialised state reducer per job.

All updates should pass through one owned channel/actor:

```text
source progress
engine progress
status change
pause/resume
terminal result
    ↓
single job state reducer
    ↓
monotonic revision
    ↓
SignalR publish
    ↓
coalesced persistence
```

Requirements:

- one ordered state mutation path;
- one persistence operation per job at a time;
- older revisions cannot overwrite newer revisions;
- terminal revisions are awaited;
- errors are observed;
- no unbounded task creation;
- progress update frequency is configurable.

---

## 12.3 Tests

Verify:

- monotonic revision;
- stale update rejection;
- terminal snapshot persistence;
- concurrent source/engine updates;
- persistence failure handling;
- no fire-and-forget exception loss.

---

# 13. Completed-job resource lifecycle

After terminal completion:

1. persist final snapshot;
2. persist result/output metadata;
3. complete CLI/job completion TCS;
4. publish terminal SignalR event;
5. remove from `_running`;
6. dispose cancellation resources;
7. dispose pause resources;
8. dispose worker hosts;
9. release large in-memory results.

Later API queries should use persisted files/repository.

Define terminal control behaviour explicitly:

- pause completed → conflict;
- resume completed → conflict;
- cancel completed → no-op or conflict;
- cancel queued → mark cancelled and skip execution;
- pause queued → supported explicitly or rejected.

Add direct tests proving `_running` no longer holds the completed job.

---

# 14. Async safe-frame pause

Use an async pause gate.

Pause must take effect only:

```text
after frame N is fully committed
before frame N + 1 begins
```

Never pause halfway through:

- order execution;
- OCO resolution;
- account mutation;
- strategy barrier;
- replay write.

Cancellation must release a paused wait.

Add tests during:

- download;
- cache load;
- warm-up;
- strategy processing;
- export;
- already-paused state;
- cancellation.

---

# 15. Make trades available while running

## 15.1 Current defect

Trades are stored in memory and consolidated only during final completion.

The running trades API therefore does not provide completed trades incrementally.

---

## 15.2 Required output

Use an append-only incremental trade journal.

Options:

- JSONL trade chunks;
- bounded batches;
- indexed trade-event files.

When a trade closes:

1. write/append the completed trade;
2. atomically update its index;
3. publish `TradeCompleted`;
4. make the API return it immediately.

A final consolidated file may still be produced during export.

---

## 15.3 Required API

Support:

```text
GET /api/simulations/{id}/trades?cursor=...&limit=...
```

Return:

```json
{
  "items": [],
  "nextCursor": "...",
  "hasMore": true
}
```

Do not reread all historical trades for every poll.

---

# 16. Event-driven strategy replay

Do not write nearly identical strategy state for every base candle.

Create lifecycle events:

```csharp
public enum StrategyReplayEventType
{
    SetupCreated,
    SetupAdvanced,
    SetupExpired,
    SetupInvalidated,
    SignalCreated,
    RiskRejected,
    OrderSubmitted,
    OrderRejected,
    OrderFilled,
    PositionOpened,
    StopUpdated,
    TargetUpdated,
    PartialExit,
    StrategyCloseRequested,
    PositionClosed,
    TradeCompleted,
    StrategyFailed
}
```

Each event should include:

- simulation ID;
- strategy;
- sequence;
- time;
- setup ID;
- stage;
- source timeframe;
- side;
- price;
- quantity;
- stop;
- target;
- reason;
- confidence;
- R:R where applicable.

Use periodic downsampled balance/equity samples separately.

This reduces output size and enables meaningful chart overlays.

---

# 17. Replay chunk index and bounded APIs

Create an atomic replay index:

```csharp
public sealed record ReplayChunkDescriptor
{
    public required string ChunkId { get; init; }

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

After atomic chunk creation:

1. calculate descriptor;
2. update index atomically;
3. publish `ReplayChunkAvailable`;
4. allow clients to fetch it.

Bound all replay APIs.

Do not materialise a complete one-year replay in one response.

Remove sync-over-async from request handlers.

Validate chunk IDs and prevent path traversal.

---

# 18. Fix SignalR in Vue development mode

## 18.1 Current problem

The Vue client uses:

```text
/hubs/simulations
```

But Vite proxies only `/api`.

---

## 18.2 Required correction

Add a Vite proxy for `/hubs` with WebSocket support.

Example concept:

```ts
proxy: {
  "/api": {
    target: backendUrl,
    changeOrigin: true
  },
  "/hubs": {
    target: backendUrl,
    changeOrigin: true,
    ws: true
  }
}
```

Alternatively, configure an explicit backend hub base URL.

Required behaviour:

- connect;
- join selected job group;
- reconnect automatically;
- rejoin after reconnect;
- ignore stale revisions;
- dispose on job change/unmount;
- use slow polling only as fallback.

Add frontend tests or composable tests.

---

# 19. Fix Simulator job switching and replay duplication

When selecting another simulation, clear all job-specific state:

- replay rows;
- loaded chunks;
- trades;
- setup events;
- cursor;
- follow-live state;
- chart annotations;
- in-flight request state.

Use composite chunk identity:

```text
simulationId + chunkId
```

Serialise replay loading.

Prevent two simultaneous fetches from appending the same chunk.

De-duplicate rows by:

```text
simulation ID + sequence
```

Sort deterministically after append.

Add tests for:

- rapid job switching;
- repeated chunk notifications;
- overlapping polling and SignalR events;
- retry after failed chunk download.

---

# 20. Implement a real Simulator chart

Use the existing `AnalysisChart.vue` where practical.

The Simulator must show a real chart, not only textual OHLC fields.

Display:

- candles;
- setup markers;
- 1h partial;
- 15m confirmation;
- 5m signal;
- risk rejection;
- order submission;
- entry;
- protective stop;
- target;
- updated stop;
- exit;
- trade path;
- net P/L;
- realised R.

Also support annotation deltas:

- swings;
- support/resistance zones;
- trendlines;
- channels;
- market structure;
- indicators.

Do not send full snapshots every minute.

Use interval-close annotation deltas with stable IDs.

Maintain:

- a bounded detailed chart window;
- a downsampled full-range overview;
- optional one-minute detail around trades.

---

# 21. Functional playback

The playback engine must support:

- play;
- pause;
- step forward;
- step back;
- speed control;
- jump to setup;
- jump to confirmation;
- jump to signal;
- jump to entry;
- jump to exit;
- follow latest;
- scrub backwards;
- resume follow-latest.

Separate:

```text
live follow mode
historical playback mode
```

When following latest, do not repeatedly reset the cursor in a way that prevents smooth playback through buffered rows.

Use one stable animation/timer mechanism.

---

# 22. Cache integrity

Use one authoritative cache manifest.

Validate:

- schema;
- broker;
- environment;
- instrument;
- interval;
- range;
- warm-up range where part of the request;
- price components;
- count;
- first candle;
- last candle;
- full content hash;
- compressed-stream integrity;
- completion flag.

Reject:

- truncated gzip;
- hash mismatch;
- count mismatch;
- timestamp mismatch;
- missing manifest;
- incomplete temporary file.

Wrap cache writer/source lifetime in `try/finally`.

Abort and clean temporary files on:

- cancellation;
- HTTP failure;
- parsing failure;
- disk failure;
- hash failure.

---

# 23. Correct input identity

Separate:

```text
SimulationId
InputRequestId
SimulationConfigurationId
InputHash
```

`InputRequestId` must represent the actual candle request:

- broker;
- environment;
- instrument;
- base interval;
- actual stream start including warm-up;
- end;
- price components;
- schema/source version.

It must not include strategy names.

`SimulationConfigurationId` may include:

- strategies;
- risk settings;
- spread;
- slippage;
- account;
- ambiguity policy.

`InputHash` must hash the actual emitted normalised candle stream.

---

# 24. Historical bid/ask

Implement only if the current OANDA endpoint and model support it correctly.

When implemented:

- request midpoint, bid, and ask components;
- cache price components distinctly;
- use ask for buy entry;
- use bid for sell entry;
- use executable side for exits;
- use executable high/low for stop/target detection;
- mark the fill model `HistoricalBidAsk`.

Until implemented:

- disable the Dashboard control;
- do not default it to enabled;
- label results honestly as midpoint plus configured spread/slippage;
- remove misleading metadata.

---

# 25. MFE, MAE, and performance metrics

Track per open position:

```text
maximum favourable excursion
maximum adverse excursion
```

Use only candles while the position is open.

Include in the trade journal:

- MFE price;
- MFE amount;
- MFE R;
- MAE price;
- MAE amount;
- MAE R.

Compute and expose:

- net P/L;
- gross P/L;
- commissions;
- trade count;
- wins;
- losses;
- win rate;
- profit factor;
- average R;
- median R;
- expectancy;
- maximum drawdown;
- average holding time;
- average setup time;
- exit-reason distribution;
- ambiguity count;
- MFE/MAE summaries.

Display these in the strategy comparison panel.

---

# 26. Performance instrumentation

Add low-overhead stage metrics for:

- OANDA HTTP;
- cache read;
- cache write;
- prefetch wait;
- aggregation;
- ATR;
- RSI;
- Bollinger;
- swing detection;
- structure;
- support/resistance;
- RANSAC;
- channel detection;
- confidence scoring;
- frame construction;
- strategy processing;
- broker execution;
- barrier wait;
- replay serialisation;
- compression;
- job persistence;
- SignalR publication.

Expose:

```csharp
public sealed record BacktestStageMetrics
{
    public required string Stage { get; init; }

    public required long InvocationCount { get; init; }

    public required TimeSpan TotalDuration { get; init; }

    public required TimeSpan AverageDuration { get; init; }

    public required TimeSpan MaximumDuration { get; init; }
}
```

Measure before optimising ChartAnnotator algorithms.

Do not blindly rewrite DBSCAN/RANSAC.

---

# 27. Determinism

Create a canonical result fingerprint covering:

- setup stages;
- setup timestamps;
- signal timestamps;
- risk decisions;
- order eligibility sequence;
- fills;
- entry/exit prices;
- stops;
- targets;
- close reasons;
- commissions;
- P/L;
- R;
- balances;
- ledger entries;
- data-quality report.

Compare:

- sequential;
- parallel task workers;
- true persistent dedicated threads, if retained.

All semantic fingerprints must match.

Random raw IDs may be normalised/excluded only when their semantic relationships are separately verified.

---

# 28. No-lookahead tests

Strengthen tests so they prove:

- an order generated from frame `N` is not eligible until frame `N + 1`;
- signal time cannot equal an earlier fill time;
- a signal candle cannot fill its own new order through its prior high/low;
- stops/targets use only information known at the decision time;
- swing confirmation uses required right-hand candles;
- simultaneous interval closes update all snapshots before one evaluation.

Do not use assertions that allow same-frame self-fill.

---

# 29. Job recovery

On server startup, persisted jobs left in a non-terminal state must not remain falsely “Running”.

At minimum mark them:

```text
Failed / Interrupted
```

with:

- restart reason;
- last revision;
- last sequence;
- partial output location.

Optionally support deterministic resume only if full state restoration is implemented.

Add output retention and cleanup settings.

---

# 30. API error handling

Map expected errors to structured problem responses.

Examples:

- completed job pause → HTTP 409;
- unknown job → HTTP 404;
- invalid request → HTTP 400;
- unavailable historical source → HTTP 503 or documented equivalent;
- cancelled job → correct terminal representation.

Do not expose credentials, tokens, or sensitive HTTP headers.

Respect `Retry-After` on OANDA rate limits.

Do not retry permanent authentication or invalid-instrument errors as transient failures.

---

# 31. Security and packaging

Search for secrets in:

- source;
- `.env*`;
- logs;
- test data;
- cache metadata;
- replay output;
- docs.

Keep only placeholder example files.

Produce a clean source-only archive excluding:

```text
bin/
obj/
node_modules/
dist/
.cache/
simulation outputs/
temporary cache files/
credential files/
```

The current deliverable contains build outputs and dependencies; correct that.

---

# 32. Required backend tests

Add tests for:

## Legacy strategy

- interface exit-mode mapping;
- no-target risk acceptance;
- end-to-end order placement;
- reverse close;
- protective stop;
- end-of-simulation close.

## OANDA/cache path

- first-page processing before full download;
- cache write during simulation;
- cached second run;
- refresh;
- no-cache;
- cancellation cleanup;
- same input hash.

## Low-watermark

- threshold;
- page request count;
- backpressure;
- bounded occupancy;
- no overwrite.

## Workers

- persistent task;
- true dedicated thread or removed mode;
- barrier;
- exact-once;
- failure policies;
- shutdown.

## Analysis mode

- shared mode;
- independent mode if retained.

## Job state

- monotonic revision;
- serialised updates;
- completed removal;
- restart recovery.

## Replay

- bounded queries;
- chunk index;
- incremental trades;
- lifecycle events;
- partial running output;
- path traversal rejection.

## Execution

- nearest-to-open;
- conservative;
- optimistic;
- long/short;
- bid/ask or synthetic spread.

## Determinism

- canonical fingerprint across modes.

## No lookahead

- strict next-frame eligibility.

---

# 33. Required frontend tests

Test:

- SignalR connection through dev proxy;
- reconnect/rejoin;
- stale revision ignored;
- fallback polling;
- job switch state reset;
- chunk de-duplication;
- in-flight load serialisation;
- playback timing;
- pause;
- step;
- jump actions;
- follow-latest;
- scrub/resume;
- trade event appearing while running;
- real chart integration;
- bounded chart window;
- cleanup on unmount.

---

# 34. Benchmarks

Run:

## Synthetic

- 500,000 one-minute candles;
- two strategies;
- shared analysis;
- replay enabled;
- no network.

## Cached one month

- sequential;
- parallel tasks;
- dedicated thread if retained.

## First-run paged source

Measure:

- time to first downloaded page;
- time to first warm-up candle;
- time to first evaluation candle;
- time to first replay chunk;
- time to first completed trade;
- total duration.

Record:

- candles/sec;
- peak memory;
- GC counts;
- cache read/write;
- annotation time;
- strategy time;
- barrier wait;
- replay size;
- persistence time.

State honestly whether parallel mode improves runtime for the two current strategies.

---

# 35. Dashboard acceptance scenario

The completed system must support:

1. Start DashboardLive and Vue.
2. Open Simulator.
3. Configure a one-year GBP/JPY run.
4. Select Legacy and Improved.
5. Start and immediately receive a job ID.
6. See true source phase.
7. See first candles before full download.
8. See warm-up progress.
9. See real chart replay while running.
10. See Legacy setups and orders.
11. Verify Legacy can open without target/R:R.
12. See Improved bracket orders.
13. See completed trades before final completion.
14. See lifecycle markers.
15. Pause/resume visual playback.
16. Pause/resume backend safely.
17. Change speed.
18. Scrub backward.
19. Follow latest.
20. Switch jobs without mixed replay.
21. Refresh and reconnect.
22. Cancel cleanly.
23. Complete run.
24. Compare rich metrics.
25. Replay a selected trade setup-to-exit.
26. Rerun from cache.
27. Verify same input hash.
28. Compare sequential and parallel canonical fingerprints.

---

# 36. Validation commands

Run:

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

Run:

- Legacy end-to-end test;
- default application-service cache test;
- sequential/parallel determinism test;
- failure-policy test;
- nearest-to-open tests;
- frontend SignalR test;
- one-month benchmark.

Do not claim a command passed unless it was actually executed.

---

# 37. Required final response from Codex

Return:

1. Files changed, grouped by project.
2. Confirmed root cause of the Legacy no-trade problem.
3. Exact Legacy fix.
4. Exact OANDA/cache path fix.
5. Failure-policy fix.
6. Analysis-sharing fix or removed option.
7. Dedicated-thread decision and evidence.
8. SignalR/Vite fix.
9. Incremental trade/replay implementation.
10. Real chart integration summary.
11. Build result.
12. Test totals.
13. Frontend typecheck/build result.
14. Benchmark table.
15. Time to first replay/trade.
16. Sequential/parallel comparison.
17. Peak memory.
18. Replay size.
19. Secret/package hygiene result.
20. Remaining limitations.
21. Any uncompleted requirement stated explicitly.

Do not return only another architecture document.

---

# 38. Priority order

Implement in this order:

1. Fix Legacy exit-mode interface dispatch and R:R rejection.
2. Add Legacy end-to-end trade regression tests.
3. Unify the actual OANDA/cache/prefetch/progress path.
4. Correct `NearestToOpenFirst`.
5. Implement `StopFailedStrategyOnly`.
6. Correct/remove independent analysis mode.
7. Correct dedicated-thread semantics.
8. Serialise job state and persistence.
9. Make completed trades incremental.
10. Fix SignalR through Vite.
11. Fix replay job switching and duplication.
12. Add event-driven lifecycle replay.
13. Add replay index and bounded APIs.
14. Integrate the real chart.
15. Add annotation deltas.
16. Add MFE/MAE and rich metrics.
17. Add performance instrumentation and benchmarks.
18. Strengthen determinism/no-lookahead tests.
19. Fix cache integrity and restart recovery.
20. Produce a clean source-only archive.

Correctness and honest behaviour take priority over adding more concurrency.
