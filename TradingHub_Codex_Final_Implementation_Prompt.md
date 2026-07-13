# TradingHub Simulator Modernisation — Final Codex Implementation Brief

## Role

Act as a senior .NET architect, quantitative backtesting engineer, concurrency specialist, and full-stack developer.

You are working on the existing **TradingHub** solution. Before changing code, inspect the full repository and understand the current implementation of:

- `BacktestRunner`
- `Simulator`
- `Agent`
- `ChartAnnotator`
- `Brokers`
- `Brokers.Oanda`
- `RiskManager`
- `ExecutionManager`
- `DashboardLive`
- `Dashboard`
- replay/export code
- strategy implementations
- tests
- configuration
- current ring-buffer implementations
- current candle aggregation
- current order, position, OCO, stop-loss, take-profit, and trade-journal logic

Do not create a disconnected second simulator. Improve the existing implementation and preserve correct project boundaries.

The final result must make the **Dashboard Simulator mode** the primary user experience for running, watching, pausing, replaying, comparing, and analysing historical simulations.

The CLI should remain available, but only as a thin wrapper around the same application service used by the dashboard.

---

# 1. Primary objective

Redesign the backtesting system so that it behaves like a live market replay rather than a batch job that loads one year of data, blocks for a long time, and displays results only at the end.

The required experience is:

1. The user opens the Dashboard.
2. The user selects:
   - instrument;
   - date range;
   - base execution interval;
   - analysis intervals;
   - strategies;
   - account settings;
   - spread/slippage/commission settings;
   - warm-up settings;
   - ambiguity policy;
   - strategy execution mode.
3. The user clicks **Run Simulation**.
4. The backend immediately creates a simulation job and returns a simulation ID.
5. The dashboard immediately shows:
   - job status;
   - data download/cache status;
   - progress;
   - current historical market time;
   - candles processed;
   - candles per second;
   - current balance and equity per strategy;
   - open positions;
   - active setups;
   - completed trades;
   - latest replay window.
6. The backend processes historical one-minute candles as quickly as correctness allows.
7. The dashboard progressively displays candles, setup stages, entries, stops, targets, exits, P/L, R-multiple, balance, drawdown, and comparison metrics.
8. The user can control visual playback independently from backend computation speed.
9. The result remains reproducible and deterministic.
10. The same simulation can be rerun from cached candles without redownloading OANDA data.

The Agent must not know whether candles come from:

- a live broker stream;
- OANDA historical paging;
- a local compressed cache;
- an integration test;
- a fake test source.

It must receive only completed chronological market events through a common abstraction.

---

# 2. Preserve project responsibilities

Keep these responsibilities separate:

```text
Brokers / OANDA
    Historical and live market-data retrieval
    OANDA paging, retry, rate-limit handling
    Bid/ask/mid candle retrieval
    Data-source adapters

Simulator
    Historical clock
    Market-frame production
    Order fills
    OCO behaviour
    account/position lifecycle
    replay/job orchestration
    deterministic backtest progression

ChartAnnotator
    ATR, RSI, Bollinger
    swings
    support/resistance
    trendlines
    channels
    market structure
    annotation snapshots

Agent
    Multi-timeframe setup progression
    signal generation
    trade-plan generation
    strategy-driven exit decisions

RiskManager
    exposure checks
    quantity/position sizing
    risk approval
    portfolio constraints

ExecutionManager
    order creation
    order lifecycle
    close/reduce-position requests
    reconciliation abstractions

DashboardLive
    simulation API
    job queue
    SignalR/SSE
    replay-range endpoints
    runtime control endpoints

Dashboard
    configuration UI
    progress UI
    visual playback
    strategy comparison
    chart overlays
    trade journal
```

Do not move execution-safety or reconciliation responsibilities into the Agent.

Do not let the Dashboard directly execute simulation logic.

---

# 3. Refactor the existing CLI runner into an application service

The current runner logic must be moved into a reusable application service.

Create or improve an abstraction similar to:

```csharp
public interface IBacktestApplicationService
{
    Task<SimulationJobHandle> StartAsync(
        BacktestRequest request,
        CancellationToken cancellationToken = default);

    Task<SimulationJobSnapshot?> GetAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    Task PauseAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    Task ResumeAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default);
}
```

Both entry points must call this same service:

```text
Dashboard API ─┐
               ├── BacktestApplicationService
CLI ───────────┘
```

The CLI must become a thin adapter that:

- parses arguments;
- constructs `BacktestRequest`;
- starts a job;
- displays progress;
- waits for completion;
- returns a useful process exit code.

Do not maintain separate dashboard and CLI implementations.

---

# 4. Dashboard-first simulation jobs

The normal workflow should be initiated from Dashboard Simulator mode.

Create a persistent simulation-job model:

```csharp
public enum SimulationJobStatus
{
    Queued,
    PreparingData,
    DownloadingData,
    LoadingCache,
    WarmingUp,
    Running,
    Paused,
    Cancelling,
    Cancelled,
    Exporting,
    Completed,
    Failed
}
```

A job snapshot should contain at least:

