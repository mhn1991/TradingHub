# TradingHub Simulator Phase 3 — Precision Execution, Custom Timeframes, Live Replay, and Final Completion

## Role

Act as a senior .NET architect, quantitative backtesting engineer, market-data engineer, concurrency specialist, performance engineer, test engineer, and Vue/ASP.NET Core full-stack developer.

Work directly against the existing TradingHub solution supplied with this prompt.

Do **not** create:

- a second simulator;
- a parallel backtest implementation;
- a disconnected CLI workflow;
- duplicate Agent, RiskManager, ExecutionManager, Broker, or ChartAnnotator projects;
- a fake 1-second data source created by splitting larger candles.

This is the next corrective and completion pass over the current implementation.

The current solution already contains meaningful fixes from the previous passes. Preserve them and add regression coverage so they cannot be lost.

---

# 1. Current implementation status

The current repository already includes:

- dashboard-first simulation jobs;
- shared `IBacktestApplicationService`;
- bounded background job queue;
- file-backed job persistence;
- one chronological execution stream;
- `StreamingComparativeEngine`;
- `StrategySimulationSession`;
- persistent strategy worker hosts;
- sequential and parallel strategy execution;
- a deterministic frame barrier;
- async pause gate;
- streamed OANDA download while simulation progresses;
- incremental temporary cache writing;
- shared or independent annotation configuration;
- SignalR server support and Vue client;
- progressive market replay chunks;
- working visual playback controls;
- Legacy and Improved progressive agents;
- corrected `NearestToOpenFirst`;
- corrected Legacy exit-mode interface dispatch;
- Vite `/hubs` WebSocket proxy;
- replay de-duplication by sequence;
- job-specific replay reset;
- bounded replay range API.

Do not rewrite these unnecessarily.

---

# 2. Fixes that must not regress

## 2.1 Legacy strategy exit mode

The Legacy strategy now correctly exposes:

```csharp
AgentExitManagementMode.ProtectiveStopAndStrategyExit
```

through `ITradingAgent`.

It must continue to allow:

```csharp
StopLossPrice = protectiveStop;
TakeProfitPrice = null;
ExpectedRewardRisk = null;
```

without rejecting the trade for missing R:R.

The Legacy strategy must retain:

- protective emergency stop;
- reverse-strategy close;
- safety close;
- end-of-simulation liquidation;
- no mandatory fixed target.

The Improved strategy must retain bracket behaviour and minimum R:R validation.

Keep and strengthen the interface-dispatch and end-to-end trade tests.

---

## 2.2 `NearestToOpenFirst`

The corrected ordering must remain:

```text
distance from candle open
    ↓
stop-first only as exact tie-breaker
```

Do not reintroduce stop/target policy priority before distance.

---

## 2.3 Strategy failure isolation

Keep:

```text
StopEntireComparison
StopFailedStrategyOnly
```

The second policy must continue remaining strategies while clearly marking the failed strategy incomplete.

---

## 2.4 SignalR development proxy

Keep `/hubs` proxied with:

```ts
ws: true
```

Polling remains fallback only.

---

# 3. Mandatory initial audit

Before changing code:

1. Read:
   - `ARCHITECTURE_SIMULATOR.md`
   - `RUN_BACKTEST.md`
   - `VALIDATION.md`
   - `SIMULATOR_PHASE2_VALIDATION.md`
   - `SIMULATOR_FINAL_VALIDATION.md`
   - all previous Codex prompts included in the repository.
2. Inspect:
   - `StreamingComparativeEngine`;
   - `StrategySimulationSession`;
   - `StrategyWorkerHost`;
   - `BacktestApplicationService`;
   - `OandaStreamingCandleSource`;
   - `PrefetchingCandleStream`;
   - `LowWatermarkPrefetchStream`;
   - cache writer and cache reader;
   - replay writer;
   - DashboardLive API and SignalR bridge;
   - Vue Simulator panel and composables;
   - strategy factories and timeframe options;
   - all simulator tests.
