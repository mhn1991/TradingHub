# TradingHub Structural Confluence Agent — Improvement Implementation Prompt

**Date:** 2026-07-19  
**Audience:** Codex / Claude / any implementation agent  
**Owner context:** TradingHub multi-agent simulator + dashboard experiment stack  
**Goal:** Implement a sequenced, measurable improvement program for the Structural Confluence agent — starting with research throughput and telemetry, then edge quality, without unfocused refactors.

---

## 0. How to use this document

1. Implement work **in package order** (P0 → P1 → P2 → P3). Do not skip ahead to new playbooks or ML.
2. Each package must leave the tree **buildable** and preferably covered by focused tests.
3. Prefer **small, reviewable PRs / commits** per package (or clearly labeled sub-steps).
4. Do **not** change trading identity hashes casually. Operational switches that do not affect decisions must remain `[JsonIgnore]` / non-hash-affecting where that pattern already exists.
5. Do **not** enable automatic live/demo certification.
6. When finished with a package, update the checklist at the end of this file (or a short `*_Final_Report.md`) with:
   - files touched
   - tests run + results
   - residual risks
   - how to verify manually

**Repo root:** `/home/mhn70/RiderProjects/TradingHub`  
**Solution:** `TradingHub.slnx`

---

## 1. Background and diagnosis (read before coding)

### 1.1 What the Structural agent is

Registered agent type:

| Field | Value |
|---|---|
| Type ID | `structural-confluence` |
| Display name | Structural Confluence |
| Exit mode | Bracket |
| Catalog | `Agent/Factories/TradingAgentCatalog.cs` |
| Type IDs | `Agent/Configuration/TradingAgentTypeIds.cs` |

Architecture (already implemented):

```text
AgentMarketContext
  → StructuralEvidencePacketFactory (context / setup / trigger TFs)
  → IStructuralPlaybook[] (stateful)
      - LiquidityBreakRetestPlaybook
      - LiquiditySweepReversalPlaybook
      - SupplyDemandPullbackPlaybook
  → StructuralCandidateArbitrator
  → StructuralGeometry (+ brackets)
  → AgentDecision (Observe or Trade)
```

Core files:

| Area | Path |
|---|---|
| Agent | `Agent/Strategies/StructuralConfluence/StructuralConfluenceAgent.cs` |
| Options | `Agent/Strategies/StructuralConfluence/StructuralConfluenceStrategyOptions.cs` |
| Evidence | `Agent/Strategies/StructuralConfluence/Evidence/*` |
| Playbooks | `Agent/Strategies/StructuralConfluence/Playbooks/*` |
| Arbitration | `Agent/Strategies/StructuralConfluence/StructuralCandidateArbitrator.cs` |
| Geometry | `Agent/Strategies/StructuralConfluence/StructuralGeometryBuilder.cs` |
| Identity | `Agent/Strategies/StructuralConfluence/StructuralIdentity.cs` |

Playbooks emit rich evaluations: mandatory gates, supporting/conflicting evidence, confidence contributions, lifecycle, geometry, pool/zone/sweep references (`PlaybookModels.cs`).

### 1.2 Observed production problems (2026-07-18 live diagnosis)

These are **measured**, not speculative:

1. **Throughput collapse on Structural learning**
   - Progressive / lighter runs historically ~**45–70 candles/sec**
   - Structural with Supply/Demand + Liquidity enabled ~**~5 candles/sec** per job
   - Two concurrent learning profiles ≈ ~10 c/s machine total

2. **Market replay snapshot bloat** (major historical tax)
   - Market chunks with **5000 rows**, ~**80 KB analysis JSON per row**, **500 swings** retained
   - ~**330–390 MB uncompressed** / ~**50–60 MB gzip** per chunk; outliers ~**617 MB** gzip
   - Training pipeline sets `CaptureMarketReplay = false` in `QuantResearch.Training/Pipeline/CalibrationTrainingPipeline.cs`
   - **Experiment executor does not** always disable replay for learning/evaluation
   - `ChunkedReplayWriter` now caps market chunks at 250 rows and trims swings to 100 for **new** code paths — still insufficient alone if replay is on and analysis is rich

3. **Zombie simulation jobs**
   - After host restart / interrupted experiments, many jobs remain `WarmingUp`/`Running` in API listings with frozen `processedBaseCandles` and stale low c/s
   - Confuses operators and experiment UI