```csharp
public sealed record SimulationJobSnapshot
{
    public required Guid Id { get; init; }

    public required SimulationJobStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public required InstrumentKey Instrument { get; init; }

    public required DateTimeOffset RequestedFrom { get; init; }

    public required DateTimeOffset RequestedTo { get; init; }

    public DateTimeOffset? WarmupFrom { get; init; }

    public DateTimeOffset? CurrentMarketTime { get; init; }

    public required long ProcessedBaseCandles { get; init; }

    public long? EstimatedBaseCandleCount { get; init; }

    public required decimal ProgressPercent { get; init; }

    public required decimal CandlesPerSecond { get; init; }

    public required IReadOnlyList<StrategyProgressSnapshot> Strategies { get; init; }

    public string? Error { get; init; }

    public bool IsComplete { get; init; }
}
```

The browser must not hold a single long HTTP request open for the full simulation.

`POST /api/simulations` should quickly return:

```json
{
  "simulationId": "00000000-0000-0000-0000-000000000000",
  "status": "Queued"
}
```

Use a bounded background job queue.

Do not use fire-and-forget tasks without ownership and cancellation.

Persist enough job state so that refreshing the dashboard does not lose:

- job status;
- configuration;
- progress;
- error state;
- output paths;
- strategy results;
- trade records.

A file-backed repository is acceptable initially, but use a proper abstraction so it can later be replaced by a database.

---

# 5. API requirements

Implement or improve these endpoints:

```text
POST   /api/simulations
GET    /api/simulations
GET    /api/simulations/{id}

POST   /api/simulations/{id}/pause
POST   /api/simulations/{id}/resume
POST   /api/simulations/{id}/cancel

GET    /api/simulations/{id}/strategies
GET    /api/simulations/{id}/trades
GET    /api/simulations/{id}/performance

GET    /api/simulations/{id}/replay
GET    /api/simulations/{id}/replay?from=...&to=...
GET    /api/simulations/{id}/replay/chunks
GET    /api/simulations/{id}/replay/chunks/{chunkId}
```

Use SignalR or Server-Sent Events for live updates.

Prefer SignalR if the solution already uses ASP.NET Core and may later require bidirectional controls.

Push events such as:

```text
SimulationStatusChanged
SimulationProgressChanged
StrategyProgressChanged
SetupCreated
SetupAdvanced
SetupInvalidated
OrderCreated
OrderFilled
PositionOpened
PositionUpdated
PositionClosed
TradeCompleted
ReplayChunkAvailable
SimulationCompleted
SimulationFailed
```

Do not push every one-minute candle as a large full snapshot if that overloads the browser.

Use compact events and replay chunks.

---

# 6. Decouple backend computation from visual playback

The backend should process historical candles as fast as possible while preserving correctness.

The dashboard should visually replay the result at a user-controlled speed.

These are different controls:

```text
Pause simulation
Resume simulation

Pause playback
Resume playback
Step one candle
```

Pausing visual playback must not automatically pause backend processing.

Support playback speeds such as:

```text
1×
5×
10×
25×
50×
100×
Maximum
```

Also support:

```text
Jump to next setup
Jump to next confirmation
Jump to next entry
Jump to next exit
Jump to next losing trade
Jump to next winning trade
Jump to completion
```

The dashboard should progressively show available replay while the backend continues computing.

---

# 7. One-minute candles as the canonical simulation clock

Use completed one-minute candles as the canonical market stream.

Do not independently download 5m, 15m, and 1h historical datasets for normal simulation.

The one-minute stream must drive:

- order execution;
- stop-loss checks;
- take-profit checks;
- gap behaviour;
- OCO behaviour;
- MAE/MFE calculations;
- 5m aggregation;
- 15m aggregation;
- 1h aggregation;
- progress;
- replay.

Each simulation step should conceptually be:

```text
Read next completed 1m candle
    ↓
Validate chronology and sequence
    ↓
Process existing orders and open positions
    ↓
Apply fills/stops/targets/strategy exits
    ↓
Update account, margin, equity, trade lifecycle
    ↓
Feed candle into multi-timeframe aggregator
    ↓
Collect newly completed 5m/15m/1h candles
    ↓
Update all relevant annotation snapshots
    ↓
Construct one immutable MarketFrame
    ↓
Evaluate strategies
    ↓
Create orders eligible from the next 1m candle
    ↓
Write compact replay/performance events
    ↓
Advance to next 1m candle
```

This ordering is mandatory to prevent lookahead.

An order created after candle `N` closes must not fill using candle `N` high/low.

It becomes eligible on candle `N + 1`.

---

# 8. OANDA historical streaming

Do not load a full year into a `List<Candle>` before simulation.

Create or improve:

```csharp
public interface IHistoricalCandleStream
{
    IAsyncEnumerable<MarketCandle> StreamAsync(
        HistoricalCandleRequest request,
        CancellationToken cancellationToken = default);
}
```

Suggested request:

```csharp
public sealed record HistoricalCandleRequest(
    InstrumentKey Instrument,
    BarInterval BaseInterval,
    DateTimeOffset From,
    DateTimeOffset To,
    bool RefreshCache = false,
    bool UseHistoricalBidAsk = true);
```

The OANDA source must:

- page within OANDA request limits;
- use UTC consistently;
- request completed candles only;
- sort chronologically;
- remove page-boundary duplicates;
- detect out-of-order candles;
- stop exactly at the requested end;
- support cancellation;
- retry transient HTTP failures;
- use bounded exponential backoff;
- respect rate limits;
- expose download progress;
- expose data-quality statistics;
- support compressed caching;
- avoid logging secrets.

Do not commit OANDA credentials.

