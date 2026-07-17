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
| TradeManager | Pure deterministic break-even, structure/ATR, and adverse-structure recommendations |
| DashboardLive | Simulation API, job queue host, SignalR |
| Dashboard | Configuration, progress, playback, comparison UI |
| BacktestRunner | Thin CLI adapter over the application service |

## Application service and jobs

- `IBacktestApplicationService` starts, queries, pauses, resumes, and cancels jobs.
- `FileSimulationJobRepository` persists snapshots under `.cache/simulation-jobs`.
- A bounded `Channel` job queue owns background workers. Each running job also owns a single ordered async state actor; source/engine/control/trade/terminal updates are reduced to monotonic snapshots before SignalR and coalesced persistence.
- Refresh-safe: status, progress, errors, and output paths survive browser reload.

## Execution clock and management ordering

Simulation advances on completed execution candles (1m, OANDA-native 5s, or imported 1s):

```text
Read 1m candle
  → process existing orders/fills (per strategy worker)
  → aggregate 5m/15m/1h
  → update shared annotation snapshots
  → build immutable MarketFrame
  → evaluate strategies (sequential or parallel barrier)
  → evaluate TradeManager only when its configured management interval closes
  → atomically replace a risk-reducing stop, eligible from execution sequence N+1
  → create other orders eligible from next candle
  → write compact replay events
```

No lookahead: orders and amended stops created on frame `N` fill no earlier than frame `N+1`. R, MFE-R, and MAE-R always retain the immutable initial-stop denominator.

## Streaming source, cache, prefetch

- `IHistoricalCandleStream` yields `MarketCandle` without materialising a full year.
- `OandaStreamingCandleSource` pages OANDA, de-duplicates boundaries, retries transient failures, and writes/reads a versioned compressed cache.
- Prefetch uses hysteresis: fill toward channel capacity, suspend near capacity, then resume once unread rows drain to the low watermark. Diagnostics expose current/peak unread, pages, source/consumer waits, capacity, and low watermark.
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
  strategies/{id}/trades.ndjson
  strategies/{id}/trades.index.json
  strategies/{id}/performance.json.gz
  execution-detail/{strategy}/{setup}/chunk-*.json.gz
  execution-detail/{strategy}/{setup}/index.json
  COMPLETE | INCOMPLETE
```

A single ordered writer commits after the per-frame barrier.

## API surface

```text
POST   /api/simulations
GET    /api/simulations
GET    /api/simulations/{id}
POST   /api/simulations/{id}/pause|resume|cancel
POST   /api/simulations/imports
GET    /api/simulations/imports
DELETE /api/simulations/imports/{datasetId}
GET    /api/simulations/{id}/strategies|trades?cursor=|performance
GET    /api/simulations/{id}/replay[?from=&to=]
GET    /api/simulations/{id}/replay/chunks[/{chunkId}]
GET    /api/simulations/{id}/replay/execution-detail[/{chunkId}]?strategy=&setupId=
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

## Phase 4 — dynamic protection

- `StructureBasedTradeManager` is invoked by every production `StrategySimulationSession` on its configured management interval only.
- Cost-aware break-even includes entry/estimated-exit commission, spread, two-sided slippage, and an ATR buffer.
- Structural candidates use confirmed swings and available support/resistance zones with ATR buffers, minimum-improvement/cooldown gates, and never-widen rules.
- `ExecutionCoordinator.AmendProtectiveStopAsync` validates exact broker state and bypasses entry-only safety locks without bypassing reconciliation checks.
- The simulated broker replaces the stop atomically under one state lock and retains the bracket target/OCO group.
- Replay events and trades include stop requests, accept/reject/unsupported outcomes, structure source, locked R, and precise stop exit reason.
- OANDA reports this capability as unsupported until a native atomic implementation is certified.

## Final corrective notes (Legacy exit mode)

Legacy must declare `ExitManagementMode` through `ITradingAgent` as
`ProtectiveStopAndStrategyExit`. There is **no** default interface implementation
returning Bracket — that incorrectly forced R:R/take-profit requirements and
blocked Legacy entries with `TakeProfitPrice = null`.

## Phase 2 additions