3. Run the complete current build and tests before edits.
4. Record a baseline:
   - test totals;
   - frontend build;
   - one-month cached runtime;
   - candles/sec;
   - peak memory;
   - replay size;
   - time to first replay;
   - time to first trade;
   - sequential versus parallel results.
5. Add all before/after evidence to:
   - `SIMULATOR_PHASE3_VALIDATION.md`.

Do not return only an architecture summary.

---

# 4. Major new capability: separate execution and analysis clocks

The current `BaseInterval` concept must be clarified and separated.

Introduce explicit configuration:

```csharp
public sealed record SimulationTimeframeOptions
{
    public required BarInterval ExecutionInterval { get; init; }

    public required BarInterval AnalysisBaseInterval { get; init; }

    public required IReadOnlyList<BarInterval> AnalysisIntervals { get; init; }
}
```

Definitions:

## Execution interval

The finest historical candle or quote interval used for:

- order eligibility;
- market/limit/stop fills;
- stop-loss;
- take-profit;
- OCO resolution;
- trailing-stop execution;
- open-position MFE/MAE;
- gap handling;
- account equity updates.

Examples:

```text
1m
5s
1s
```

## Analysis base interval

The smallest completed candle delivered to:

- indicators;
- SwingDetector;
- MarketStructureAnalyzer;
- SupportResistanceDetector;
- RANSAC;
- channel detection;
- strategy analysis.

Default:

```text
1m
```

Sub-minute execution data must not cause ChartAnnotator to run every second.

## Analysis intervals

User-configurable analysis timeframes aggregated from the analysis base:

```text
1m
3m
5m
15m
30m
1h
2h
```

---

# 5. Precision modes

Expose clear presets.

```csharp
public enum SimulationPrecisionMode
{
    Fast,
    BrokerNativePrecision,
    HighPrecision
}
```

## Fast

```text
Execution interval:      1m
Analysis base interval:  1m
```

Use for:

- rapid development;
- year-long strategy comparisons;
- parameter experiments;
- CI tests.

## Broker-native precision

For OANDA historical candles:

```text
Execution interval:      5s
Analysis base interval:  1m
```

Use for:

- better stop/target ordering;
- better limit/stop entry timing;
- better MFE/MAE;
- final strategy validation.

## High precision

```text
Execution interval:      1s
Analysis base interval:  1m
```

This mode is allowed only when the selected historical source genuinely provides 1-second candles or finer quote/tick data.

Do not claim that every broker supports this mode.

---

# 6. Critical source capability rule

The current OANDA v20 candle mapping supports:

```text
5s
10s
15s
30s
1m and larger supported granularities
```

It does not provide a native 1-second historical candle granularity through the current candle endpoint.

Therefore:

- OANDA historical-candle precision mode should use `5s`;
- selecting `1s` with the normal OANDA candle source must produce a clear capability error;
- the UI should suggest:
  - `5s OANDA precision`, or
  - a recorded/imported 1-second or tick source;
- never synthesize sixty 1-second candles from one 1-minute candle;
- never split one 5-second OHLC candle into five invented 1-second candles.

Add a capability contract:

```csharp
public interface IHistoricalMarketDataCapabilities
{
    string SourceName { get; }

    IReadOnlySet<BarInterval> SupportedExecutionIntervals { get; }

    bool SupportsHistoricalBidAsk { get; }

    bool SupportsTicks { get; }

    bool SupportsInterval(BarInterval interval);
}
```

Validate the request before queuing a long-running job.

Return a structured error such as:

```text
HistoricalGranularityNotSupported
```

with:

- requested interval;
- source;
- supported intervals;
- suggested alternative.

---

# 7. Genuine 1-second FX source

To support true 1-second FX backtesting, add a source that consumes real recorded data.

Acceptable designs:

## Recorded quote source

