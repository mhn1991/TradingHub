# TradingHub Phase 4 — Structure/ATR Trailing Stops, Protective-Order Amendment, and Production Completion

## Role

Act as a senior .NET architect, quantitative trading-system engineer, broker-integration engineer, backtesting specialist, concurrency engineer, test engineer, and Vue/ASP.NET Core full-stack developer.

Work directly against the supplied **existing TradingHub solution**.

Do **not** create:

- a second simulator;
- a duplicate TradeManager;
- a second ExecutionManager;
- a separate trailing-only Agent;
- a disconnected dashboard;
- a fake broker amendment implementation that is not used by the real simulator;
- synthetic one-second candles created from larger candles.

This is a focused **Phase 4 integration and production-hardening pass**.

The largest missing feature is the integration of the already existing:

```csharp
TradeManager.StructureBasedTradeManager
```

into:

- the Legacy Progressive strategy lifecycle;
- the Improved Progressive strategy lifecycle;
- the simulator;
- broker-neutral execution;
- protective-order reconciliation;
- replay;
- the dashboard;
- tests and deterministic comparison.

The requested trailing technique is:

```text
market structure determines where the stop belongs
+
ATR provides a volatility buffer
+
initial-risk R thresholds determine when the stop may move
```

This is not a simple fixed-distance ATR trail.

---

# 1. Current repository status

The current repository already contains important working features. Preserve them.

## Existing simulator features

- Dashboard-first simulation jobs.
- Shared `IBacktestApplicationService`.
- OANDA streamed historical data.
- Incremental cache writing.
- Complete cache manifest validation.
- One-minute and sub-minute execution modes.
- OANDA 5-second execution support.
- Imported 1-second/5-second candle datasets.
- Explicit execution interval and analysis-base interval.
- Two-stage aggregation:
  - execution candle → analysis-base candle;
  - analysis-base candle → higher analysis intervals.
- Custom strategy intervals:
  - trend;
  - confirmation;
  - entry.
- Sequential and parallel strategy workers.
- Minute-aligned worker batching.
- Strategy failure isolation.
- SignalR progress and live trade notification.
- Incremental live trade NDJSON.
- `AnalysisChart` integration in Simulator mode.
- Main replay at the analysis-base interval.
- Sub-minute execution-detail replay around trades.
- MFE and MAE tracking.
- Richer strategy performance metrics.
- Correct Legacy no-target/no-R:R risk behaviour.
- Correct `NearestToOpenFirst`.
- Incomplete analysis buckets are discarded instead of marked complete.
- Honest midpoint-plus-spread fill metadata.
- Frontend typecheck/build currently succeeds.

## Existing TradeManager feature

The solution already contains:

```csharp
StructureBasedTradeManager
```

with recommendations for:

- hold;
- move stop;
- exit;
- break-even activation;
- ATR-buffered structural trailing;
- adverse structure-break exit;
- never widening a stop.

However, it is currently isolated.

The current `Simulator.csproj` does not reference `TradeManager`.

No production code creates or evaluates `StructureBasedTradeManager`.

The broker abstraction exposes order placement and cancellation, but no broker-neutral protective-stop amendment operation.

Therefore, neither Legacy nor Improved currently moves an open trade’s stop after entry.

---

# 2. Mandatory initial audit

Before modifying code:

1. Read:
   - `Readme.md`
   - `ARCHITECTURE_SIMULATOR.md`
   - `RUN_BACKTEST.md`
   - `VALIDATION.md`
   - `SIMULATOR_FINAL_VALIDATION.md`
   - `SIMULATOR_PHASE2_VALIDATION.md`
   - `SIMULATOR_PHASE3_VALIDATION.md`
   - all previous Codex prompts included in the repository.
2. Inspect:
   - `TradeManager/StructureBasedTradeManager.cs`
   - TradeManager tests
   - `StrategySimulationSession`
   - `SimulationRunner`
   - `StreamingComparativeEngine`
   - `ExecutionCoordinator`
   - `ITradingOrderClient`
   - simulated broker order/OCO implementation
   - OANDA order client/commands/DTOs
   - trade journal
   - replay contracts and writer
   - SignalR bridge
   - `SimulatorPanel.vue`
   - `AnalysisChart.vue`
   - Legacy and Improved agents.