Read credentials from environment variables or secure configuration.

---

# 9. Bid/ask support and realistic spread

Where possible, retrieve historical:

- midpoint candles;
- bid candles;
- ask candles.

Use a compatible envelope:

```csharp
public sealed record MarketCandle
{
    public required Candle Mid { get; init; }

    public Candle? Bid { get; init; }

    public Candle? Ask { get; init; }
}
```

Use bid/ask data for fills when available.

Examples:

```text
Buy market entry → ask
Sell market entry → bid

Long stop/exit → bid
Short stop/exit → ask
```

When bid/ask history is unavailable, use the configured spread approximation.

Every result must record which fill model was used:

```text
HistoricalBidAsk
MidpointPlusConfiguredSpread
SyntheticSpreadModel
```

---

# 10. Cache design

Use a versioned compressed cache.

Cache keys must include:

```text
broker
environment
instrument
base interval
from
to
price components
schema version
data-source version
```

Cache metadata must include:

- first candle;
- last candle;
- candle count;
- hash;
- data-quality summary;
- creation time;
- requested price components.

The cache should be read as a stream.

Do not decompress the whole year into memory.

Support:

```text
--refresh
--no-cache
```

Repeated runs with the same cache and configuration must use identical candle data.

---

# 11. Ring buffer and bounded prefetch

Use ring buffers for active bounded state, but do not confuse physical array position with logical stream position.

Do not implement:

> “When the consumer reaches the beginning of the ring, download more candles.”

That is unsafe because circular buffers wrap and overwrite.

Track:

- absolute sequence number;
- logical read sequence;
- logical write sequence;
- unread count;
- capacity;
- completed state.

Use a low-watermark prefetch design.

Example:

```text
Source page size:          5,000
Prefetch capacity:        20,000
Prefetch low watermark:    5,000
```

When unread count falls below the low watermark, begin loading the next page/cache chunk.

Never overwrite unread data.

Use backpressure.

A bounded `Channel<MarketCandle>` is acceptable for producer/consumer coordination.

Use ring buffers for:

- active candle history;
- indicators;
- swings;
- chart annotations;
- short replay windows;
- recent equity points.

Separate:

## Source storage

Paged OANDA/cache stream.

## Active analysis storage

Bounded ring buffers.

## Persistent replay storage

Chunked output written incrementally.

Do not store the entire year of full `AnalysisSnapshot` object graphs in memory.

---

# 12. Multi-timeframe aggregation

Aggregate configured intervals from the one-minute stream.

The aggregator must:

- align boundaries in UTC;
- use first open;
- max high;
- min low;
- last close;
- sum volume;
- emit only completed candles;
- never expose incomplete analysis candles to the Agent;
- handle weekend/session gaps;
- not fabricate missing one-minute candles;
- identify incomplete aggregates;
- support arbitrary configured target intervals.

Suggested interface:

```csharp
public interface IMultiTimeframeCandleAggregator
{
    IReadOnlyList<CandleClosedEvent> Update(
        MarketCandle oneMinuteCandle);
}
```

For a 5-minute candle:

```text
00:00
00:01
00:02
00:03
00:04
```

The 5-minute candle becomes available after `00:04` closes.

Do not include `00:05` in the previous bar.

---

# 13. Simultaneous interval completion

At an hourly boundary, 5m, 15m, and 1h may complete together.

Do not make the result depend on event or dictionary order.

Use two phases:

## Phase A — update state

Update all newly completed timeframe snapshots.

## Phase B — evaluate strategies

Evaluate each strategy once with all current snapshots.

Create:

```csharp
public sealed record MarketFrame
{
    public required long Sequence { get; init; }

    public required DateTimeOffset AvailableAt { get; init; }

    public required MarketCandle ExecutionCandle { get; init; }

    public required IReadOnlySet<BarInterval> ClosedIntervals { get; init; }

    public required IReadOnlyDictionary<
        BarInterval,
        AnalysisSnapshot> Snapshots { get; init; }

    public required string InputStreamId { get; init; }
}
```

The strategy must see the updated 1h, 15m, and 5m state together.

---

# 14. Shared market analysis

If both strategies use identical annotation configuration, calculate market analysis once centrally:

```text
1m stream
    ↓
aggregator
    ↓
ChartAnnotationEngine
    ↓
immutable AnalysisSnapshots
    ↓
strategy workers
```

Do not calculate RSI, ATR, Bollinger, swings, DBSCAN, RANSAC, channels, and structure twice when both strategies use identical settings.

Support:

```csharp
public enum AnalysisSharingMode
{
    SharedImmutableSnapshots,
    IndependentPerStrategy
}
```

Use `SharedImmutableSnapshots` by default when configuration values are identical.

If strategies intentionally use different annotation settings, provide independent engines.

Never share a mutable `ChartAnnotationEngine` between concurrent workers.

---

# 15. Heavy-analysis scheduling

Do not run expensive structural algorithms on every one-minute candle.

Review current scheduling for:

- DBSCAN;
- RANSAC;
- channel detection;
- support/resistance;
- market-structure analysis.

Recommended behaviour:

```text
1m close:
    order execution
    base progress
    aggregation only

5m close:
    entry timeframe indicators
    entry timeframe strategy context

15m close:
    confirmation timeframe analysis

1h close:
    trend timeframe analysis

new confirmed swing:
    support/resistance
    trendlines
    channels
```