```csharp
public interface IRecordedQuoteHistoricalSource
{
    IAsyncEnumerable<HistoricalQuote> StreamQuotesAsync(
        HistoricalQuoteRequest request,
        CancellationToken cancellationToken = default);
}
```

```csharp
public sealed record HistoricalQuote
{
    public required DateTimeOffset Timestamp { get; init; }

    public required decimal Bid { get; init; }

    public required decimal Ask { get; init; }

    public decimal Mid => (Bid + Ask) / 2m;
}
```

Aggregate real quotes into completed 1-second bid/ask/mid candles.

## Imported 1-second candle source

Support a documented file format such as:

```text
timestamp
bidOpen
bidHigh
bidLow
bidClose
askOpen
askHigh
askLow
askClose
volume/tickCount
```

Use:

- CSV;
- JSONL;
- Parquet, if an appropriate library already exists or is justified.

Do not add a large dependency without explaining it.

## Recorded live OANDA pricing data

Allow future live pricing data to be recorded and later replayed.

The recorder must:

- use UTC timestamps;
- persist bid and ask;
- preserve event ordering;
- rotate/chunk files;
- flush safely;
- recover incomplete files;
- avoid secrets in recorded metadata.

Do not confuse live polling once per second with guaranteed historical 1-second market truth.

---

# 8. Two-stage aggregation

Implement:

```text
execution candles
    ↓
analysis-base aggregator
    ↓ completed 1m candle
    ↓
multi-timeframe analysis aggregator
    ↓
1m / 3m / 5m / 15m / 30m / 1h / 2h
```

For example:

```text
1-second candles
    ↓
completed 1-minute candle
    ↓
3m, 5m, 15m, 30m, 1h, 2h
```

The ChartAnnotator must receive only completed analysis candles.

Do not feed it sub-minute execution candles unless a strategy explicitly declares a sub-minute analysis interval and the design supports it.

Default minimum analysis interval remains 1 minute.

---

# 9. Required execution ordering for sub-minute mode

For every execution candle:

```text
receive completed execution candle
    ↓
process orders already eligible
    ↓
process fills, stops, targets, OCO
    ↓
update position/account/equity
    ↓
update MFE/MAE
    ↓
aggregate toward the current 1m analysis candle
```

When the 1-minute candle closes:

```text
complete 1m candle
    ↓
complete any higher intervals ending at the same time
    ↓
update all snapshots in phase A
    ↓
evaluate strategy once in phase B
    ↓
create new orders
    ↓
new orders become eligible from the next execution candle
```

Example:

```text
last execution candle: 10:00:59–10:01:00
1m candle completes at: 10:01:00
strategy evaluates at:  10:01:00
new order eligible from the first execution candle after 10:01:00
```

A strategy-created order must not fill from any price movement already contained in the completed minute that generated it.

---

# 10. Avoid a per-second task barrier overhead explosion

A one-year 1-second run can contain tens of millions of execution frames.

Do not blindly allocate:

- one `TaskCompletionSource`;
- one frame task;
- one replay object graph;

per second per strategy if a more efficient deterministic design is available.

Introduce execution batches aligned to the analysis base.

Example:

```csharp
public sealed record ExecutionBatch
{
    public required long FirstExecutionSequence { get; init; }

    public required long LastExecutionSequence { get; init; }

    public required IReadOnlyList<MarketCandle> ExecutionCandles { get; init; }

    public CandleClosedEvent? AnalysisBaseClosed { get; init; }

    public required IReadOnlyList<CandleClosedEvent>
        HigherIntervalsClosed { get; init; }

    public required AnalysisSnapshotSet SnapshotSet { get; init; }

    public required bool IsWarmup { get; init; }

    public required bool IsLastBatch { get; init; }
}
```

For a normal complete minute:

```text
1s mode → up to 60 execution candles
5s mode → up to 12 execution candles
1m mode → 1 execution candle
```

Each strategy worker should:

1. process every execution candle sequentially;
2. preserve exact order and timestamps;
3. capture zero or more lifecycle/trade events;
4. evaluate the strategy only after the analysis snapshot update;
5. return one batch result.

Use a barrier per analysis-base batch rather than necessarily per second.

This must not alter results.

Retain an optional strict per-execution-frame mode for validation if useful.

Add a benchmark proving whether batching improves throughput and allocations.

---

# 11. Batch correctness requirements

A strategy worker may close and potentially open positions inside one batch, so the result model must support collections:

```csharp
public sealed record StrategyBatchResult
{
    public required string StrategyId { get; init; }

    public required long FirstExecutionSequence { get; init; }

    public required long LastExecutionSequence { get; init; }

    public required IReadOnlyList<StrategyReplayEvent>
        Events { get; init; }

    public required IReadOnlyList<SimulatedTradeRecord>
        CompletedTrades { get; init; }

    public required StrategyProgressSnapshot Progress { get; init; }
}
```

Do not retain only:

```csharp
SimulatedTradeRecord? NewlyCompletedTrade
```

because batching may produce multiple relevant lifecycle events.

The single-position restriction may currently limit actual trade count per batch, but the API should not encode that assumption unnecessarily.

---

# 12. Custom strategy intervals

The current strategy factory still hardcodes:

```text
Trend:         1h
Confirmation: 15m
Entry:         5m
```

Make these configurable.

Add to `BacktestRequest` or a dedicated strategy configuration:

```csharp
public sealed record ProgressiveStrategyTimeframes
{
    public required BarInterval TrendInterval { get; init; }

    public required BarInterval ConfirmationInterval { get; init; }

    public required BarInterval EntryInterval { get; init; }
}
```

Defaults:

```text
Trend:         1h
Confirmation: 15m
Entry:         5m
```

Allow configurations such as:

```text
Trend:         2h
Confirmation: 30m
Entry:         5m
```

or:

```text
Trend:         2h
Confirmation: 30m
Entry:         3m
```

---

# 13. Separate strategy timeframes from analysis timeframes

The user may request:

```text
Analysis:
1m,3m,5m,15m,30m,2h

Strategy:
Trend 2h
Confirmation 30m
Entry 5m
```

Automatically construct:

```csharp
effectiveAnalysisIntervals =
    requestedAnalysisIntervals
    ∪ strategy.RequiredIntervals
    ∪ AnalysisBaseInterval;
```

Never let a strategy wait forever because its required interval was omitted from the analysis list.

Display automatically added intervals in the UI before starting.

---

# 14. Timeframe validation

Validate:

```text
ExecutionInterval <= AnalysisBaseInterval
EntryInterval < ConfirmationInterval
ConfirmationInterval < TrendInterval
```

For fixed-duration intervals, verify clean alignment/divisibility.

Examples with 1-second execution and 1-minute analysis base:

```text
1m   valid
3m   valid
5m   valid
15m  valid
30m  valid
2h   valid
```

Examples with 5-second execution:

```text
1m valid because 60 seconds is divisible by 5
3m valid
```

Reject incompatible combinations rather than producing malformed candles.

Be careful with:

- day/week/month intervals;
- daylight-saving effects;
- UTC alignment;
- session gaps.

Use UTC for all aggregation boundaries.

---

# 15. Dashboard timeframe controls

Replace ambiguous single “Base interval” wording with:

```text
Precision mode
Historical source
Execution interval
Analysis base interval
Analysis intervals
Trend interval
Confirmation interval
Entry interval
```

Suggested UI:

```text
Precision:
○ Fast — 1m execution
○ OANDA precision — 5s execution
○ High precision — 1s recorded data

Analysis base:
1m

Additional analysis:
3m,5m,15m,30m,1h,2h

Strategy:
Trend 2h
Confirmation 30m
Entry 5m
```

Show:

- supported source intervals;
- estimated execution-frame count;
- estimated cache size;
- a performance warning for sub-minute modes;
- whether bid/ask is available;
- whether the source is real or synthetic.