4. **Insufficient research telemetry**
   - Playbook funnel (why no trade) is not first-class in experiment/calibration reporting
   - Calibration models already understand `StrategyId` + `PlaybookId` (`RiskManager/Calibration/SetupCalibration.cs`, `MetaLabel.cs`) but Structural learning does not fully exploit a **funnel + playbook outcome** surface

5. **Edge not yet proven**
   - `AutomaticDemoCertified` / `AutomaticLiveCertified` correctly false
   - Multiple experiment profiles (`s&d-CCI-soft`, `Structural baseline`, `sweep-cci-soft`) with high degrees of freedom before baseline results

### 1.3 Design principles for this work

- Structural agent skeleton is **good**; do not rewrite it.
- Prefer **instrumentation + cost control + gate/geometry quality** over new playbooks.
- Meta-model / calibration may **reduce risk or skip**, never increase size above base policy.
- Determinism and causal `AvailableAt` filtering must be preserved.
- Learning runs should not pay for chart-grade replay unless explicitly requested.

---

## 2. Success criteria (program level)

### Must achieve

- [ ] Experiment **learning** and **evaluation** can disable market replay by default (opt-in only for chart review).
- [ ] Live Structural learning throughput is **measurably improved** vs ~5 c/s baseline under comparable hardware (target: **≥2×** with replay off + warm cache + single profile; stretch **≥3×** if analysis sharing lands).
- [ ] Operators can see **per-playbook funnel and outcomes** for a completed learning run.
- [ ] Host restart marks orphan non-terminal sims as failed/cancelled rather than eternal WarmingUp.
- [ ] No change to automatic live certification.
- [ ] Focused unit/integration tests for each package; solution builds.

### Must not do

- Do not add new playbooks (order blocks, FVG-only, ICT variants, etc.).
- Do not replace structure with ML entry models.
- Do not silently weaken immutable profile content-hash checks.
- Do not run `rm -rf` on user calibration artifacts without an explicit opt-in tool/flag.
- Do not broad-refactor progressive agents, dashboard UI chrome, or unrelated Postgres schema.

---

## 3. Implementation packages

---

### Package P0 — Throughput & experiment correctness (do first)

**Intent:** Make Structural research runnable so later quality work can iterate.

#### P0.1 Disable market replay for experiment learning (and default evaluation)

**Files (primary):**

- `Simulator/Experiments/BacktestSimulationExperimentExecutor.cs`
- `Simulator/Models/BacktestConfiguration.cs` (`CaptureMarketReplay` is `[JsonIgnore]`, default `true`)
- `QuantResearch.Training/Pipeline/CalibrationTrainingPipeline.cs` (already sets `false` — keep consistent)
- Any DashboardLive experiment → backtest request mapping if it constructs `BacktestRequest` independently

**Requirements:**

1. Learning backtests for experiments **must** set `CaptureMarketReplay = false` unless an explicit opt-in exists.
2. Evaluation/held-out backtests should **default false** as well, with an explicit opt-in such as:
   - `BacktestRequest` / experiment options flag `CaptureMarketReplay = true`, or
   - experiment manifest flag `captureChartReplay: true`
3. Opt-in must not affect content hashes / profile identity (mirror existing `[JsonIgnore]` pattern on `BacktestRequest.CaptureMarketReplay`).
4. When replay is false:
   - no `market/chunk-*.json.gz` written
   - trades, strategy events, execution-detail (if still required), manifests, calibration artifacts still work
5. Add/extend test (pattern already exists):
   - `Simulator.Tests` — `Application_Service_Can_Complete_Without_Capturing_Market_Replay` style coverage for **experiment executor path**, not only training pipeline.

**Acceptance:**

- Fresh experiment learning run produces **zero** market chunks under the run output directory.
- Strategy trades + events still present.
- Single-run interactive simulation (dashboard single backtest) still defaults to replay **on** (user chart expectation), unless product owner later changes that.

#### P0.2 Keep / harden replay compaction when replay is on

**Files:**

- `Simulator/Replay/ChunkedReplayWriter.cs`
- `Simulator/Engine/StreamingComparativeEngine.cs`
- `DashboardLive/SimulationApi.cs` (streaming/gzip endpoints if touched)