Retain periodic safeguards where necessary, but do not sort/copy/recluster full histories on every base candle.

Use bounded histories and incremental calculations.

Avoid repeated LINQ allocations in the hot path where practical.

Measure before and after.

---

# 16. Parallel strategy workers

Run each strategy in an independent worker.

Current required workers:

- Legacy Progressive strategy
- Improved Progressive strategy

The architecture must support additional strategies later.

```text
Historical producer
        ↓
bounded prefetch
        ↓
market clock + aggregation + annotation
        ↓
immutable MarketFrame
        ├──────────────────────┐
        ↓                      ↓
Legacy worker             Improved worker
        ↓                      ↓
own account               own account
own orders                own orders
own positions             own positions
own strategy state        own strategy state
own journal               own journal
```

Each strategy must own separate mutable state:

```csharp
public sealed class StrategySimulationSession
{
    public required string StrategyName { get; init; }

    public required IAgentStrategy Strategy { get; init; }

    public required SimulatedAccount Account { get; init; }

    public required ExecutionCoordinator Execution { get; init; }

    public required IRiskManager RiskManager { get; init; }

    public required SimulatedOrderBook OrderBook { get; init; }

    public required SimulatedPositionBook PositionBook { get; init; }

    public required TradeJournal Journal { get; init; }

    public required StrategyPerformanceTracker Performance { get; init; }
}
```

Never share:

- account state;
- position state;
- order state;
- OCO state;
- risk state;
- strategy scope;
- active setup;
- journals;
- RNG state.

Share only immutable market data and configuration.

---

# 17. Task workers versus dedicated threads

Default to worker tasks.

Support:

```csharp
public enum StrategyWorkerMode
{
    Task,
    DedicatedThread
}
```

Normal mode:

```csharp
Task RunStrategyWorkerAsync(
    ChannelReader<StrategyFrameMessage> reader,
    StrategySimulationSession session,
    CancellationToken cancellationToken);
```

Optional dedicated thread:

```csharp
Task.Factory.StartNew(
    () => RunStrategyWorkerAsync(
        reader,
        session,
        cancellationToken),
    cancellationToken,
    TaskCreationOptions.LongRunning,
    TaskScheduler.Default).Unwrap();
```

Do not assume dedicated threads are faster.

Benchmark:

- sequential;
- parallel tasks;
- dedicated threads.

Default to tasks unless measurements show a reason to change.

---

# 18. Per-sequence barrier

Parallelism must not make results nondeterministic.

For every market frame:

1. Build one immutable `MarketFrame`.
2. send the same frame to every active strategy worker;
3. process workers concurrently;
4. await every worker;
5. validate returned sequence;
6. commit replay output;
7. advance to the next frame.

Conceptually:

```csharp
StrategyFrameResult[] results =
    await Task.WhenAll(
        workers.Select(worker =>
            worker.ProcessAsync(frame, cancellationToken)));

foreach (StrategyFrameResult result in results)
{
    if (result.Sequence != frame.Sequence)
    {
        throw new InvalidOperationException(
            $"Unexpected sequence from {result.StrategyName}.");
    }
}
```

Required guarantee:

```text
All strategies finish frame N
        ↓
market clock advances to N + 1
```

Do not allow one strategy to run months ahead of another.

Use bounded strategy channels with small capacity.

Example:

```csharp
new BoundedChannelOptions(4)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = true,
    AllowSynchronousContinuations = false
};
```

Do not use unbounded channels.

---

# 19. Strategy execution modes

Support:

```csharp
public enum StrategyExecutionMode
{
    Sequential,
    ParallelWorkers
}
```

Both modes must produce identical results with the same:

- candle stream;
- configuration;
- deterministic seed.

The simulator should be able to compare correctness and performance between modes.

---

# 20. Worker processing order

Each worker must process a frame in this order:

```text
Receive frame N
    ↓
Process orders eligible for frame N
    ↓
Process stop-loss/take-profit/OCO/strategy exits
    ↓
Update position/account/equity
    ↓
Read current analysis snapshots
    ↓
Advance strategy setup state
    ↓
Generate intent/close decision
    ↓
Risk approval
    ↓
Create orders eligible from frame N + 1
    ↓
Update trade lifecycle and metrics
    ↓
Return immutable StrategyFrameResult
```

Do not let a newly created order fill using the same frame.

---

# 21. Progressive setup state machine

Preserve the progressive multi-timeframe flow:

```text
1h setup / partial
    ↓
15m confirmation
    ↓
5m entry
```

Use explicit states such as:

```csharp
public enum ProgressiveSetupStage
{
    WaitingForTrend,
    WaitingForConfirmation,
    WaitingForEntry,
    SignalReady
}
```

The setup must support:

- expiry at the next higher-timeframe close;
- replacement by a newer 1h setup;
- invalidation by opposing structure;
- invalidation by opposing break;
- safe retry if intent publishing fails;
- reset after successful publication;
- exact signal candle and price;
- deterministic setup ID.

Do not restore dictionary `.Last()` behaviour from the old Agent.

---

# 22. Legacy Progressive strategy

Implement the legacy comparison strategy as faithfully as practical:

```text
1h partial
    ↓
15m confirmation
    ↓
5m entry
```

After entry:

- use an emergency protective stop;
- do not require a fixed take-profit;
- continue until:
  - protective stop;
  - confirmed reverse strategy exit;
  - safety exit;
  - end-of-simulation liquidation.