3. Run baseline validation:
   ```bash
   dotnet restore TradingHub.slnx
   dotnet build TradingHub.slnx -c Release
   dotnet test TradingHub.slnx -c Release
   ```
4. Run frontend baseline:
   ```bash
   cd Dashboard
   rm -rf node_modules dist
   npm ci
   npm run typecheck
   npm run build
   ```
5. Record:
   - test totals;
   - existing benchmark;
   - trailing-related test count;
   - replay size;
   - one-month runtime;
   - current Legacy and Improved results without trailing.
6. Create:
   - `SIMULATOR_PHASE4_VALIDATION.md`.

Do not report a feature as complete unless it is used by the actual streamed comparative simulator and proven by tests.

---

# 3. Preserve project boundaries

The final responsibility split must remain:

```text
Agent
    entry setup progression
    signal generation
    strategy thesis/reverse close decisions

TradeManager
    post-entry position-management recommendations
    break-even
    structural/ATR trailing
    optional adverse-structure exit recommendation

RiskManager
    entry risk
    exposure
    maximum loss
    portfolio/account constraints

ExecutionManager
    validates and applies order actions
    opens/closes positions
    applies protective-stop amendments
    journals outcomes
    preserves idempotency

Brokers
    broker-neutral trading contracts
    broker-specific amendment capabilities
    OANDA/native mapping
    simulated broker implementation

Simulator
    chronological execution
    invokes TradeManager at safe analysis boundaries
    applies amendments from the next execution frame
    records deterministic lifecycle

Dashboard
    configuration
    current dynamic stop
    stop amendment history
    locked profit
    replay visualisation
```

Do not move broker mutation into `TradeManager`.

`TradeManager` must continue producing deterministic recommendations only.

`ExecutionManager` must apply recommendations safely.

---

# 4. Add a broker-neutral protective-order amendment contract

The current trading interface supports:

```csharp
PlaceOrderAsync
CancelOrderAsync
```

but not stop amendment.

Add a clear broker-neutral contract.

A suitable design is:

```csharp
public sealed record AmendProtectiveStopRequest
{
    public required InstrumentKey Instrument { get; init; }

    public required string PositionId { get; init; }

    public string? ExistingStopOrderId { get; init; }

    public required decimal NewStopPrice { get; init; }

    public required decimal PositionQuantity { get; init; }

    public required OrderSide PositionSide { get; init; }

    public required string ClientAmendmentId { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
}
```

```csharp
public enum ProtectiveStopAmendmentStatus
{
    Rejected,
    Accepted,
    Replaced,
    Pending,
    Unsupported,
    Unknown
}
```

```csharp
public sealed record ProtectiveStopAmendmentResult
{
    public required ProtectiveStopAmendmentStatus Status { get; init; }

    public required string ClientAmendmentId { get; init; }

    public string? PreviousStopOrderId { get; init; }

    public string? CurrentStopOrderId { get; init; }

    public required decimal RequestedStopPrice { get; init; }

    public decimal? AcceptedStopPrice { get; init; }

    public string? RejectionReason { get; init; }

    public required ExecutionCertainty Certainty { get; init; }
}
```

Expose the operation through an appropriate broker interface, for example:

```csharp
public interface IProtectiveOrderClient
{
    Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        AmendProtectiveStopRequest request,
        CancellationToken cancellationToken = default);
}
```

or extend `ITradingOrderClient` if that is a better fit.

Also expose broker capability information:

```csharp
public sealed record TradingBrokerCapabilities
{
    public required bool SupportsNativeStopAmendment { get; init; }

    public required bool SupportsAtomicOrderReplacement { get; init; }

    public required bool SupportsDependentOcoAmendment { get; init; }
}
```

Do not pretend all brokers support native amendment.

---

# 5. Amendment safety rules

A protective-stop amendment must be risk-reducing.

For a long position:

```text
new stop > current stop
new stop < current executable bid
```

For a short position:

```text
new stop < current stop
new stop > current executable ask
```

The operation must reject:

- a stop that increases initial risk;
- a stop that moves backwards;
- a stop on the wrong side of current executable price;
- zero/negative prices;
- quantity mismatch;
- wrong position side;
- wrong instrument;
- amendment for a closed position;
- amendment against a stale stop-order ID;
- invalid tick size/price precision;
- duplicate amendment IDs with different payloads.