- **True first-run streaming**: OANDA pages yield to the simulator while a temporary cache is appended; commit is atomic.
- **Low-watermark prefetch**: `IPagedHistoricalCandleSource` + `LowWatermarkPrefetchStream`.
- **Persistent strategy workers**: `StrategyWorkerHost` uses one bounded async task worker per strategy for the whole run.
- **Async frame-boundary pause**: `AsyncPauseGate`.
- **Coalesced job persistence** with monotonic `Revision`.
- **Completed job cleanup** from in-memory `_running`; CLI awaits completion TCS.
- **Bounded replay API** (`limit`/`startSequence`/`cursor`, max 5000).
- **SignalR client** in Dashboard with polling fallback; functional playback composable.
- **NearestToOpenFirst** OCO policy; historical bid/ask option disabled until implemented.

See `SIMULATOR_PHASE2_VALIDATION.md`.

## Quantitative risk / portfolio layer (added 2026-07-16 corrective pass)

A `SharedPortfolioRuntime` (`AccountMode = SharedPortfolioAccount`) coordinates multiple
strategy sessions through one admission layer: `PortfolioReservationBook`,
`PortfolioRiskManager`, `CapitalAllocator`, and `TradingSafetyController` (equity
high-watermark) are shared; each `StrategySimulationSession` still owns its own
`SimulatedBrokerClient` (see limitation below for what that still means for positions).
The account-level ledger built on top of those independent brokers is real, not just
aggregated for display: `SharedPortfolioRuntime.AggregateAccount()` reconciles balance
to one shared starting pool (not N independent ones summed) and, since the 2026-07-16
§6 pass, computes `MarginUsed` from **net exposure per instrument across strategies'
virtual lots** rather than summing each strategy's independently-calculated margin — two
strategies opposite-sided on the same instrument now correctly offset instead of each
being charged full margin, matching real broker margin economics.

Per-frame, `SharedPortfolioRuntime.FlushAsync`:

```text
aggregate account/heat/currency-exposure across sessions
  → evaluate correlation (RollingCorrelationClusters) and real notional net/gross
    currency exposure (CurrencyExposureCalculator) for each candidate
  → CapitalAllocator ranks and reserves/resizes opportunities against one shared budget
  → approved decisions submit through each session's own broker
```

**Correlation** (`PortfolioManager.Correlation.RollingCorrelationClusters`): real,
tested, fed from closed-candle log returns at a configured interval (default 1h). No
longer architecturally inert as of the 2026-07-16 §7 multi-instrument portfolio clock
(below) — `StreamingComparativeEngine` can now stream more than one instrument in a
single run, so a real second instrument's returns can reach it in production.

**Currency exposure** (`PortfolioManager.Risk.CurrencyExposureCalculator`): real
notional net/gross exposure per currency (not a 50/50 stop-risk split), gates
admission via `MaximumNetCurrencyExposurePercent`/`MaximumGrossCurrencyExposurePercent`,
distinct from the older stop-risk-based `MaximumCurrencyStopRiskPercent`. Account
currency is excluded from the exposure gate (it's the funding leg of every trade, not
foreign-currency concentration risk).

**Currency strength** (`PortfolioManager.CrossMarket.CrossMarketAnalysisCoordinator`):
real, leave-one-out, coverage-gated. Unlike correlation, this does *not* need
multi-instrument support to be live — `StreamingComparativeEngine` pre-fetches basket
instrument candles as a side channel, independent of what the run is actually trading,
and feeds `AgentMarketContext.CurrencyStrength` / `MetaLabelFeatures.CurrencyStrengthDifferential`.
Dashboard exposes enable/thresholds but not yet a basket editor (defaults to a
single-instrument basket, which leave-one-out then correctly reports as unavailable).

**Regime runtime context** (`AnalysisRuntimeContext`): `MarketRegimeClassifier` now
receives real executable-spread (spread/ATR) and data-quality signals from the
simulator instead of always defaulting to "unknown spread, data quality OK" —
`IlliquidUnsafe` can actually be reached via those inputs now.

**DST-aware sessions**: London/New York session classification converts through
`Europe/London`/`America/New_York` IANA zones instead of fixed UTC hours, so it
tracks UK/US daylight-saving transitions (previously wrong for roughly half of each
year, in opposite directions for each session).