The legacy strategy may issue an explicit close request.

Record exit reason clearly:

```text
StopLoss
ReverseStrategyClose
SafetyClose
EndOfSimulation
```

Do not force the legacy strategy to use the improved strategy’s target logic.

---

# 23. Improved Progressive strategy

The improved strategy must create a complete trade plan before order submission.

Suggested model:

```csharp
public sealed record TradePlan
{
    public required Guid SetupId { get; init; }

    public required TradeSide Side { get; init; }

    public required decimal SignalPrice { get; init; }

    public required decimal StopLoss { get; init; }

    public required decimal PrimaryTarget { get; init; }

    public decimal? SecondaryTarget { get; init; }

    public required decimal ExpectedRewardRisk { get; init; }

    public required string StopSource { get; init; }

    public required string TargetSource { get; init; }

    public required DateTimeOffset SetupStartedAt { get; init; }

    public required DateTimeOffset SignalCreatedAt { get; init; }

    public required DateTimeOffset EntryExpiresAt { get; init; }

    public required string Thesis { get; init; }

    public required string InvalidationReason { get; init; }
}
```

Stop candidates:

- recent 5m structural swing;
- recent 15m structural swing;
- active channel boundary;
- support/resistance zone;
- ATR fallback.

Target candidates:

- nearest meaningful opposing zone;
- recent confirmed swing;
- channel boundary;
- higher-timeframe structural level;
- ATR fallback.

Do not select a distant target while ignoring a strong nearby obstacle.

Reject the trade when available space produces insufficient R:R.

Use configuration for minimum R:R.

---

# 24. Position lifecycle

Keep setup logic separate from position management.

A strategy setup ends after a position is accepted/opened.

Position lifecycle may use:

```csharp
public enum SimulatedPositionStage
{
    PendingEntry,
    Open,
    RiskReduced,
    PartialProfitTaken,
    Trailing,
    Closed,
    Cancelled
}
```

The simulator and strategy may support:

- protective stop;
- primary target;
- secondary target;
- partial exit;
- break-even transition;
- structure-based trailing stop;
- time stop;
- thesis invalidation;
- reverse strategy exit;
- end-of-simulation liquidation.

Do not rely solely on a complete reverse multi-timeframe setup for profit protection.

---

# 25. Execution realism

Preserve or improve:

- next-candle entry eligibility;
- bid/ask or configured spread;
- slippage;
- commission;
- leverage;
- margin;
- OCO;
- gap-through-stop;
- gap-through-target;
- protective close;
- explicit market close;
- safety controls;
- end-of-simulation liquidation.

Use:

```csharp
public enum AmbiguousIntrabarPolicy
{
    ConservativeStopFirst,
    OptimisticTargetFirst,
    NearestToOpenFirst
}
```

Default:

```text
ConservativeStopFirst
```

Record how many trades were affected by a candle where both stop and target were touched.

Do not claim exact intrabar ordering when only OHLC data exists.

---

# 26. Warm-up

Support a warm-up period before result collection.

Example:

```text
Requested result period:
2025-01-01 → 2026-01-01

Warm-up:
45 days before 2025-01-01
```

During warm-up:

- stream candles normally;
- aggregate intervals;
- update indicators;
- detect swings;
- update structure;
- do not create setups;
- do not submit orders;
- do not include P/L in the requested-period result.

Begin trading exactly at the requested start time.

Record actual warm-up metadata.

Allow warm-up by:

- days;
- base-candle count;
- readiness condition.

---

# 27. Data-quality report

Create:

```csharp
public sealed record MarketDataQualityReport
{
    public required long CandleCount { get; init; }

    public required long DuplicateCount { get; init; }

    public required long OutOfOrderCount { get; init; }

    public required long MissingIntervalCount { get; init; }

    public required long WeekendGapCount { get; init; }

    public required long SessionGapCount { get; init; }

    public required long IncompleteAggregateCount { get; init; }

    public required DateTimeOffset FirstCandle { get; init; }

    public required DateTimeOffset LastCandle { get; init; }

    public required string InputHash { get; init; }
}
```

Unexpected open-market gaps must be reported.

Weekend/session gaps must be classified separately.

Never silently fabricate candles.

Include the same `InputHash` in every strategy result.

---

# 28. Trade journal

Record every setup and trade lifecycle.

At minimum:

```text
Simulation ID
Input stream ID
Strategy
Setup ID
Instrument
Side

Setup start
1h setup time
15m confirmation time
5m signal time

Order creation time
Order eligibility sequence
Fill time
Signal price
Fill price
Quantity

Initial stop
Initial target
Stop source
Target source
Expected R:R

Exit time
Exit price
Exit reason
Exit explanation

Gross P/L
Entry commission
Exit commission
Financing cost if modelled
Net P/L
Realised R

MFE
MAE
Holding duration
Setup duration

Ambiguous intrabar flag
Fill model
Data-quality warnings
```

Calculate MAE/MFE only from candles while the position is open.

---

# 29. Replay output

Do not generate a single enormous duplicated JSON document.

Use:

```text
simulation metadata
shared market replay chunks
strategy-specific event chunks
strategy trade journal
strategy performance summary
```

Example:

```text
simulations/{simulationId}/
    manifest.json
    market/
        chunk-000001.json.gz
        chunk-000002.json.gz
    strategies/
        legacy/
            events-000001.json.gz
            trades.json
            performance.json
        improved/
            events-000001.json.gz
            trades.json
            performance.json
```