A safety lock that blocks **new entries** must not block:

- a risk-reducing stop amendment;
- a position close.

RiskManager should not re-run entry R:R rules for a stop amendment.

Add a dedicated risk/safety assessment for amendments if necessary.

---

# 6. Atomicity and failure behaviour

Never cancel the existing protective stop first and leave the position unprotected.

Preferred order:

## Native atomic replacement

Use the broker’s native replace/amend operation when supported.

## Safe replacement fallback

Only use cancel/replace if the implementation can guarantee safe behaviour.

Required fallback semantics:

1. submit replacement stop;
2. verify acceptance;
3. preserve/transfer OCO linkage;
4. cancel old stop only after the new stop is active;
5. reconcile broker state;
6. if replacement fails, keep old stop active.

If the broker cannot safely guarantee this:

```text
return Unsupported
retain old stop
journal the recommendation as not applied
```

Do not implement an unsafe live-broker cancel-first fallback.

The simulator can implement an atomic in-memory replacement.

---

# 7. Simulated broker amendment

Implement deterministic stop replacement in the simulated broker.

The simulated operation must:

- identify the open position;
- identify the current active stop;
- preserve the target order;
- preserve OCO behaviour;
- preserve quantity;
- replace the stop atomically;
- mark the old stop cancelled/replaced;
- emit order events;
- update broker order state;
- become effective only from the next eligible execution candle;
- never use the analysis candle that generated the amendment to trigger the new stop retroactively.

For an amendment generated after analysis frame `N`:

```text
all execution prices contained in N are already history
new stop is eligible from execution frame N + 1
```

This is a strict no-lookahead requirement.

---

# 8. ExecutionManager integration

Add a broker-neutral method to `IExecutionCoordinator`, for example:

```csharp
Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
    ProtectiveStopAmendmentCommand command,
    ITradingBrokerClient broker,
    CancellationToken cancellationToken = default);
```

A suitable command:

```csharp
public sealed record ProtectiveStopAmendmentCommand
{
    public required InstrumentKey Instrument { get; init; }

    public required string StrategyId { get; init; }

    public required string SetupId { get; init; }

    public required string PositionId { get; init; }

    public string? ExistingStopOrderId { get; init; }

    public required OrderSide PositionSide { get; init; }

    public required decimal PositionQuantity { get; init; }

    public required decimal EntryPrice { get; init; }

    public required decimal InitialStopPrice { get; init; }

    public required decimal CurrentStopPrice { get; init; }

    public required decimal ProposedStopPrice { get; init; }

    public required decimal CurrentExecutablePrice { get; init; }

    public required decimal OpenProfitR { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required long EffectiveFromExecutionSequence { get; init; }
}
```

ExecutionManager must:

- validate improvement;
- validate executable price;
- normalise price precision;
- create a stable idempotency key;
- call broker amendment;
- journal requested/accepted/rejected/unsupported;
- reconcile result;
- trip safety only for genuinely dangerous execution uncertainty;
- keep the old stop when the amendment is rejected.

---

# 9. Position-management configuration

Create explicit strategy-specific post-entry management options.

For example:

```csharp
public enum TrailingStopMode
{
    Disabled,
    BreakEvenOnly,
    StructureAtr
}
```

```csharp
public sealed record PositionManagementOptions
{
    public TrailingStopMode Mode { get; init; } = TrailingStopMode.StructureAtr;

    public BarInterval? ManagementInterval { get; init; }

    public decimal BreakEvenActivationR { get; init; } = 1.0m;

    public decimal StructureTrailActivationR { get; init; } = 1.5m;

    public decimal AtrBufferMultiplier { get; init; } = 0.25m;

    public decimal BreakEvenBufferAtr { get; init; } = 0.05m;

    public decimal MinimumStopImprovementAtr { get; init; } = 0.05m;

    public decimal MinimumStopImprovementTicks { get; init; } = 1m;

    public int MinimumAnalysisBarsBetweenAmendments { get; init; } = 1;

    public bool ExitOnAdverseStructureBreak { get; init; }

    public bool PreserveBracketTarget { get; init; } = true;

    public bool IncludeEstimatedExitCostsAtBreakEven { get; init; } = true;
}
```