**Value-location evidence** (`ChartAnnotator.Value.ValueLocationEvidenceEvaluator`):
optional, soft, reason-coded evidence of price's location relative to an anchored
value reference, wired into `ProgressiveStrategyBase` (Legacy/Improved) as a small
confidence nudge. Enabled by default for CLI/Dashboard requests that omit the field
(see the 2026-07-16 agent decision-quality pass below; direct `ProgressiveStrategyOptions`
construction, e.g. in tests, remains opt-in). No Dashboard chart layer for the anchored
value references themselves yet (`AnalysisSnapshot.ValueReferences` is computed and
replayed but not rendered).

**Economic event filter**: `EconomicEventFilterEnabled=true` now fails request
validation instead of silently reporting `Unavailable` — no production
`IEconomicEventProvider` exists in this codebase.

**Multi-instrument portfolio clock (added 2026-07-16, §7)**: `StreamingComparativeEngine`
runs a real chronological k-way merge across one `IHistoricalCandleStream` call per
distinct traded instrument (`Simulator/Engine/InstrumentPipelineState.cs` bundles each
instrument's own `AnalysisBaseAggregator`/`MultiTimeframeAggregator`/
`MarketDataQualityTracker`), picking the globally-earliest buffered candle each tick
(tie-broken by instrument value) so `SharedPortfolioRuntime` still observes strictly
increasing `(Sequence, AvailableAt)` across every instrument. Strategies are assigned an
instrument via `BacktestRequest.StrategyAssignments` (`StrategyType` + `InstrumentKey`,
optional `Id`, default `"{type}:{instrument}"`); routing frames to only the strategies
trading that instrument is a filter over the existing sequential/parallel-worker batch
processing, not new dispatch machinery — `StrategySimulationSession` and
`SharedPortfolioRuntime` needed zero changes, since both already derived "instrument"
from data handed to them rather than a session-level field. `ChartAnnotationEngine`'s
calibration freeze, previously a single global call on first crossing `EvaluationFrom`,
is now triggered per-instrument-first-crossing (idempotent — freezing an
already-frozen chart state is a no-op) so an instrument whose data starts later than
another's still gets its own (lazily-created) chart state frozen correctly. For a
single traded instrument every code path degenerates to exactly the pre-§7 behaviour —
proven by the full existing test suite passing unmodified, including hash-sensitive
tests like the sequential/parallel determinism check.