Disable unsupported choices rather than failing after a long wait.

---

# 16. API and CLI interval parsing

Support:

```text
1s
5s
10s
15s
30s
1m
3m
5m
15m
30m
1h
2h
1d
```

The Dashboard API currently does not parse seconds.

Add one shared interval parser rather than duplicating different parsers in:

- DashboardLive;
- BacktestRunner;
- workspace code;
- cache code.

Create an abstraction such as:

```csharp
public static class BarIntervalParser
{
    public static BarInterval Parse(string value);

    public static bool TryParse(
        string value,
        out BarInterval interval);

    public static string Format(BarInterval interval);
}
```

Use it everywhere.

---

# 17. Data-source selection

Add explicit source configuration:

```csharp
public enum HistoricalDataSourceKind
{
    OandaCandles,
    RecordedQuotes,
    ImportedSecondCandles,
    BinanceCandles,
    InlineTestData
}
```

The selected source must drive capability validation.

Examples:

```text
OANDA + 1s → reject with suggestion to use 5s
OANDA + 5s → allowed
RecordedQuotes + 1s → allowed
Binance + supported 1s candle → allowed
Inline fake 1s source → allowed for tests
```

Do not silently switch source or interval.

---

# 18. Replay design for sub-minute execution

Do not store a full year of 1-second candles in the browser-facing market replay by default.

Use separate replay layers:

## Analysis/chart replay

Default:

```text
1m candles and above
```

Used for:

- main chart;
- indicators;
- structure;
- setups;
- trendlines;
- channels.

## Execution-detail replay

Persist sub-minute detail only:

- around pending entries;
- while a position is open;
- around stop/target ambiguity;
- within a configurable window around trade lifecycle events.

Example:

```text
30 minutes before signal
through
30 minutes after exit
```

Make the window configurable.

## Optional full execution recording

Allow explicitly enabled full 1s/5s output for research, with:

- clear storage warning;
- chunked compressed output;
- strict size limits;
- lazy range loading.

Do not make this the default.

---

# 19. Real chart integration

The current Simulator panel still shows textual OHLC playback rather than the full `AnalysisChart`.

Integrate the real chart.

Display:

- 1m or selected analysis candles;
- 1h/2h setup marker;
- 15m/30m confirmation marker;
- 3m/5m signal marker;
- order submission;
- entry;
- stop;
- target;
- stop updates;
- strategy close;
- exit;
- P/L;
- realised R;
- trade path;
- swings;
- support/resistance;
- trendlines;
- channels;
- market structure.

When execution detail is loaded, allow zooming into 1s/5s around a selected trade.

Do not render millions of points simultaneously.

---

# 20. Incremental live trades

The current replay writer still writes the consolidated:

```text
trades.json.gz
```

mainly at final completion.

Make trades genuinely available during a running simulation.

When a trade completes:

1. append it to an incremental trade chunk;
2. atomically update the trade index;
3. publish `TradeCompleted` through SignalR;
4. allow cursor-based API retrieval;
5. update the Dashboard immediately.

Add:

```text
GET /api/simulations/{id}/trades?strategy=legacy&cursor=...&limit=...
```

Return:

```json
{
  "items": [],
  "nextCursor": "...",
  "hasMore": true
}
```

Do not reread and resend all trades on every poll.

---

# 21. Event-driven lifecycle replay

Replace per-execution-frame duplicated strategy state with event-driven records.

Use:

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

Write balance/equity samples separately and downsample them.

For sub-minute mode, this is mandatory to keep replay size bounded.

---

# 22. Annotation deltas

Do not serialise the complete annotation snapshot for every execution candle or every minute.

Write deltas only when analysis changes:

- new/removed swing;
- zone added/updated/removed;
- trendline added/updated/removed;
- channel added/updated/removed;
- structure changed;
- volatility regime changed;
- divergence/convergence signal changed.