Add separate request settings:

```text
LegacyPositionManagement
ImprovedPositionManagement
```

Do not force both strategies to use identical thresholds.

---

# 10. Recommended defaults

Use explicit and documented defaults.

## Legacy Progressive

The Legacy strategy has no fixed take-profit, so profit protection is particularly important.

Recommended default:

```text
Mode: StructureAtr
Management interval: entry interval
Break-even activation: 1.0R
Structure trail activation: 1.5R
ATR buffer: 0.25 ATR
Adverse structure exit: false by default
Preserve target: not applicable
```

Legacy still closes on:

- original/trailing protective stop;
- confirmed reverse strategy setup;
- safety close;
- end-of-simulation liquidation.

Do not replace its reverse-signal exit with the TradeManager unless explicitly configured.

## Improved Progressive

The Improved strategy already has a target.

Recommended default:

```text
Mode: StructureAtr
Management interval: entry or confirmation interval, configurable
Break-even activation: 1.0R
Structure trail activation: 2.0R
ATR buffer: 0.25 ATR
Preserve bracket target: true
Adverse structure exit: use existing strategy invalidation by default
```

Use a later structural threshold for Improved to avoid choking bracket trades too early.

All defaults must remain configurable and benchmarked.

---

# 11. Cost-aware break-even

The current `StructureBasedTradeManager` break-even candidate uses:

```text
entry ± small ATR buffer
```

Improve it so “break-even” can protect expected costs.

Include configurable estimates for:

- entry commission;
- expected exit commission;
- spread;
- slippage.

For a long position, the break-even stop should be high enough that an executable bid-side stop approximately covers round-trip costs.

For a short position, use the executable ask side.

Do not claim a trade is risk-free merely because the stop equals the midpoint entry price.

Record:

```text
raw entry
cost-adjusted break-even
ATR buffer
final proposed break-even stop
```

---

# 12. Structural candidate rules

The TradeManager should choose the nearest valid **confirmed** structure behind price.

For a long position:

1. confirmed swing low below current price;
2. confirmed support/mixed zone below current price;
3. select the highest valid protective structural level;
4. place stop below it by ATR buffer.

For a short position:

1. confirmed swing high above current price;
2. confirmed resistance/mixed zone above current price;
3. select the lowest valid structural level;
4. place stop above it by ATR buffer.

Candidate must also:

- improve the current stop;
- be on the valid side of executable price;
- meet minimum ATR/tick improvement;
- use only snapshots available at the current frame;
- not use an unconfirmed swing;
- not use future structure;
- not move more than once from the same snapshot version;
- respect a configurable amendment cooldown.

Keep initial risk fixed for all R calculations.

Do not recalculate “1R” from the newly moved stop.

---

# 13. Avoid conflicting exit authorities

Agent and TradeManager may both recommend an exit.

Define deterministic priority:

1. existing protective stop/target fill;
2. safety/emergency liquidation;
3. explicit Agent close;
4. TradeManager adverse-structure close;
5. stop amendment;
6. hold.

If Agent and TradeManager both request close on the same frame:

- submit one idempotent close;
- record both reasons;
- do not submit duplicate orders.

For Legacy, reverse strategy close remains authoritative unless configured otherwise.

For Improved, existing structural invalidation remains authoritative.

TradeManager adverse-break exit should be independently configurable to avoid duplicate logic.

---

# 14. Integrate TradeManager into `StrategySimulationSession`

Add a `StructureBasedTradeManager` or interface abstraction to each strategy session.

Do not evaluate it every 1-second/5-second execution candle.

Recommended schedule:

```text
every execution frame:
    fills
    stops
    targets
    MFE/MAE
    account/equity

when configured management interval closes:
    inspect open position
    get latest immutable analysis snapshot
    evaluate TradeManager
    apply recommendation
```

For example:

```text
5s execution
1m analysis base
5m management interval
```

Trailing evaluation occurs only after the completed 5m snapshot is available.

The new stop becomes eligible from the next 5s execution candle.

---

# 15. Track initial stop separately from current stop

This is critical.

The current trade record has a single:

```csharp
StopLossPrice
```

Do not overwrite the value used to calculate initial risk.

Add:

```csharp
InitialStopLossPrice
CurrentStopLossPrice
FinalStopLossPrice
```

or make the existing field’s meaning explicit and add separate dynamic fields.

R-multiple, MFE-R, MAE-R, and open-profit-R must always use:

```text
initial risk = abs(entry - initial stop)
```

Trailing must not change the denominator.

---

# 16. Stop-amendment history

Add:

```csharp
public enum StopAmendmentReason
{
    BreakEven,
    StructureSwing,
    StructureZone,
    AtrFallback,
    Manual,
    Other
}
```

```csharp
public sealed record StopAmendmentRecord
{
    public required long RequestedSequence { get; init; }

    public long? EffectiveSequence { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public DateTimeOffset? AcceptedAt { get; init; }

    public required decimal PreviousStopPrice { get; init; }

    public required decimal ProposedStopPrice { get; init; }

    public decimal? AcceptedStopPrice { get; init; }

    public required decimal OpenProfitR { get; init; }

    public required decimal LockedProfitR { get; init; }

    public required StopAmendmentReason Reason { get; init; }

    public required string Explanation { get; init; }

    public required ProtectiveStopAmendmentStatus Status { get; init; }

    public string? PreviousStopOrderId { get; init; }

    public string? CurrentStopOrderId { get; init; }

    public string? RejectionReason { get; init; }
}
```

Add to trade records:

```text
stop amendment count
break-even activated at
structure trailing activated at
maximum locked-in R
final stop
stop amendment history
```

Use a separate event file if embedding full history makes trade records too large.

---

# 17. Exit-reason precision

Differentiate:

```csharp
InitialStopLoss
BreakEvenStop
TrailedStructureStop
TakeProfit
ReverseStrategyClose
StructuralInvalidation
TradeManagerStructureExit
SafetyClose
EndOfSimulation
```

When a moved stop is hit, do not report it merely as generic `StopLoss`.

This is necessary to evaluate whether trailing improves outcomes.

---

# 18. Replay lifecycle events

Replace inferred count-only lifecycle where necessary with explicit events.

Add:

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
    BreakEvenActivated,
    StructureTrailActivated,
    StopAmendmentRequested,
    StopAmendmentAccepted,
    StopAmendmentRejected,
    StopAmendmentUnsupported,
    StopHit,
    TargetHit,
    StrategyCloseRequested,
    PositionClosed,
    TradeCompleted,
    StrategyFailed
}
```

`ChunkedReplayWriter` currently infers events from changes in counts.

For trailing, emit real event records from the session/execution path.

Each stop event should contain:

- strategy ID;
- setup ID;
- position ID;
- sequence;
- event time;
- previous stop;
- proposed stop;
- accepted stop;
- reason;
- open R;
- locked R;
- analysis interval;
- analysis snapshot version.

---

# 19. Dashboard trailing-stop controls

Add configuration fields to Simulator mode.

## Legacy management

```text
Trailing mode
Management interval
Break-even activation R
Structure activation R
ATR buffer
Minimum stop improvement
Adverse structure exit
```

## Improved management

Use separate values.

Show estimated behaviour before starting.

Example:

```text
Legacy:
Break-even at 1.0R
Structure trail at 1.5R
5m management
0.25 ATR buffer

Improved:
Break-even at 1.0R
Structure trail at 2.0R
5m management
Preserve target
```

Validate:

```text
BreakEvenActivationR > 0
StructureTrailActivationR >= BreakEvenActivationR
ATR multipliers >= 0
Management interval is in effective analysis intervals
```

Automatically include the management interval in the analysis interval union.

---

# 20. Dashboard dynamic-stop visualisation

`AnalysisChart` currently draws one horizontal stop from the trade record.

Replace or augment it with a time-varying stepped stop line.

Display:

- initial stop;
- break-even move;
- each structural move;
- rejected amendment markers;
- current stop;
- stop-hit marker;
- locked-in profit region where useful.

Tooltip should show:

```text
time
previous stop
new stop
open R
locked R
reason
structure source
ATR
status
```

Use a step-line, not a diagonal interpolation, because the stop changes discretely.

---

# 21. Open-position runtime panel

While a position is open, show:

```text
entry
initial stop
current stop
target
current open R
maximum open R
locked-in R
trailing mode
last management action
last management reason
next management interval close
```

For Legacy, make it clear that no fixed target is required.

---

# 22. Execution-detail replay correction

The current execution-detail writer captures a bounded tail when a trade completes and some frames afterward.

For long-running positions, that can lose the entry period and intermediate stop moves.

Change the capture policy.

Required default:

```text
configurable pre-entry tail
+
all execution frames while:
    entry order is pending
    or position is open