**Requirements:**

1. Confirm market chunk row cap remains `min(profileChunkSize, 250)`.
2. Confirm replay swing trim remains last **100** swings; engine analysis capacity stays full (e.g. 500).
3. Extend compaction for replay-only snapshots (when analysis is non-null):
   - Cap supply/demand **active zones** and **recent events** to small bounds suitable for charts (e.g. 20 zones / 50 events — pick constants, document them).
   - Cap liquidity **active pools**, **recent events**, **recent sweeps** similarly.
   - Cap price-action diagnostics/events lists if they dominate size.
   - Prefer `with` expressions / immutable snapshot copies; do not mutate live engine state.
4. Unit test: compacting a fat `AnalysisSnapshot` reduces serialized size materially and preserves OHLCV row fields.

**Acceptance:**

- Synthetic fat snapshot compact path tested.
- Optional manual: one single-run with replay on writes chunks **well under** historical ~60 MB/5k rows pattern (expect order-of-magnitude smaller per row when S/D+Liq enabled).

#### P0.3 Orphan / zombie simulation job cleanup on host start

**Problem:** Jobs left `WarmingUp`/`Running` after process death remain listed forever with frozen c/s.

**Files (locate actual persistence):**

- `Simulator/Services/BacktestApplicationService.cs`
- Job repository implementations (file and/or Postgres):
  - `Simulator` job store interfaces
  - `TradingHub.Persistence.Postgres` / `DBManager.Postgres` simulation job entities if used by DashboardLive
- `DashboardLive/Program.cs` or simulation host startup

**Requirements:**

1. On DashboardLive / simulation host startup, mark non-terminal jobs that cannot be resumed as:
   - `Failed` or `Cancelled` with a clear error/reason, e.g. `HostRestartedWhileRunning`
2. Do **not** delete historical artifacts.
3. Experiment parent snapshots that point at dead child learning jobs should surface failure or allow retry without pretending progress is live.
4. Test: repository method or host bootstrap unit/integration test with a fake running job becomes terminal after recovery hook.

**Acceptance:**

- After restart, `/api/simulations` does not show multi-hour-old WarmingUp jobs with delta-0 progress as active work.
- New runs still start normally.

#### P0.4 Experiment concurrency defaults for Structural-heavy analysis (optional but recommended)

**Files:**

- `Simulator/Experiments/SimulationResourceGovernor.cs`
- `Simulator/Experiments/Models/*` parallelism / governor options
- Experiment create API defaults in `DashboardLive/SimulationApi.cs` or experiment application service

**Requirements:**

1. Document current defaults (`maxProfileGroups`, `maxTotalStrategyWorkers`, `maxHistoricalDownloadsPerBroker`).
2. For Structural experiments (or globally if safer), default **max concurrent profile learning groups to 1** when analysis enables supply/demand **or** liquidity — unless user overrides.
3. Keep download mutex per broker (already intended via governor) effective so two jobs do not thrash the same OANDA range.

**Acceptance:**

- Starting a 3-profile Structural experiment runs learning **sequentially** by default (or clearly documented override).
- Throughput per active job improves under CPU contention vs dual-running ~5 c/s each.

#### P0.5 Cache / download operational notes (code only if easy)

**Files:**

- `Simulator/MarketData/OandaStreamingCandleSource.cs`
- Prefetch streams under `Simulator/MarketData/`

**Requirements (minimum):**

1. Ensure progress `dataSourceStatus` distinguishes `DownloadingData` vs `ReadingCache` accurately when cache hit.
2. If a long-range cache tmp stalls, failures should surface rather than infinite WarmingUp with near-zero IO (investigate existing timeout/retry; fix only if a clear bug is found).

**Non-goals:** rewriting the entire candle cache format.

---

### Package P1 — Playbook funnel & research telemetry

**Intent:** Make “why no trade / which playbook earns” visible and calibratable.

#### P1.1 Define a stable funnel event model

Add a small, versioned model (name suggestions — pick one consistent with codebase style):

```text
StructuralFunnelSnapshot
  StrategyId
  Instrument
  AvailableAt
  Sequence? 
  PlaybookId?          // null for agent-level only
  Stage                // enum
  ReasonCode
  Direction?
  Confidence?
  SetupId?
  GatesPassed / GatesTotal?
  IsReady
```