Exposed end-to-end: Dashboard (Simulator panel → "Multi-instrument portfolio clock"
section, repeatable strategy/instrument rows), CLI (`BacktestCommandOptions.StrategyAssignments`,
JSON/programmatic only, matching `AnnotationOptions`/`MarketRegimeRouting`), and the
Dashboard replay chart (candle series filtered by a selector when a run trades more
than one instrument — trade markers are not yet instrument-filtered, since `ReplayTrade`
doesn't expose `Instrument` in the API today; a real, minor, known gap). Manifest/replay
schema gained additive `Instruments`/`StrategyInstruments` fields and a per-row
`Instrument` on `MarketReplayRow` (both null/absent-safe for older single-instrument
manifests/chunks). Proven via `Simulator.Tests/MultiInstrumentPortfolioClockTests.cs`
(real two-instrument routing, per-instrument end-of-stream liquidation, staggered
warmup) and an end-to-end test through the real public `BacktestApplicationService`.
**Not yet proven**: a dedicated test that exact-correlated real streamed
multi-instrument data trips a correlation risk penalty end-to-end — the code path is
unchanged from what `CorrelationWiringTests.cs` (synthetic decisions) and the routing
tests above (real streamed data, generic assertions) already separately cover, so this
is a real but low-risk documentation gap rather than an open correctness question.
Deliberately unchanged: the shared broker-account **position**-level federation
(see Known limitations below) — §7 makes correlation/margin-netting/currency-exposure
reachable with real multi-instrument data, it does not touch position ownership.

See `TradingHub_Quantitative_Enhancements_Final_Audit_and_Corrective_Prompt.md` for
the full audit this section summarizes, and its findings' current status.

## Agent decision-quality pass (2026-07-16)

`ProgressiveStrategyBase` (Legacy/Improved) leaned on RANSAC trendlines/channels for
entries and stop/target selection, ignored several already-computed `AnalysisSnapshot`
signals, and carried two proven-but-dormant features. This pass wired in the real
gaps and flipped the dormant features on, staged so every default-changing item is
independently testable:

- **Channel stop/target candidates removed** from `ImprovedProgressiveAgent.SelectStop`/
  `SelectTarget` — trendlines/channels are judged unreliable and are no longer used to
  source new signals. `Options.MinimumChannelConfidence` remains for
  `StructureBasedTradeManager`'s existing trailing/opposing-structure logic, which is
  out of scope (management behavior, not a new signal).
- **Regime routing and value-location evidence now on by default** at the CLI
  (`BacktestCommandOptions`) and Dashboard (`SimulationApi.CreateSimulationRequest`)
  construction sites — both were already fully built and tested, just off. Direct
  `ProgressiveStrategyOptions`/`MarketRegimePolicyOptions` construction (tests,
  programmatic callers) stays opt-in.
- **`TrendQualityEvidenceEvaluator`** (`ChartAnnotator/Value/`) and
  **`CurrencyStrengthEvidenceEvaluator`** (`ChartAnnotator/CurrencyStrength/` — a
  separate namespace from `PortfolioManager.CurrencyStrength` to avoid a circular
  project reference) are new soft, reason-coded evidence types mirroring
  `ValueLocationEvidenceEvaluator`'s pattern: efficiency/choppiness, ATR/Bollinger
  volatility regime, and ADX strengthening for the former; base/quote currency-strength
  differential confluence for the latter. Both wired into
  `ProgressiveStrategyBase.EvaluateAsync` as additive confidence nudges. **Disabled by
  default** — new, unproven capability.
- **`PriceAction.ActiveRetest`** (already computed, previously discarded) now counts
  as a soft entry trigger and confidence nudge when a same-direction retest is in
  progress within `MaximumActiveRetestDistanceAtr` (default 0.35 ATR).
- **Real `BullishPullback`/`BearishPullback` detection** (`PriceActionAnalyzer.DetectPullback`)
  — the enum values existed but nothing ever emitted them. Gated on an "established
  trend" check using `MarketStructureSnapshot.Consecutive{Higher,Lower}{Highs,Lows}`
  (not a trendline/channel), with the continuation reference coming from swings/zones
  or the Bollinger midline. Feeds `PriceActionSnapshot` scoring/entry-trigger logic
  automatically once registered.
- **Confidence-aware stagnation sensitivity** (`StructureBasedTradeManager`,
  `EnableConfidenceScaledStagnation`): when enabled, interpolates the stagnation-bars
  threshold between `MinimumStagnationBarsAtLowConfidence` (8) and
  `MaximumStagnationBarsAtHighConfidence` (20) by `ManagedTradeState.EntryConfidence`,
  instead of the fixed `StagnationBars`. **Disabled by default.**
- **Adaptive risk-budget (drawdown/volatility scaling) now on by default** at the
  CLI/Dashboard construction sites, same convention as regime/value-location above
  (`RiskBudgetPolicy`'s own record default stays `false`). This is the largest
  default-changing item for equity-curve shape: any run drawing down 2%+ equity or
  sitting above the 80th volatility percentile now gets position size scaled down
  where it previously stayed at 1.0x.
- **Confidence-scaled position sizing** (`PositionSizer`, `EnableConfidenceScaledSizing`):
  linearly interpolates a 0.5x–1.0x multiplier on the sized quantity/risk budget
  between `ConfidenceMultiplierFloorConfidence` (40) and
  `ConfidenceMultiplierCeilingConfidence` (85), applied in both `FixedQuantity` and
  risk-based modes. Deliberately one-directional (never exceeds 1.0x) — `Confidence`
  is a rule score, not a calibrated probability. **Disabled by default.**
  `ExecutionCoordinator` needed no changes: it already passes the full `AgentDecision`
  (including `Confidence`) into `PositionSizingContext`, so this propagates
  automatically.
- **Deliberately deferred**: `PreTradeRiskOptions.MinimumConfidence` stays at its
  inert `0` default — picking a hard confidence floor before this pass's changes to
  what `Confidence` is composed of, and before any backtest data shows what range
  separates positive from negative edge, would be arbitrary.

| Change | Before | After |
|---|---|---|
| Channel stop/target candidates | considered | never considered |
| `MarketRegimePolicyOptions.Enabled` (CLI/Dashboard) | `false` | `true` |
| `ValueLocationEvidenceOptions.Enabled` (CLI/Dashboard) | `false` | `true` |
| `PriceAction.ActiveRetest` as entry trigger | unused | used (soft) |
| `BullishPullback`/`BearishPullback` emitted | never | emitted, feeds score/entry |
| `AdaptiveRiskOptions.Enabled` (CLI/Dashboard) | `false` | `true` |
| `TrendQualityEvidence`, `CurrencyStrengthEvidence`, confidence-scaled stagnation, confidence-scaled sizing | n/a | new, **off** by default |

Dashboard note: `SimulatorPanel.vue`'s form always sends an explicit value for
`regimeEnabled`/`valueLocationEvidenceEnabled`/`adaptiveRiskEnabled` in every request,
so the C#-side default flips above only affect raw API callers that omit the field —
they do not change the Dashboard UI's behavior. The "Research baseline" preset
deliberately keeps all three off (unconstrained control profile); "Day trading" and
"Scalping" deliberately keep `regimeEnabled` off (compression-hour routing otherwise
starves the sample) but already had `adaptiveRiskEnabled`/`valueLocationEvidenceEnabled`
on. No preset changes were made as part of this pass.

## Known limitations

- Historical bid/ask OANDA components not implemented (option off / documented).
- OANDA protective-stop amendment is intentionally unsupported; no cancel-first fallback is used.
- The main chart stays at analysis-base resolution; sub-minute execution detail is loaded per selected trade.
- Stage-level allocation profiling across ChartAnnotator internals is incomplete.
- `Simulator` is not fully AOT-compatible due to JSON job/cache/replay I/O.
- **Shared portfolio mode's account ledger (balance + margin) is real and netted; its
  positions are still a federation, not one authoritative broker-side ledger.** As of
  the 2026-07-16 §6 pass, `SharedPortfolioRuntime.AggregateAccount()` reconciles one
  shared balance pool and nets `MarginUsed` by real exposure per instrument across
  strategies (see architecture note above) — that part of "one ledger" is done and
  tested (`Simulator.Tests/SharedPortfolioIntegrationTests.cs`). What remains
  deliberately out of scope: same-instrument **positions** across strategies still
  cannot be reconciled to one broker-side netted position (two opposing broker
  positions instead of one net position). This was investigated in depth and
  intentionally not attempted — the underlying simulated broker already nets
  positions per-instrument internally (`SimulatedBrokerState`), but
  `StrategySimulationSession`'s trade-lifecycle logic (stops/trailing/scale-outs,
  ~2400 lines) assumes it owns 100% of whatever position its own broker reports —
  every quantity it reads is filtered by instrument only, not by strategy ownership
  (`StrategySimulationSession.cs:331-436`) — and `ExecutionCoordinator.AmendProtectiveStopCoreAsync`
  hard-rejects any stop amendment whose quantity doesn't exactly match the full broker
  position (`ExecutionManager/Execution/ExecutionCoordinator.cs:388-415`). Sharing one
  broker instance safely needs every exit-management code path retrofitted to a
  virtual-lot model first; no such concept (`VirtualLot`/`PositionShare`/etc.) exists
  anywhere in the codebase today.
- **No longer single-instrument per run (2026-07-16, §7)** — see the "Multi-instrument
  portfolio clock" architecture note above. Real cross-instrument correlation/margin/
  currency-exposure engagement in production now only needs a request that actually
  assigns different instruments to different strategies; nothing further to build.
  Trade markers on the Dashboard replay chart are not yet instrument-filtered (only
  the candle series is), and there is no dedicated test proving an exact-correlated
  real streamed pair trips a correlation penalty end-to-end (the underlying code path
  is covered separately by synthetic-decision and real-routing tests — see above).
- No operational research runner (walk-forward/ablation/sensitivity/calibration CLI
  workflow) exists yet — the QuantResearch library primitives (folds, Monte Carlo,
  ablation callback runner) are present but not wired into a CLI or Dashboard flow.
- No calibration-artifact repository (upload/validate/select setup or management
  calibration, or a meta-label model, from Dashboard/CLI) exists yet.
- CLI (`BacktestRunner`) exposes materially less configuration than the Dashboard API;
  most advanced options (annotation, regime policy, correlation, currency
  strength/exposure, value-location evidence) are programmatic/JSON-only, with no
  individual `--flag` and no `--config <file.json>` mechanism yet.
- Live broker functionality (partial close, reduce-only, stop amendment, OCO,
  reconnect/reconciliation) has not been certified against a demo account.