+
configurable post-exit tail
```

For 1s/5s mode, this preserves:

- exact entry;
- intratrade stop amendments;
- stop/target ordering;
- exit.

Keep the main market chart at the analysis-base interval.

Do not load full-year sub-minute data into the browser.

Add an execution-detail chunk index keyed by:

```text
strategy
setup/trade ID
first/last sequence
first/last time
```

---

# 23. Incremental trade API improvements

The current live trade endpoint reads the entire `trades.ndjson` file each time.

The SignalR client receives `TradeCompleted` but currently uses it mainly to trigger a full refetch.

Add cursor-based incremental retrieval:

```text
GET /api/simulations/{id}/trades
    ?strategy=legacy
    &cursor=...
    &limit=...
```

Return:

```json
{
  "items": [],
  "nextCursor": "...",
  "hasMore": true
}
```

The SignalR client should append the received trade payload directly when valid, then reconcile through the cursor endpoint.

Do not refetch all trades for every completion.

De-duplicate by:

```text
simulation ID + strategy ID + setup ID + closed time
```

---

# 24. Low-watermark semantics and metrics

The current `PrefetchingCandleStream` now uses the low-watermark value, but it gates the producer candle-by-candle and effectively maintains a backlog near the low watermark.

Review and document the intended semantics.

Prefer a page-aware source coordinator:

```text
unread <= low watermark
    → request next source page if none in flight

unread near capacity
    → producer waits
```

Do not needlessly restrict the buffer to `lowWatermark + 1` when capacity is much larger.

Expose:

- current unread;
- peak unread;
- pages requested;
- source waits;
- consumer waits;
- channel capacity;
- low watermark.

Benchmark the current and corrected behaviour.

---

# 25. Job-state update pipeline

The current progress path uses:

```csharp
PersistNonTerminal(...)
```

which synchronously blocks on an asynchronous coalescing store.

Replace it with one owned asynchronous job-state actor.

Required flow:

```text
source progress
engine progress
status changes
trade notifications
control changes
terminal state
    ↓
single ordered channel per job
    ↓
snapshot reducer
    ↓
monotonic revision
    ↓
SignalR
    ↓