**Suggested stages (enum):**

| Stage | Meaning |
|---|---|
| `AnalysisNotReady` | Missing TF snapshots |
| `EvidenceBuilt` | Packet created |
| `PlaybookEvaluated` | Per playbook result |
| `PlaybookArmed` | Lifecycle armed (if applicable) |
| `GatesFailed` / `GatesPassed` | Mandatory gates |
| `GeometryInvalid` / `GeometryValid` | Bracket geometry |
| `ArbitrationRejected` | Conflict / no candidate |
| `ArbitrationSelected` | Winner chosen |
| `OrderIntent` | Trade decision emitted |
| `SuppressedOpenPosition` | Already in position |
| `StaleEpoch` | Time travel / dup eval |

Emit stages from:

- `StructuralConfluenceAgent.EvaluateAsync`
- optionally playbooks when not ready (reason codes already exist)

**Important:** Funnel volume can be huge if emitted every bar. Requirements:

1. **Default:** aggregate counters in-memory per run (per playbook × reasonCode × stage).
2. **Optional detailed mode:** sample or emit on lifecycle transitions only (arm / expire / fire / reject-after-arm), not every dormant bar.
3. Never write full analysis snapshots into funnel storage.

#### P1.2 Persist funnel aggregates with simulation outputs

**Files:**

- `Simulator/Engine/StrategySimulationSession.cs` and/or agent runtime bridge
- `Simulator/Replay/ChunkedReplayWriter.cs` (sidecar JSON, not market chunks)
- Result types under `Simulator/Models/`

**Requirements:**

1. At run completion, write a sidecar such as:

   `strategies/<strategyId>/structural-funnel.json`

   containing:
   - schema version
   - per-playbook counts by stage + top reason codes
   - decision counts (observe vs buy/sell)
   - optional last-N transition events (bounded, e.g. 200)

2. Include funnel summary pointers in simulation manifest or strategy sidecar index if one exists.

3. Calibration training should be able to read funnel aggregates when present (even if unused for model fit yet).

#### P1.3 Surface funnel in experiment completion / API

**Files:**

- `Simulator/Experiments/*` result DTOs
- `DashboardLive` experiment/simulation API contracts
- Dashboard Vue only if minimal: show a compact table (optional in P1; API is mandatory)

**Requirements:**

1. Experiment profile learning result includes funnel summary (JSON field is enough).
2. API returns funnel for completed child jobs without loading market chunks.

#### P1.4 Ensure PlaybookId flows into trades / meta-labels

**Files:**

- `StructuralConfluenceAgent` trade decision construction
- `RiskManager/Calibration/MetaLabel.cs`
- Trade journal / `SimulatedTradeRecord` if playbook id is not already stored

**Requirements:**

1. Every Structural entry decision must carry playbook id in a durable field used by calibration (`SetupId` / reason list / explicit `PlaybookId` — prefer explicit if model allows without breaking serializers).
2. Closed trades used for setup calibration must not collapse all Structural trades into `playbook=unknown` when playbook was known.
3. Tests: decision from each playbook → meta-label/feature extraction sees correct playbook id.

**Acceptance:**

- After a short inline-candle or fixture backtest, `structural-funnel.json` exists and non-zero stage counts make sense.
- At least one unit test builds agent decisions through arbitration and asserts funnel counters.

---

### Package P2 — Edge quality (gates, lifecycle, geometry, arbitration)

**Intent:** Improve candidate quality without adding playbooks.  
**Gate:** Start P2 only after P0 replay-off is in and a learning run can finish.

#### P2.1 Lifecycle hardening

**Files:**

- `PlaybookStateStore.cs`, playbook classes, `PlaybookModels.cs`
- Options records in `StructuralConfluenceStrategyOptions.cs`

**Requirements:**

1. Enforce explicit max armed duration / max bars since catalyst (options already have several bar limits — audit each playbook uses them consistently).
2. Catalyst timestamp must be **≤** trigger confirmation timestamp (reject inverted narratives).
3. Invalidate armed setups on strong opposing structure break on setup TF (define using existing market structure / PA events; document rule).
4. One active armed setup per `(instrument, playbook, direction)` unless intentionally stacking (default: replace only if new candidate has higher mandatory quality floor).
5. Unit tests for: stale armed expiry, inverted catalyst/trigger, opposing invalidation.