Use one ordered replay writer.

Strategy workers must not write concurrently to the same output file.

After the barrier:

```text
MarketFrame + StrategyFrameResult[]
    ↓
single replay writer
```

Output must be atomic or clearly marked incomplete.

---

# 30. Dashboard Simulator UI

The Dashboard Simulator mode must include:

## Configuration panel

- instrument;
- start/end date;
- base interval;
- analysis intervals;
- strategies;
- warm-up;
- starting balance;
- quantity;
- leverage;
- spread;
- slippage;
- commission;
- minimum R:R;
- ambiguity policy;
- cache/refresh;
- sequential/parallel strategy mode;
- task/dedicated-thread worker mode.

## Runtime panel

- job status;
- data source status;
- cache/download status;
- progress;
- current market time;
- candles processed;
- candles/sec;
- elapsed time;
- estimated remaining work;
- pause/resume/cancel;
- backend queue status.

## Playback controls

- play/pause;
- step;
- speed;
- jump to next setup;
- jump to confirmation;
- jump to entry;
- jump to exit;
- jump to next trade;
- jump to completion.

## Chart overlays

- candles;
- swings;
- support/resistance zones;
- trendlines;
- channels;
- market structure;
- 1h setup marker;
- 15m confirmation marker;
- 5m signal marker;
- entry;
- stop;
- target;
- exit;
- entry-to-exit path;
- P/L;
- R-multiple.

## Strategy comparison

Show:

```text
Metric                 Legacy        Improved
Net P/L
Trades
Wins
Losses
Win rate
Profit factor
Average R
Median R
Maximum drawdown
Average holding time
Average setup duration
Stop exits
Target exits
Strategy exits
Ambiguous fills
```

## Trade journal

Clicking a trade should:

- select the strategy;
- load the required replay range;
- position the chart at setup start;
- optionally auto-play through exit;
- show full lifecycle metadata.

---

# 31. Progressive dashboard updates

The dashboard must not remain blank until the simulation is complete.

Immediately show:

- download/cache progress;
- warm-up progress;
- current historical time;
- latest completed setups;
- open positions;
- balance/equity;
- completed trades.

When replay chunks are completed, notify the dashboard.

The dashboard should lazy-load chunks.

Do not render all one-minute candles for an entire year simultaneously.

Use:

- hourly overview for full range;
- 5m chart for strategy lifecycle;
- detailed 1m window around entry/exit;
- virtualised trade journal;
- range-based replay API.

---

# 32. Concurrency beyond strategy workers

Use concurrency only where independence is real.

Good concurrency:

- downloading/reading page `N + 1` while processing page `N`;
- legacy and improved strategy workers;
- replay compression/writing;
- independent instrument simulations;
- independent parameter scenarios;
- selected independent heavy analyses using immutable input.

Do not parallelise sequential dependencies such as:

- candles within one strategy timeline;
- one account’s mutation;
- one order book;
- one position book;
- one indicator state;
- swing confirmation;
- shared mutable replay writer.

Do not use “as much concurrency as possible” blindly.

Correctness and determinism take priority.

---

# 33. Failure handling

Create:

```csharp
public enum StrategyFailurePolicy
{
    StopEntireComparison,
    StopFailedStrategyOnly
}
```

Default:

```text
StopEntireComparison
```

A failure record must contain:

- simulation ID;
- strategy;
- sequence;
- market time;
- input stream ID;
- current setup;
- open position;
- pending orders;
- last completed trade;
- exception.

If `StopFailedStrategyOnly` is explicitly selected:

- mark result incomplete;
- record last sequence;
- exclude from full-period comparison;
- continue other workers;
- show incomplete state clearly.

Never silently swallow strategy exceptions.

---

# 34. Pause, resume, and cancellation

Support safe pause/resume/cancel.

Pause must stop market-clock advancement at a sequence boundary.

Resume continues from the same sequence.

Cancel must:

- signal cancellation;
- stop source/prefetch;
- stop aggregation;
- stop workers;
- complete channels;
- close replay output safely;
- mark job cancelled;
- not leave background tasks running.

Visual playback pause remains separate.

---

# 35. Progress model

Create:

```csharp
public sealed record BacktestProgress
{
    public required Guid SimulationId { get; init; }

    public required SimulationJobStatus Status { get; init; }

    public required DateTimeOffset CurrentMarketTime { get; init; }

    public required DateTimeOffset EvaluationStart { get; init; }

    public required DateTimeOffset EvaluationEnd { get; init; }

    public required long ProcessedBaseCandles { get; init; }

    public long? EstimatedTotalBaseCandles { get; init; }

    public required decimal ProgressPercent { get; init; }

    public required decimal CandlesPerSecond { get; init; }

    public required IReadOnlyList<StrategyProgressSnapshot> Strategies { get; init; }
}
```

Throttle progress notifications.

Do not publish one large event for every candle.

---

# 36. Worker metrics

Record:

```csharp
public sealed record StrategyWorkerMetrics
{
    public required string StrategyName { get; init; }

    public required long ProcessedFrames { get; init; }

    public required TimeSpan TotalProcessingTime { get; init; }

    public required TimeSpan MaximumFrameProcessingTime { get; init; }

    public required TimeSpan AverageFrameProcessingTime { get; init; }

    public required TimeSpan BarrierWaitTime { get; init; }

    public required int PeakChannelOccupancy { get; init; }
}
```