coalesced persistence
```

Requirements:

- no synchronous `.GetAwaiter().GetResult()` in progress callbacks;
- no fire-and-forget mutation;
- no stale revision overwrite;
- terminal state is flushed and awaited;
- persistence errors are observed;
- state updates remain ordered.

---

# 26. Worker mode cleanup

`StrategyWorkerMode.DedicatedThread` is retained as a compatibility enum but currently runs the normal task worker.

Do one of:

1. implement a real dedicated synchronous worker with stable OS-thread identity and benchmark value; or
2. remove the option from:
   - enum;
   - API;
   - CLI;
   - Dashboard;
   - docs.

Do not expose a mode that behaves identically to another mode under a misleading name.

Task workers are acceptable as the default.

---

# 27. Historical bid/ask status

The current OANDA path is midpoint plus configured spread.

Keep result metadata honest.

Do not enable historical bid/ask until the source actually downloads:

```text
mid
bid
ask
```

and the simulator uses executable sides.

If implemented later:

```text
buy entry → ask
sell entry → bid
long stop/exit → bid
short stop/exit → ask
```

Trailing-stop validation must use executable bid/ask.

Until then:

```text
FillModel = MidpointPlusConfiguredSpread
```

---

# 28. Imported dataset security and lifecycle

The Dashboard now supports CSV upload.

Complete dataset lifecycle:

- use server-owned generated IDs;
- never trust client paths;
- validate file extension and content;
- validate exact interval;
- validate chronological ordering;
- validate duplicates;
- validate OHLC;
- limit size;
- stream validation;
- store metadata;
- add retention/cleanup;
- prevent path traversal;
- do not expose physical server paths;
- allow selecting previously imported datasets;
- delete expired datasets safely.

Do not retain orphaned 2 GiB files indefinitely.

---


---

# 30. Clean source packaging

The supplied archive still contains generated content including:

```text
Dashboard/node_modules
Dashboard/dist
.cache
some bin output
```

Produce a clean source-only archive excluding:

```text
.git/
.idea/
bin/
obj/
node_modules/
dist/
.cache/
simulation output/
imported datasets/
temporary files/
.env
.env.*
```

while preserving:

```text
.env.example
.env.integration.example
package-lock.json
source
tests
documentation
```

Validate the clean archive from scratch.

---

# 31. Strengthen the Legacy automated regression

The current test:

```text
Legacy_CanOpenPosition_WithoutTakeProfit_OnSyntheticStream
```

calculates:

```csharp
bool anyTradeActivity = ...
```

but does not assert it.

Replace the discarded value with a real assertion.

The test must fail if Legacy again produces zero trade activity because of R:R rejection.

Also assert:

- no target is required;
- at least one submitted or filled order;
- no R:R missing-target rejection in journal;
- trailing can activate once thresholds are reached;
- current stop moves in the favourable direction;
- initial risk remains unchanged.

---

# 32. Required TradeManager unit tests

Add comprehensive unit tests.

## Long break-even

- below threshold → hold;
- at threshold → move stop;
- stop covers configured costs;
- stop remains below current executable price.

## Short break-even

Equivalent short-side behaviour.

## Structural long trail

- selects nearest valid confirmed swing/zone;
- applies ATR buffer;
- improves current stop;
- never widens.

## Structural short trail

Equivalent short-side behaviour.

## Candidate precedence

Test:

- swing only;
- zone only;
- both;
- candidate behind current stop;
- candidate beyond current price;
- stale snapshot;
- ATR unavailable;
- minimum improvement not met;
- cooldown not met.

## Initial risk

Verify R remains based on initial stop after multiple moves.

## Adverse structure

Verify policy on/off.

---

# 33. Required amendment integration tests

## Simulator long trade

1. open long;
2. price reaches 1R;
3. break-even recommendation;
4. amendment accepted;
5. old stop replaced;
6. new stop active only next execution frame.

## Simulator short trade

Equivalent.

## Structure trail

1. reach structure activation;
2. confirmed new swing/zone;
3. stop moves with ATR buffer;
4. later new structure moves it again;
5. stop never moves backwards.

## OCO preservation

For Improved:

- target remains active;
- amended stop remains in the same protective relationship;
- target fill cancels current stop;
- stop fill cancels target.

## Rejection

When amendment is invalid or broker rejects:

- old stop remains active;
- trade remains protected;
- event and journal record rejection.

## Duplicate amendment

Same amendment ID and payload is idempotent.

## Conflicting duplicate

Same ID with different proposed stop is rejected.

## Same-frame ordering

A stop amendment generated at an analysis close cannot be hit by earlier price movement in the same completed analysis candle.

## Sequential/parallel determinism

Stop amendment history and final trades match.

## 1m/5s/1s

Test the amendment ordering in all supported execution intervals.

---

# 34. Trailing strategy comparison

Provide a benchmark/comparison mode.

At minimum compare:

```text
Legacy without trailing
Legacy with structure/ATR trailing