#### P2.2 Geometry quality bar

**Files:**

- `StructuralGeometryBuilder.cs`
- Playbook geometry attachment paths

**Requirements:**

1. Reject geometry when:
   - stop distance > configured max ATR
   - reward/risk after **executable spread** &lt; minimum RR
   - stop/target on wrong side of entry
2. Prefer structural stops (zone distal / sweep extreme / invalidation) over pure ATR pads when structure available; ATR remains buffer only.
3. Target selection prefers opposing liquidity pool / S/D zone with obstacle awareness (builder already has obstacle concepts — complete/verify).
4. Every rejection has a stable `ReasonCode` (for funnel `GeometryInvalid`).
5. Tests with synthetic prices for accept/reject cases.

#### P2.3 Arbitration improvements

**File:** `StructuralCandidateArbitrator.cs`

**Current behavior:**

- No ready → no candidate
- Conflicting directions → hard `StructuralPlaybookConflict`
- Else sort by mandatory quality floor, geometry quality, confidence, catalyst time, playbook id
- Same-direction multi-ready → small confidence bump

**Requirements:**

1. Keep hard stand-down as **default** on true directional conflict.
2. Add structured rejection detail (losing playbook ids + scores) into arbitration result for funnel/logging.
3. Same-direction confluence boost only if candidates share location affinity (same zone id, or pool id, or distance ≤ configured ATR). Otherwise no boost (or smaller).
4. Deterministic ordering tests (existing style) updated.
5. Do not silently flip conflict into a trade without an options flag.

#### P2.4 Session / regime soft-hard filters (Structural options)

**Files:**

- `StructuralConfluenceStrategyOptions.cs` (+ nested option records)
- Evidence packet (session/regime fields if needed)
- Playbooks or a shared `StructuralPlaybookRules` helper

**Requirements:**

1. Add optional filters (default **off** or soft to preserve identity of existing profiles unless profile JSON sets them):
   - session preference windows (e.g. prefer London/NY for break-retest)
   - regime allowance matrix:
     - disorder/choppy → allow sweep reversal, suppress break-retest
     - strong trend → prefer pullback / break-retest, suppress countertrend sweeps
2. Filters must be serializable in agent definition / profile JSON.
3. When disabled, bit-identical decisions vs previous defaults for fixture tests.
4. When enabled, reason codes like `StructuralSessionFilter` / `StructuralRegimeFilter`.

**Identity note:** Changing defaults that affect decisions will change profile hashes — that is OK for **new** profile revisions; do not mutate existing frozen profile blobs in DB.

#### P2.5 Research profile pack (data, not just code)

Add example strategy profiles under `examples/` (or experiment profile seeds) documenting the locked research matrix:

| Profile name | Playbooks enabled | Notes |
|---|---|---|
| `structural-baseline` | all three | current soft CCI defaults |
| `structural-sd-only` | supply/demand pullback only | |
| `structural-sweep-only` | sweep reversal only | |

Each profile JSON/README must state: **change only one dimension per experiment revision.**

---

### Package P3 — Performance architecture (after P0/P1)

**Intent:** Raise Structural c/s without changing signals.

#### P3.1 Analysis profile de-dupe across experiment children

**Files:**

- `Simulator/Engine/StreamingComparativeEngine.cs`
- `Simulator/Engine/AnalysisProfileRegistry.cs`
- Experiment executor / shared run planner

**Requirements:**

1. When two profile runs share identical `AnalysisProfileKey` / annotation options + instrument + interval set, prefer **shared analysis engines** rather than N full annotators (registry already exists — ensure experiment path uses it across sequential runs via cache warming, or document that sequential runs still re-warm).
2. For parallel profiles with identical analysis keys, do **not** fork independent annotators unless `AnalysisSharingMode.IndependentPerStrategy` is requested.
3. Benchmark or at least unit-level assertion that registry returns same engine instance for equal keys.

#### P3.2 Evidence packet build cost

**File:** `StructuralEvidencePacketFactory.cs`

**Requirements:**

1. Avoid repeated OrderBy allocations where easy (reuse buffers carefully only if thread-safety allows; agent uses a lock today).
2. Filter zones/pools/sweeps to relevant recent windows for playbook evaluation (causal, configurable caps) **without** changing engine chart state.
3. Tests that caps do not drop the primary zone/pool needed for an armed setup within max age bars.