Use stable IDs or deterministic geometric fingerprints.

---

# 23. Production prefetch still does not use the low watermark

The current `CreatePrefetchStream` sends OANDA’s progress-aware stream through `PrefetchingCandleStream`.

`PrefetchingCandleStream` currently calculates:

```csharp
long unread = writeSequence - readSequence;
```

and then discards it.

Its configured low watermark has no effect.

Correct this without bypassing OANDA caching/progress again.

Preferred design:

```text
cache/source coordinator owns page acquisition
    ↓
low-watermark controls next page request
    ↓
bounded output channel
    ↓
simulator
```

Do not fix low-watermark paging by reverting to bare OANDA `ReadPageAsync` and losing cache behaviour.

Unify:

- page acquisition;
- cache read/write;
- progress;
- low-watermark;
- cancellation.

Add a production-path integration test.

---

# 24. OANDA cache writer lifecycle

The current async iterator does not comprehensively wrap download and cache writer ownership in `try/finally`.

Ensure:

```csharp
await using cacheWriter
try
{
    stream/download/yield
    commit
}
catch
{
    abort
    throw
}
finally
{
    dispose
}
```

Handle:

- HTTP failure;
- parser failure;
- cancellation;
- consumer stopping enumeration early;
- disk failure;
- cache commit failure.

No temporary cache handle should remain open after an aborted job.

---

# 25. Full cache integrity validation

The current validation reads only the beginning of the gzip stream.

Validate the complete cache:

- manifest exists;
- manifest complete;
- schema;
- source version;
- broker;
- environment;
- instrument;
- execution interval;
- range;
- price components;
- row count;
- first timestamp;
- last timestamp;
- full content hash;
- gzip reaches valid end;
- no out-of-order rows;
- no duplicate rows.

Use streaming validation so memory remains bounded.

If full hash validation on every cached run is too expensive, support:

```text
Quick validation
Full validation
```

but define the safety trade-off clearly.

---

# 26. Progress state remains partially fire-and-forget

The current service still contains non-awaited persistence calls from progress callbacks.

The snapshot store also starts a task for every publication attempt.

Replace this with one owned, serialised per-job reducer/persistence loop.

Required flow:

```text
source progress
engine progress
status transitions
control actions
terminal result
    ↓
single ordered job-update channel
    ↓
monotonic revision
    ↓
in-memory snapshot
    ↓
SignalR
    ↓
coalesced disk persistence
```

Requirements:

- no overlapping snapshot mutations;
- no duplicate revisions;
- no stale revision overwrites;
- no unobserved exceptions;
- one persistence worker per job or one keyed global actor;
- terminal update flushed and awaited.

Also observe SignalR publisher failures instead of discarding them blindly.

---

# 27. Dedicated-thread honesty

The current `LongRunning` async worker is not guaranteed to remain on one OS thread after `await`.

Choose:

## True dedicated thread

Use a synchronous dedicated worker loop with stable thread identity.

## Remove/rename the option

Keep only task workers if true thread affinity provides no measured benefit.

Do not expose `DedicatedThread` unless tests prove the same thread processes the entire run.

---

# 28. Input and fill-model metadata

Separate:

```text
SimulationId
InputRequestId
SimulationConfigurationId
InputHash
```

Include:

- source kind;
- actual execution interval;
- analysis base;
- actual warm-up start;
- price components;
- schema/source version.

Correct fill-model labels.

Until historical bid/ask drives fills, report clearly:

```text
MidpointPlusConfiguredSpread
```

Do not label midpoint-plus-spread results ambiguously as a different synthetic model unless those models are truly distinct and documented.

---

# 29. Historical bid/ask

For OANDA intervals where bid/ask candles are supported, implement all price components if practical:

```text
mid
bid
ask
```

Cache them independently from midpoint-only data.

Execution rules:

```text
buy entry → ask
sell entry → bid
long exit → bid
short exit → ask
```