Improved without trailing
Improved with structure/ATR trailing
```

Do not overwrite the original strategy behaviour without giving a reproducible configuration.

Report:

- net P/L;
- maximum drawdown;
- profit factor;
- win rate;
- average R;
- median R;
- average MFE;
- average MAE;
- MFE captured;
- initial-stop exits;
- break-even exits;
- trailed-stop exits;
- target exits;
- reverse exits;
- average amendments per trade;
- average locked R;
- profit given back from MFE to exit.

This is necessary to determine whether trailing improves the system rather than merely sounding safer.

---

# 35. Dashboard performance metrics for trailing

Add:

```text
Break-even activations
Structure-trailing activations
Accepted stop amendments
Rejected stop amendments
Unsupported amendments
Average amendments/trade
Average maximum locked R
Average profit giveback from MFE
Trailing-stop exits
Break-even exits
```

Allow filtering the trade journal by exit type.

---

# 36. Deterministic result fingerprint

Extend the canonical fingerprint to include:

- initial stop;
- every stop amendment request;
- amendment acceptance/rejection;
- effective sequence;
- accepted stop;
- amendment reason;
- final stop;
- stop-hit type;
- locked R;
- all existing setup/order/fill/exit fields.

Compare:

- sequential;
- parallel;
- cached and uncached;
- 1m;
- 5s;
- imported 1s where test data exists.

---

# 37. Performance and memory benchmarks

Run:

## 1-minute

One month, both strategies, trailing enabled and disabled.

## OANDA-native precision

5-second execution / 1-minute analysis.

## Imported precision

1-second execution / 1-minute analysis using deterministic test data.

Measure:

```text
execution frames/sec
analysis candles/sec
management evaluations/sec
amendment requests/sec
accepted amendments
rejected amendments
barrier wait
cache read/write
replay write
peak memory
GC counts
output size
total runtime
```

Ensure trailing evaluation runs only on the management interval and does not run on every execution second.

---

# 38. Validation commands

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

Secret check:

```bash
find . \
  -type f \
  \( -name '.env' -o -name '.env.*' \) \
  ! -name '.env.example' \
  ! -name '.env.integration.example'
```

Packaging check must show no generated/secrets directories.

Do not claim success unless the commands were executed.

---

# 39. Required Dashboard acceptance scenario

1. Open Simulator.
2. Select Legacy and Improved.
3. Configure:
   - precision;
   - analysis intervals;
   - strategy intervals;
   - Legacy trailing options;
   - Improved trailing options.
4. Start simulation.
5. See live chart and trades.
6. Open a Legacy position.
7. Observe initial stop.
8. At 1R, observe break-even recommendation and accepted amendment.
9. At structure threshold, observe swing/zone ATR trail.
10. See the stop step line move only in the profitable direction.
11. See current locked R.
12. Verify no target is required for Legacy.
13. Observe Improved target retained while stop trails.
14. Select a trade.
15. Load sub-minute execution detail from entry through exit.
16. Inspect every stop amendment.
17. Compare trailing enabled/disabled performance.
18. Refresh browser without losing events.
19. Re-run from cache.
20. Verify deterministic fingerprint.

---

# 40. Required final response from Codex

Return:

1. Files changed grouped by project.
2. Confirmation that `StructureBasedTradeManager` is now used by production simulation.
3. Broker-neutral amendment contract.
4. Simulated broker amendment implementation.
5. OANDA/native amendment status.
6. Atomicity and fallback behaviour.
7. Legacy trailing defaults.
8. Improved trailing defaults.
9. Cost-aware break-even implementation.
10. Structural candidate rules.
11. No-lookahead amendment ordering.
12. Stop-amendment history model.
13. Replay lifecycle events.
14. Dashboard dynamic stop line.
15. Live current-stop and locked-R panel.
16. Execution-detail capture policy.
17. Incremental trade cursor improvements.
18. Low-watermark changes.
19. Job-state actor changes.
20. Security cleanup result.
21. Clean archive result.
22. Build result.
23. Test totals.
24. Frontend result.
25. Benchmark table.
26. Trailing on/off comparison.
27. Remaining limitations stated explicitly.

Do not return only an architecture document.

---

# 41. Priority order

Implement in this order:

1. Add broker-neutral protective-stop amendment contracts.
2. Implement deterministic atomic amendment in the simulated broker.
3. Add ExecutionManager amendment validation/journaling.
4. Integrate `TradeManager` into `StrategySimulationSession`.
5. Separate initial and current stop state.
6. Add stop-amendment history.
7. Add Legacy trailing.
8. Add Improved trailing while preserving target/OCO.
9. Add strict no-lookahead tests.
10. Add explicit replay lifecycle events.
11. Add dynamic stop visualisation.
12. Correct execution-detail capture across the whole open trade.
13. Add incremental trade cursors/direct SignalR append.
14. Strengthen Legacy regression assertion.
15. Improve low-watermark semantics/metrics.
16. replace blocking job persistence with an async actor.
17. remove or honestly implement DedicatedThread.
18. clean secrets and packaging.
19. run deterministic comparisons and benchmarks.
20. document all remaining broker limitations.

Correctness, preserved protection, deterministic ordering, and honest broker capability reporting are more important than moving stops frequently.