At completion report:

- slowest strategy;
- average frame time;
- max frame time;
- barrier wait;
- channel occupancy;
- total duration;
- frames/sec;
- peak memory if available.

---

# 37. Configuration

Support validated configuration similar to:

```json
{
  "Backtest": {
    "BaseInterval": "1m",
    "AnalysisIntervals": ["5m", "15m", "1h"],

    "SourcePageSize": 5000,
    "PrefetchCapacity": 20000,
    "PrefetchLowWatermark": 5000,

    "WarmupDays": 45,

    "StrategyExecutionMode": "ParallelWorkers",
    "StrategyWorkerMode": "Task",
    "StrategyChannelCapacity": 4,
    "MaximumParallelStrategies": 4,
    "StrategyFailurePolicy": "StopEntireComparison",

    "AnalysisSharingMode": "SharedImmutableSnapshots",

    "AmbiguousIntrabarPolicy": "ConservativeStopFirst",

    "UseHistoricalBidAsk": true,
    "RefreshCache": false,
    "DeterministicSeed": 12345,

    "ReplayChunkSize": 5000,
    "ProgressPublishIntervalMilliseconds": 500
  }
}
```

Validate:

```text
SourcePageSize >= 1
PrefetchCapacity > SourcePageSize
PrefetchLowWatermark >= 1
PrefetchLowWatermark < PrefetchCapacity
StrategyChannelCapacity >= 1
MaximumParallelStrategies >= 1
selected strategy count <= MaximumParallelStrategies
ReplayChunkSize >= 1
```

Fail early with clear errors.

---

# 38. CLI

Keep a CLI for:

- CI;
- regression testing;
- benchmarking;
- overnight runs;
- automation;
- reproducing saved configurations.

Example:

```bash
dotnet run -c Release \
  --project BacktestRunner/BacktestRunner.csproj -- \
  --instrument FX:GBP/JPY \
  --from 2025-01-01 \
  --to 2026-01-01 \
  --base-interval 1m \
  --analysis-intervals 5m,15m,1h \
  --warmup-days 45 \
  --strategies legacy,improved \
  --strategy-execution parallel \
  --strategy-worker task \
  --strategy-channel-capacity 4 \
  --max-parallel-strategies 4 \
  --strategy-failure-policy stop-all \
  --analysis-sharing shared \
  --quantity 1000 \
  --starting-balance 100000 \
  --minimum-rr 1.5 \
  --seed 12345 \
  --output Dashboard/public/data/backtests
```

Support:

```text
--refresh
--no-cache
--strategy
--strategies
--strategy-execution
--strategy-worker
--strategy-channel-capacity
--max-parallel-strategies
--strategy-failure-policy
--analysis-sharing
--ambiguous-policy
--progress-interval
--seed
```

---

# 39. Tests

Add comprehensive tests.

## Historical source and prefetch

Verify:

- page boundary has no duplicate;
- page boundary has no missing candle;
- prefetch starts below low watermark;
- unread candles are never overwritten;
- slow producer waits safely;
- slow consumer creates backpressure;
- cancellation ends all tasks;
- end-of-stream drains remaining candles;
- cache replay matches downloaded stream hash.

## Aggregation

Verify exact OHLCV for:

- 5m;
- 15m;
- 1h;
- UTC alignment;
- weekend gaps;
- session gaps;
- missing one-minute member;
- simultaneous interval completion.

## No lookahead

Prove:

- signal candle cannot fill its own order;
- new orders become eligible next base candle;
- stop/target candidates use only available data;
- swing confirmation uses right-hand candles;
- simultaneous close uses updated snapshots only.

## Progressive strategy

Verify:

- 1h partial;
- 15m confirmation;
- 5m entry;
- expiry;
- invalidation;
- replacement;
- failed intent retry;
- reset after publication;
- exact signal timestamp.

## Legacy strategy

Verify:

- entry;
- emergency stop;
- reverse setup close;
- end-of-simulation close;
- no forced target.

## Improved strategy

Verify:

- structural stop;
- structural target;
- obstacle rejection;
- minimum R:R;
- bracket/OCO;
- structural invalidation.

## Execution

Verify:

- spread;
- bid/ask;
- slippage;
- commission;
- margin;
- stop gap;
- target gap;
- OCO cancellation;
- ambiguous candle policies;
- explicit close;
- safety controls do not block reducing exits;
- final liquidation.

## Parallel workers

Verify:

- all strategies receive same frame sequence;
- same timestamps;
- same input stream ID;
- same frame count;
- per-frame barrier;
- no state sharing;
- no order/setup ID collisions;
- slow worker causes backpressure;
- no dropped frames;
- deterministic sequential/parallel results;
- deterministic task/dedicated-thread results where supported;
- failure policies;
- cancellation.

## Dashboard API

Verify:

- create job;
- query job;
- progress;
- pause;
- resume;
- cancel;
- completed result;
- replay-range response;
- refresh-safe persistence;
- error handling.

## Integration

Use a fake paginated source for full multi-month tests without internet.

Keep OANDA tests explicit and environment-controlled.

---

# 40. Performance benchmarks

Benchmark:

1. current implementation;
2. streamed sequential implementation;
3. parallel strategy tasks;
4. dedicated threads;
5. shared analysis;
6. independent analysis.