#### P3.3 Annotator heavy-path audit (scoped)

**Files under:** `ChartAnnotator/SupplyDemand/*`, `ChartAnnotator/Liquidity/*`, `ChartAnnotator/Engine/*`

**Requirements:**

1. Identify O(n²) or full-scan per candle paths on active zone/pool lists; fix only hot spots with tests.
2. Ensure disabled detectors are true no-ops (Structural profiles that disable a playbook should ideally disable unneeded detectors at analysis options level when safe).
3. Do not change detector science outputs without golden tests.

**Acceptance:**

- Document before/after c/s on a fixed fixture or one-day inline dataset if feasible in `Simulator.Tests`.
- No detector golden regressions.

---

## 4. Testing requirements

### Minimum commands (adjust to machine)

```bash
dotnet build TradingHub.slnx -c Release
dotnet test Simulator.Tests/Simulator.Tests.csproj --filter "FullyQualifiedName~Structural|FullyQualifiedName~Replay|FullyQualifiedName~Experiment" --verbosity minimal
dotnet test TradingCore.Tests/TradingCore.Tests.csproj --filter "FullyQualifiedName~Structural|FullyQualifiedName~Agent" --verbosity minimal
# If agent tests live elsewhere:
dotnet test --filter "FullyQualifiedName~StructuralConfluence" --verbosity minimal
```

Add tests in existing projects (`Simulator.Tests`, agent-related test projects). Prefer fixture candles over live OANDA.

### Required new/extended tests (checklist)

- [ ] Experiment/learning path does not write market chunks when replay false
- [ ] Replay compaction reduces snapshot size / swing count
- [ ] Host recovery marks orphan running jobs terminal
- [ ] Funnel aggregate sidecar written and schema-valid
- [ ] PlaybookId preserved into calibration-facing trade/decision fields
- [ ] Geometry reject reasons for RR/stop/side
- [ ] Arbitration determinism + location-affinity confluence boost
- [ ] Session/regime filters off → baseline fixture parity

---

## 5. Manual verification script (operator)

After P0+P1:

1. Restart `DashboardLive` (Release).
2. Confirm old zombie sims are terminal.
3. Launch Structural experiment with 1 profile, learning window short if needed for smoke.
4. Confirm:
   - c/s substantially above previous ~5 under dual load (single profile + warm cache)
   - run dir has **no** `market/chunk-*.json.gz` for learning
   - `structural-funnel.json` (or chosen name) exists at completion
5. Optional chart run: single interactive backtest with replay on; chunks remain downloadable and browser-safe.

---

## 6. Suggested PR / commit split

| Commit / PR | Package | Title idea |
|---|---|---|
| 1 | P0.1–P0.2 | `fix(sim): disable experiment market replay by default; harden replay compaction` |
| 2 | P0.3–P0.4 | `fix(sim): recover orphan simulation jobs; serialize structural-heavy learning` |
| 3 | P1 | `feat(structural): playbook funnel aggregates and playbook-id durability` |
| 4 | P2 | `feat(structural): lifecycle, geometry, arbitration, optional session/regime filters` |
| 5 | P3 | `perf(structural): analysis sharing and evidence hot-path` |

Do not squash unrelated dashboard cosmetic work into these.

---

## 7. File anchor index (quick navigation)