For 1-second recorded quotes, bid/ask must be preserved.

Until completed:

- disable the option;
- do not claim historical bid/ask;
- display the actual fill model.

---

# 30. MFE and MAE

Track on every execution candle while a position is open.

For each trade record:

```text
maximum favourable excursion price
maximum favourable excursion amount
maximum favourable excursion R
maximum adverse excursion price
maximum adverse excursion amount
maximum adverse excursion R
timestamp of MFE
timestamp of MAE
```

Sub-minute execution mode should materially improve these metrics.

Use executable bid/ask side where available.

---

# 31. Rich performance comparison

Compute and display:

- gross P/L;
- net P/L;
- commissions;
- wins;
- losses;
- win rate;
- profit factor;
- average R;
- median R;
- expectancy;
- maximum drawdown;
- average holding time;
- average setup duration;
- stop exits;
- target exits;
- strategy exits;
- safety exits;
- end-of-simulation exits;
- ambiguous fills;
- average MFE;
- average MAE;
- MFE captured percentage.

Do not compare a failed/incomplete strategy as though it covered the full period.

---

# 32. Stage-level instrumentation

Measure:

- source HTTP;
- cache read;
- cache write;
- prefetch wait;
- execution-candle processing;
- 1m aggregation;
- higher-timeframe aggregation;
- indicators;
- swing detection;
- structure;
- support/resistance;
- RANSAC;
- channels;
- confidence;
- strategy evaluation;
- risk validation;
- simulated broker execution;
- batch/barrier wait;
- replay serialisation;
- compression;
- job persistence;
- SignalR.

For precision modes, separately report:

```text
execution frames/sec
analysis-base candles/sec
strategy evaluations/sec
```

Do not use only “candles/sec” when execution and analysis intervals differ.

---

# 33. Required precision tests

## Exact 1m aggregation from 1s

Generate 60 known one-second candles.

Verify:

```text
1m open = first open
1m high = maximum high
1m low = minimum low
1m close = final close
volume = sum
```

## Exact 1m aggregation from 5s

Generate 12 known 5-second candles and verify the same.

## Missing execution candles

Do not fabricate missing seconds.

Classify the gap and determine whether the 1m candle is:

- complete under source semantics;
- incomplete;
- reset;
- rejected.

## Order eligibility

Signal created at the completed 1m boundary.

Verify the order cannot fill from any earlier second in that minute.

## Intraminute stop/target ordering

Verify a target touched at second 12 and stop touched at second 43 closes at target.

## Remaining one-second ambiguity

If both stop and target are touched in one 1-second OHLC candle, apply the selected ambiguity policy and mark the trade ambiguous.

## Equivalence

Where one-minute candles do not contain ambiguous intraminute behaviour, Fast and precision modes should produce semantically equivalent strategy decisions.

## Batch equivalence

Per-execution-frame and minute-batch processing must produce the same canonical trade fingerprint.

---

# 34. Required source-capability tests

Verify:

```text
OANDA + 1s → rejected
OANDA + 5s → accepted
Recorded quotes + 1s → accepted
Inline 1s source → accepted
```

Verify that unsupported requests fail before job execution.

Verify no synthetic 1-second candles are generated.

---

# 35. Required custom-timeframe tests

Test:

```text
1m,3m,5m,15m,30m,2h
```

Verify UTC boundaries.

Test strategy configuration:

```text
2h trend
30m confirmation
5m entry
```

Verify required intervals are automatically added.

Reject:

- entry >= confirmation;
- confirmation >= trend;
- incompatible execution/analysis divisibility;
- unsupported source granularity.

---

# 36. Required replay and UI tests

Add tests for:

- live trade emitted before simulation completes;
- event-driven lifecycle chunks;
- 1m chart replay during 1s execution;
- execution detail loaded only around selected trades;
- no full-year 1s browser load;
- job switching;
- chunk de-duplication;
- SignalR reconnect;
- stale revision ignored;
- selected strategy interval controls;
- unsupported 1s OANDA option disabled;
- capability warning;
- precision mode estimate.