Measure:

```text
total runtime
candles/sec
download time
cache time
aggregation time
annotation time
strategy time
execution time
replay-write time
peak memory
GC allocations
worker barrier wait
channel occupancy
```

A one-year cached run should not take multiple hours on a modern desktop unless there is a measured reason.

Do not claim a speedup without measurements.

---

# 41. Memory acceptance criteria

The simulator must not retain:

- all one-minute candles;
- all full annotation snapshots;
- duplicate market replay for each strategy;
- an entire year of chart-ready objects.

Memory should remain bounded by configuration:

```text
source page size
prefetch capacity
analysis ring-buffer capacity
strategy channel capacity
replay chunk size
trade count
```

Add diagnostics proving bounded behaviour.

---

# 42. Security

Search the repository for credentials.

Remove real secrets from:

- source;
- `.env.integration`;
- ZIPs;
- logs;
- test snapshots;
- replay metadata.

Add:

```gitignore
.env
.env.*
!.env.example
```

Keep only placeholders.

Do not print access tokens.

---

# 43. Documentation

Update README and add architecture documentation covering:

- dashboard-first workflow;
- application service;
- job queue;
- OANDA source;
- cache;
- ring-buffer ownership;
- prefetch;
- one-minute clock;
- aggregation;
- event ordering;
- no-lookahead;
- shared analysis;
- parallel workers;
- per-frame barrier;
- strategy isolation;
- position lifecycle;
- replay chunks;
- dashboard playback;
- determinism;
- failure handling;
- limitations.

Include exact commands.

---

# 44. Implementation order

Work in this order:

1. Inspect and document current architecture.
2. Run current build/tests and record failures.
3. Identify reusable code.
4. Refactor runner into `IBacktestApplicationService`.
5. Add persistent job repository.
6. Add bounded job queue.
7. Implement streamed OANDA/cache source.
8. Implement bounded prefetch.
9. Use one-minute canonical clock.
10. Correct aggregation.
11. Correct simulation event ordering.
12. Add warm-up.
13. Create immutable `MarketFrame`.
14. Share annotation snapshots where valid.
15. Add independent strategy sessions.
16. Add sequential execution mode.
17. Add parallel worker mode.
18. Add per-frame barrier.
19. Complete legacy strategy lifecycle.
20. Complete improved strategy lifecycle.
21. Complete execution realism.
22. Add trade lifecycle and quality records.
23. Add chunked replay writer.
24. Add API endpoints.
25. Add SignalR/SSE.
26. Update Dashboard Simulator mode.
27. Add progressive playback.
28. Add tests.
29. Add benchmarks.
30. Run all validation commands.
31. Document bugs found and limitations.

Do not leave placeholders in the main execution path.

---

# 45. Deliverables

Return:

1. modified source files;
2. new source files;
3. migrations/configuration where needed;
4. unit tests;
5. integration tests;
6. benchmark results;
7. updated dashboard;
8. updated README;
9. architecture documentation;
10. list of bugs found;
11. list of performance bottlenecks found;
12. exact build/test commands;
13. remaining limitations;
14. security findings;
15. instructions for running from Dashboard;
16. instructions for running from CLI.

---

# 46. Required validation

Run:

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
```

Then:

```bash
cd Dashboard
npm ci
npm run build
```

Run relevant formatter/linter commands.

Run at least:

- short fake-source integration simulation;
- one-month cached simulation;
- sequential-versus-parallel determinism comparison.

Do not claim a command passed unless it actually passed.

If a command cannot be run, state exactly why.

---

# 47. Final acceptance scenario

The complete system must support:

1. Open Dashboard Simulator mode.
2. Select GBP/JPY.
3. Select one-year evaluation range.
4. Select one-minute base data.
5. Select 5m, 15m, and 1h analysis.
6. Select Legacy and Improved strategies.
7. Start simulation.
8. Receive simulation ID immediately.
9. See OANDA/cache progress.
10. See warm-up progress.
11. See current historical market time.
12. See candles processed and candles/sec.
13. See both strategies run independently.
14. See progressive setup stages appear.
15. See orders, entries, stops, targets, exits.
16. Pause/resume backend simulation.
17. Pause/step/change visual playback independently.
18. Inspect trades before the simulation finishes.
19. Complete the simulation.
20. Compare strategy metrics.
21. Click a trade and replay setup-to-exit.
22. Refresh the browser without losing the job.
23. Rerun from cache.
24. Obtain identical deterministic results.
25. Run the same request sequentially.
26. Verify sequential and parallel strategy outputs are identical.

---

# 48. Critical correctness principles

These are non-negotiable:

- No lookahead.
- One chronological base-candle clock.
- Orders created from frame `N` are eligible from frame `N + 1`.
- No unread ring-buffer overwrite.
- No independent candle stream per strategy.
- Same immutable frame for all strategies.
- Independent account/order/position/risk/journal state.
- Deterministic per-frame barrier.
- No hidden exception swallowing.
- No fabricated missing candles.
- No duplicated full-year replay per strategy.
- No real credentials in repository/output.
- Dashboard and CLI must call the same application service.
- Dashboard should show progress and partial results before completion.
- Visual playback speed must be independent from compute speed.
- Concurrency must improve independent work only; sequential state must remain sequential.
- Correctness and reproducibility take priority over raw speed.