```text
Agent/Configuration/TradingAgentTypeIds.cs
Agent/Factories/TradingAgentCatalog.cs
Agent/Strategies/StructuralConfluence/StructuralConfluenceAgent.cs
Agent/Strategies/StructuralConfluence/StructuralConfluenceStrategyOptions.cs
Agent/Strategies/StructuralConfluence/StructuralCandidateArbitrator.cs
Agent/Strategies/StructuralConfluence/StructuralGeometryBuilder.cs
Agent/Strategies/StructuralConfluence/StructuralIdentity.cs
Agent/Strategies/StructuralConfluence/Evidence/StructuralEvidencePacketFactory.cs
Agent/Strategies/StructuralConfluence/Evidence/StructuralEvidencePacket.cs
Agent/Strategies/StructuralConfluence/Playbooks/IStructuralPlaybook.cs
Agent/Strategies/StructuralConfluence/Playbooks/PlaybookModels.cs
Agent/Strategies/StructuralConfluence/Playbooks/PlaybookStateStore.cs
Agent/Strategies/StructuralConfluence/Playbooks/LiquidityBreakRetestPlaybook.cs
Agent/Strategies/StructuralConfluence/Playbooks/LiquiditySweepReversalPlaybook.cs
Agent/Strategies/StructuralConfluence/Playbooks/SupplyDemandPullbackPlaybook.cs
Agent/Strategies/StructuralConfluence/Playbooks/StructuralPlaybookRules.cs

Simulator/Models/BacktestConfiguration.cs
Simulator/Services/BacktestApplicationService.cs
Simulator/Engine/StreamingComparativeEngine.cs
Simulator/Engine/AnalysisProfileRegistry.cs
Simulator/Engine/StrategySimulationSession.cs
Simulator/Replay/ChunkedReplayWriter.cs
Simulator/Experiments/BacktestSimulationExperimentExecutor.cs
Simulator/Experiments/SimulationExperimentApplicationService.cs
Simulator/Experiments/SimulationResourceGovernor.cs

QuantResearch.Training/Pipeline/CalibrationTrainingPipeline.cs

RiskManager/Calibration/SetupCalibration.cs
RiskManager/Calibration/MetaLabel.cs

DashboardLive/SimulationApi.cs
DashboardLive/Program.cs

ChartAnnotator/SupplyDemand/*
ChartAnnotator/Liquidity/*
ChartAnnotator/Engine/*
```

Related prior diagnosis notes:

- `AI_REVIEW_HANDOFF_2026-07-18.md` (replay OOM / chunk streaming)
- `TradingHub_Structural_Confluence_Agent_and_Simulator_Experiment_Blueprint_v2.md` (original design)

---

## 8. Explicit non-goals (remind implementer)

1. No new playbooks.
2. No automatic live trading certification.
3. No silent acceptance of profile content-hash mismatches.
4. No dependency upgrades unless required to compile.
5. No mass reformatting / renames.
6. No deletion of `/tmp/tradinghub-calibration-training` giant historical chunks unless a separate explicit cleanup utility is requested by the user.
7. Do not claim edge improvement without held-out metrics — code quality ≠ alpha.

---

## 9. Implementation checklist (for Codex to fill)

### P0

- [x] P0.1 Experiment capture-market-replay defaults
- [x] P0.2 Replay compaction extended + tests
- [x] P0.3 Orphan job recovery
- [x] P0.4 Structural concurrency default
- [x] P0.5 Download/cache status accuracy (bug found and fixed)
- [x] P0 focused build + tests green

### P1

- [ ] Funnel model + aggregation
- [ ] Sidecar persistence
- [ ] API/experiment surface
- [ ] PlaybookId durability into calibration path
- [ ] Build + tests green

### P2

- [ ] Lifecycle hardening + tests
- [ ] Geometry bar + tests
- [ ] Arbitration detail + affinity boost
- [ ] Optional session/regime filters (default preserving)
- [ ] Example research profiles
- [ ] Build + tests green

### P3

- [ ] Analysis sharing verification/fixes
- [ ] Evidence hot-path
- [ ] Scoped annotator hot spots
- [ ] Throughput note in final report

### Final report

Write `TradingHub_Structural_Agent_Improvement_Final_Report.md` with:

- summary of behavior changes
- test commands + outcomes
- before/after c/s if measured
- remaining risks
- recommended next research matrix for the human

---

## 10. Prompt preamble (paste to Codex)

Use this as the first message if helpful:

> Implement the Structural Confluence agent improvement program described in `TradingHub_Structural_Agent_Improvement_Implementation_Prompt.md` in repo order P0 → P1 → P2 → P3.  
> Start with P0 only unless I say otherwise.  
> Keep changes minimal and tested. Do not add playbooks. Do not enable live automatic certification.  
> Prefer extending existing patterns (`CaptureMarketReplay`, `ChunkedReplayWriter`, experiment executor, calibration PlaybookId).  
> When P0 is done, report files changed, tests run, and residual risks before continuing.

---

## 11. Priority one-liner for the implementer

**Make Structural research fast and measurable first; only then tighten lifecycle, geometry, and arbitration. Do not invent new strategy lore until funnels and held-out playbook stats exist.**