---

# 37. Performance benchmarks

Run:

## Fast

```text
1m execution
1m analysis base
two strategies
```

## OANDA-native precision

```text
5s execution
1m analysis base
two strategies
```

## Synthetic/recorded high precision

```text
1s execution
1m analysis base
two strategies
```

Use at least:

- one trading day;
- one trading week;
- one month where feasible;
- 500,000+ synthetic execution candles.

Measure:

```text
time to first execution frame
time to first analysis candle
time to first replay chunk
time to first trade
execution frames/sec
analysis candles/sec
peak memory
allocations
GC counts
cache size
replay size
worker wait
batch size
total runtime
```

Compare:

- per-second barriers;
- one-minute execution batches.

Use measurements to choose defaults.

---

# 38. Default recommendations

Unless benchmarks prove otherwise:

```text
Default normal development:
1m execution
1m analysis base

Default OANDA precision:
5s execution
1m analysis base

Optional true high precision:
1s execution from recorded/imported 1s or tick data
1m analysis base
```

Do not default year-long jobs to 1-second mode without warning.

---

# 39. Current remaining dashboard work

Complete the Simulator panel by replacing textual OHLC-only playback with:

- real candlestick chart;
- live lifecycle markers;
- incremental trade journal;
- rich strategy metrics;
- analysis interval selector;
- execution precision selector;
- selected trade replay;
- 1s/5s zoom around trade.

The browser should never need to load every execution candle for the entire year.

---

# 40. Clean packaging

The current uploaded archive still includes generated folders such as:

- `node_modules`;
- `dist`;
- `.cache`;
- generated simulation output.

Produce a source-only archive excluding:

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

Keep lockfiles and placeholder `.env.example` files.

Run the clean archive through the build process from scratch.

---

# 41. Validation commands

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

Run all new precision, custom-timeframe, cache, live-trade, chart, and determinism tests.

Do not claim a command passed unless it was run.

---

# 42. Required final response from Codex

Return:

1. Files changed grouped by project.
2. Features preserved from the prior pass.
3. Execution/analysis interval architecture.
4. OANDA 5-second capability handling.
5. True 1-second source implementation or explicit remaining limitation.
6. Proof that no fake 1-second interpolation exists.
7. Custom strategy timeframe implementation.
8. Effective analysis interval union behaviour.
9. Live trade implementation.
10. Chart integration.
11. Prefetch/cache corrections.
12. Job-state persistence correction.
13. Dedicated-thread decision.
14. MFE/MAE implementation.
15. Rich performance metrics.
16. Build result.
17. Test totals.
18. Frontend build result.
19. Benchmark table for 1m, 5s, and 1s.
20. Peak memory and replay sizes.
21. Determinism fingerprints.
22. Clean-source archive confirmation.
23. Remaining limitations stated explicitly.

Do not return only documentation or claims without code and test evidence.

---

# 43. Priority order

Implement in this order:

1. Add source capability validation.
2. Separate execution interval from analysis base interval.
3. Add two-stage execution-to-1m aggregation.
4. Add 5s OANDA precision mode.
5. Add genuine 1s recorded/imported source capability.
6. Add minute-aligned execution batching.
7. Add custom trend/confirmation/entry intervals.
8. Add shared interval parser and validation.
9. Make trades incremental and event-driven.
10. Integrate the real chart.
11. Add execution-detail replay around trades.
12. Fix production low-watermark acquisition.
13. fix cache lifecycle and full validation.
14. serialise job-state updates.
15. resolve dedicated-thread semantics.
16. add MFE/MAE and rich metrics.
17. add instrumentation and benchmarks.
18. strengthen determinism/no-lookahead tests.
19. produce a clean source-only archive.

Correctness, real data provenance, deterministic results, and bounded memory are more important than claiming maximum precision or concurrency.
