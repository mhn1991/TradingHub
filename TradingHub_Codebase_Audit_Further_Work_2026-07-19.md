# TradingHub Codebase Audit — Gaps, Performance, and UI — 2026-07-19

## How this was produced

A direct code-reading pass (not a generic checklist), building on the same-day performance
investigation that found and fixed the unbounded supply/demand-zone and liquidity-pool growth bug
(see §1.1). Cross-referenced against the repo's own prior audits (`TradingHub_Agent_Subsystem_Audit_Issue_Register_2026-07-17.md`,
`TradingHub_Independent_Audit_Report.md`, and this session's own memory of the PostgreSQL/live-deployment/
structural-confluence work) rather than re-deriving everything from scratch, and re-verified the
items pulled from those sources against current code before including them — several older notes
turned out to already be fixed or superseded and were left out.

**Baseline at time of writing:** `dotnet build TradingHub.slnx -c Release` — 0 warnings/errors.
`dotnet test Simulator.Tests` — 822/823 (1 pre-existing, unrelated failure: `LiveExecutionActivationGuardTests.HostRejectsBrokerWritesOutsideOandaPractice`
string-matches a stale namespace, tracked as `TEST-01` in the independent audit's issue register).
`TradingHub.UnitTests` — 60/60. Everything below is additive to a healthy tree; nothing here
implies the repo is broken.

---

## 1. Performance

### 1.1 [FIXED THIS SESSION] Unbounded zone/pool growth made long backtests roughly quadratic

`ChartAnnotator/SupplyDemand/SupplyDemandAnalyzer.cs` and `ChartAnnotator/Liquidity/LiquidityAnalyzer.cs`
tracked every zone/pool ever formed in a `Dictionary<Guid, ...>` for the lifetime of the analyzer.
Lifecycle pruning (`PruneExcessZones`/`PruneExcessPools`) only ever marked entries `Terminal = true`
— it never removed them. Every candle's update (`UpdateExistingZones`, `OverlapsExistingActiveZone`,
`MergeOverlappingZones`, the snapshot builder, plus several `.Any()` duplicate checks in the pool
detectors) enumerated the **entire** dictionary, terminal entries included. Over a long stream this
degrades to roughly O(total zones/pools ever formed) work per candle, i.e. quadratic total cost —
confirmed directly: a 100k-candle structural-confluence run with supply/demand and liquidity
enabled took over 20 minutes and was still climbing before the fix, versus 85 seconds for the same
candle count with a plain agent and no S&D/liquidity.

**Fix applied:** both analyzers now retain a permanent `HashSet<Guid>` of every ID ever formed
(cheap — just the ID, so the "never resurrect a trimmed zone under its old deterministic ID"
guarantee holds forever) plus a `Queue<Guid>` in creation order, and trim terminal entries from the
live dictionary once they fall outside the window the snapshot's bounded output (`MaximumActiveZones
+ MaximumRetainedEvents` / `MaximumActivePools + MaximumRetainedSweeps`) could ever need. Proven
behavior-preserving both by a correctness argument (creation order exactly matches `AvailableAt`
order, so anything trimmed provably can't make the "most recent N" cut) and by two new long-run
tests (`Update_LongRun_NeverResurrectsATrimmedZoneAndKeepsOutputBounded`,
`Update_LongRun_NeverResurrectsATrimmedPoolAndKeepsOutputBounded`) that run far past the retention
window and assert no zone/pool is ever recreated after going terminal. All existing analyzer tests
pass unchanged.

**Follow-up investigation (same day) — second bug found and fixed, not GC pressure.** The
structural-confluence-with-S&D/liquidity re-benchmark still took excessively long (57+ min CPU time),
so before assuming the fix was complete this was chased further with a scaling check
(`StructuralConfluenceWithSupplyDemandLiquidity_ScalingCheck`, 2k/4k/8k/16k candles): throughput still
degraded as candle count grew, with **zero trades opened at any scale** (ruling out
`StructureBasedTradeManager`/execution/ledger). Every other stateful collection in the path was
checked directly and confirmed properly bounded (`RingBuffer<T>.Add`, `SimulatedBrokerState`'s ledger,
`PlaybookStateStore`, `SupplyDemandLiquidityConfluenceAnalyzer`'s `_relationships`,
`PriceActionAnalyzer.Update`'s fresh-per-call lists). A first pass logged this as an unconfirmed
"working theory" of GC/allocation pressure — that theory was **wrong**. Re-measuring with
`GC.GetTotalAllocatedBytes`/`GC.CollectionCount` instrumentation on the same scaling check exposed the
real cause instead of confirming the guess: Gen2 collections exploded with candle count (1 → 3 → 103 →
271 across 2k/4k/8k/16k) and bytes/candle grew (221K → 402K → 768K → 977K) — proof of genuine
**remaining unbounded growth**, not flat-but-heavy allocation.

**Real root cause:** the fix above's own `Queue<Guid>` used for creation-order tracking
(`_creationOrder` in both `SupplyDemandAnalyzer.cs` and `LiquidityAnalyzer.cs`) only trims from the
front and gives up the instant it hits one still-active (non-terminal) zone/pool. Any single
zone/pool that stays active for a long stretch — a genuinely durable structural level, not a bug in
itself — permanently blocked cleanup of every terminal entry behind it in the queue, so the live
dictionary grew unbounded again anyway, just on a longer delay than the original bug in this section.

**Fix applied:** changed `_creationOrder` from `Queue<Guid>` to `LinkedList<Guid>` in both analyzers
and rewrote `TrimStaleTerminalZones`/`TrimStaleTerminalPools` to walk the list oldest-to-newest,
removing terminal entries **wherever they are** and skipping past active ones in place, rather than
stopping at the first active entry. The number of active entries skipped per call is bounded by
`MaximumActiveZones`/`MaximumActivePools` regardless of total run length, so the walk stays bounded.

**Proven, not theorized** — same 16,000-candle scaling-check measurement, before vs. after this fix:

| | Before (Queue bug) | After (LinkedList fix) |
|---|---|---|
| Time | 66.81s | 17.67s (**3.8x faster**) |
| Throughput | 239 c/s | 905 c/s |
| Gen2 collections | 271 | 3 |
| Bytes/candle | 977,489 | 390,966 |

Full 100k-candle head-to-head re-run after this fix (`Compare_StructuralConfluenceWithSupplyDemandLiquidity_VsPlainImproved`,
previously killed after 57+ min without finishing):

- Plain `improved` agent, no S&D/liquidity: 100,000 candles in 36.26s = 2,758 c/s
- Structural-confluence with S&D/liquidity: 100,000 candles in **142.58s = 701 c/s**
- Completes cleanly in ~3 minutes now (vs. never finishing before) — the runaway/quadratic behavior
  is gone.

**Remaining, honestly reported gap:** structural-confluence with S&D/liquidity is still ~3.9x slower
per-candle than the plain agent (293% overhead) even after this fix. This is very likely the genuine
cost of running two additional stateful analyzers (supply/demand + liquidity) plus the confluence
layer over every candle, not a further bug — no unbounded growth remains (Gen2 collections stayed at
1/1/1/3 across 2k/4k/8k/16k after the fix, essentially flat). This was not chased further since the
pathological (never-finishing) behavior is resolved; shrinking the remaining ~4x constant-factor gap
would require profiling the S&D/liquidity analyzers' per-candle work directly (e.g. `dotnet-trace`)
and was not attempted this session. Two long-run regression tests
(`Update_LongRun_NeverResurrectsATrimmedZoneAndKeepsOutputBounded` /
`...TrimmedPoolAndKeepsOutputBounded`) guard the append-only lifecycle invariant this fix depends on.
Full suite: 822/823 passing (same single pre-existing unrelated `MultiInstrumentPortfolioClockTests`
failure), zero new regressions.

### 1.2 Interactive single runs default to building chart replay chunks — now a real toggle, but still on by default

Confirmed by direct benchmark: `CaptureMarketReplay = true` costs roughly 33% of throughput
(1,951 c/s vs. 2,901 c/s on a 100k-candle synthetic run) because every candle's analysis snapshot
gets cloned/trimmed and buffered for the replay chart. This is now exposed as a real toggle in both
the Dashboard (`SimulatorPanel.vue`, "Capture chart replay" checkbox) and CLI
(`--no-capture-market-replay`), defaulting to the pre-existing `true` behavior. **Remaining work:**
no code change needed, but consider defaulting it to `false` for the Dashboard's own "quick
performance check" workflows, or surfacing an estimated-time delta in the UI when the checkbox is
toggled, since users have no way to know the cost without reading this report.

### 1.3 Auto-calibration-before-run silently 2-3x's the cost of a default single run

`CreateSimulationRequest.AutoCalibrateBeforeRun` defaults to `true` (`DashboardLive/SimulationApi.cs`),
and `SimulatorPanel.vue` sends it explicitly as `true` on every submission. When active (the default,
since no manual calibration artifact IDs are set by default either), `PreRunCalibrationService`
trains setup → meta → management calibration on a separate historical window (2 months by default)
**before** the requested simulation runs at all — and the underlying `CalibrationTrainingPipeline`
itself does a real backtest for the setup stage and a second full backtest for the management stage
(re-running under the frozen entry policy to see the actual post-calibration trade population — this
second pass is methodologically required for leakage safety, not removable). For each strategy
chain requested. This is a completely legitimate feature working as designed, but it means the
"candles/second" a user perceives for a default single run includes 2 extra full backtests over a
different date range that never show up in the visible run's own progress bar. **Action:** none
required (the checkbox to disable it already exists), but consider showing an estimated added time
next to the checkbox, and confirm whether `RunManagementStage` (skippable independently per
`CalibrationTrainingRequest`) is ever exposed as a finer-grained option for users who only want setup
calibration.

### 1.4 Experiment panel's own default silently overrides the safer backend default

`SimulationExperimentPanel.vue`'s draft defaults `parallelism.maxProfileGroups = 2`, while
`Simulator/Experiments/Models/SimulationExperimentRequest.cs`'s own field default is `1` (reduced
from 2 in a prior session specifically as a memory-safety fix, per the P0 completion report). Since
the Dashboard always sends its own explicit value, the safer backend default is inert for every
Dashboard-initiated experiment — only bare API/CLI callers that omit the field get the conservative
default. This is the same "Dashboard always sends its own explicit value, so server-side default
changes have zero effect on the UI" pattern that has recurred several times in this codebase's
history (see the `regimeEnabled`/`valueLocationEvidenceEnabled`/`adaptiveRiskEnabled` note in prior
session memory). **Action:** decide deliberately whether the Dashboard's default should also be 1,
rather than have two different "defaults" that silently diverge.

### 1.5 `DetectEqualLevels`/`DetectIsolatedSwings` re-sort the full swing buffer every candle

`ChartAnnotator/Liquidity/LiquidityAnalyzer.cs:265-336` (`DetectEqualLevels`) does, for every one of
the (bounded but potentially large, `SwingCapacity`-sized) retained swings, a `.Where(...).OrderBy(...)`
over the whole swing list per swing type, and an inner loop re-filtering/re-sorting a `.Take(N)`
subset for every candidate index. This is bounded (not unbounded like §1.1), but it's still
`O(SwingCapacity²)`-ish work every single candle regardless of whether any new equal-level pool
actually needs detecting. Not measured directly in this pass; flagged as a secondary candidate if
§1.1's fix doesn't fully close the gap to plain-agent throughput.

### 1.6 [FIXED THIS SESSION - CRITICAL] Every `TradingPolicyProfile`/calibration payload stored via
Postgres could never be read back

Found while building and end-to-end testing the "Promote to live policy" Dashboard feature (§3.5
below): promoting a real completed simulation succeeded and returned a profile, but the very next
`GET /api/trading-policy-profiles` silently dropped it (after an initial defensive fix that turned a
500 into a silent skip - see below). Root cause, confirmed empirically rather than assumed
(`git log`/manual reflection-based diffing, not guesswork):

- `TradingPolicyProfile.Validate()` recomputes a `ConfigurationHash` from the object's content on
  *every* deserialize and throws if it doesn't match the hash stored at write time - this is meant
  to catch tampering/corruption, and works fine for the file-based store.
- PostgreSQL's `jsonb` column type does **not** preserve JSON object key order or exact number
  formatting (documented Postgres behavior, not a bug in Postgres) - confirmed directly: a document's
  retrieved key order (`status, revision, agentKind, createdAt, profileId, strategyId...`) bore no
  resemblance to the C# record's declared property order (`SchemaVersion, ProfileId, Revision,
  StrategyId...`).
- So every profile's stored `ConfigurationHash` (computed once, pre-Postgres, at write time) mismatched
  the hash recomputed from the jsonb-reordered document on every subsequent read - permanently, for
  every profile ever stored this way, not just the one built by this session's new feature.
- Same bug, same root cause, independently confirmed in `PostgresCalibrationArtifactRepository.
  GetPayloadAsync`'s `ContentHash` check on the `payload` column - meaning **any real simulation using
  a calibration artifact stored via Postgres (`GetSetupAsync`/`GetManagementAsync`/`GetMetaModelAsync`)
  could already have been silently failing**, and `calibration_bundle_candidates.proposed_profile`
  (the calibration-candidate *approval* workflow's own `TradingPolicyProfile`) is subject to the exact
  same failure - meaning an approved policy could be unreadable by the live/shadow runtime meant to
  use it. This was found only because a real end-to-end round trip was actually exercised, not
  because it was suspected in advance.

**Fix applied:** changed `policy_document` (`config.policy_revisions`), `payload`
(`config.calibration_artifacts`), and `proposed_profile` (`config.calibration_bundle_candidates`) from
`jsonb` to `text` (EF Core migration `Phase13PolicyDocumentAsText`), preserving exact write-time bytes
- matching how the file-based stores already work. Verified live: deleted the one profile that had
been silently swallowed under the old schema, re-ran the exact same promote action against the same
completed simulation, and confirmed `GET /api/trading-policy-profiles` now returns it fully
deserialized with no error and no skip-warning.

**Also hardened (defense in depth, not a substitute for the real fix above):**
`PostgresTradingPolicyProfileStore.ListAsync` and `PostgresCalibrationArtifactRepository.ListAsync`
now skip individually-unparseable rows (logging a warning) instead of 500ing the entire list - this
is what first surfaced the bug (turned a 500 into a silent empty result, which is what led to
investigating further rather than assuming success). Also found and cleaned up: 19 rows of leftover
`smoke-test`-tagged fixture data across 7 tables (`policy_revisions`, `policy_profiles`, `deployments`,
`positions`, `position_management_state`, `policy_artifacts`, `calibration_artifacts`) from an earlier
manual verification pass on 2026-07-18, all clearly synthetic (`started_by='smoke-test'`,
`host_instance_id='host-1'`, strategy `'TrendFollow'` used nowhere else in the codebase) - deleted
after confirming the full dependency graph with the user.

---

## 2. Functional gaps (features that exist but aren't fully connected, or are explicitly incomplete)

### 2.1 [From this session's own work] `TargetPlanRevision` is defined and persisted but nothing ever produces one

The Structural Indicator and Adaptive Target Management plan's target-acceptance/advancement logic
(plan §4.5 — marking a consumed terminal candidate and promoting to the next one) and strong-terminal-
rejection full-close logic (plan §4.6) were explicitly deferred this session. The contract
(`ChartAnnotator.TargetManagement.TargetPlanRevision`) exists and is wired all the way through
`SimulatedTradeRecord.TargetPlanRevisions`, but no code path ever populates it — it will always
serialize as an empty array. This is the single biggest remaining gap in that feature: today, a v2
managed trade partials once at its checkpoint and then relies entirely on trailing-stop/profit-floor/
giveback to manage the runner, never advancing to a farther target or hard-rejecting on the original
terminal. **Files:** `TradeManager/StructureBasedTradeManager.cs` (`FindPositionReduction`/`Evaluate`),
`Agent/Strategies/StructuralConfluence/TargetManagement/TargetMapBuilder.cs`. **Blocker:**
`StructureBasedTradeManager.Evaluate` only receives a single-timeframe `AnalysisSnapshot`, not a
multi-timeframe evidence packet, so a live target-map rebuild isn't mechanically possible without an
interface change — the memory tracker for this work (`tradinghub_structural_target_management_progress.md`)
has the detailed design notes.

### 2.2 No Dashboard visibility at all for the new adaptive target-management fields

`ExitPolicy`, `TargetPlan`, `PlannedR`, `RealizedR`, and `InitialRiskCash` were added to
`SimulatedTradeRecord` this session and are already exposed end-to-end through the existing
generic (no-DTO-allowlist) trade-exposure paths (`DashboardLive/SimulationHub.cs`'s realtime push,
`GET /api/simulations/{id}/trades`'s raw JSON passthrough) — but `Dashboard/src/types.ts` and every
Vue component are unaware of them. A user running a structural-confluence-v2 profile today has no
UI way to see whether a trade was Fixed/Partial/Managed, what the target plan looked like, or planned
vs. realized R. **Action:** add the fields to `types.ts`, and surface them somewhere in
`SimulatorPanel.vue`'s trade table or a new column — probably worth doing together with §2.1 once
target-plan revisions actually exist to show.

### 2.3 AGENT-06 — Setup calibration and meta-model may be redundant (open, needs empirical data, not code)

`RiskManager/Calibration/SetupCalibration.cs:106-131` and `Calibration/CalibratedSetupMetaModel.cs:30-87`
use an identical reject/reduce/pass-through decision tree with the same thresholds, differing only
in their conditioning axis (`InstrumentGroup` vs. `MultiTimeframeAlignment`). If those axes don't
actually produce materially different bucket populations for the same `(StrategyId, Regime,
Confidence)` slice, a single piece of evidence gets multiplicatively double-counted as two
independent risk reductions, needlessly starving trade frequency. Deferred at the user's explicit
direction pending an offline empirical check (do the bucket populations actually diverge on real
historical data?) — this is a data question, not a code question, and should be run before any
merge/simplification decision. See `TradingHub_Agent_Subsystem_Audit_Issue_Register_2026-07-17.md`'s
AGENT-06 entry for the full recommended check.

### 2.4 AGENT-13 — `RuleBasedMultiTimeframeAgent` is dead production code

`Agent/Strategies/RuleBasedMultiTimeframeAgent.cs` is a complete third Agent implementation with its
own entry/confidence logic, referenced only by itself and one test file — never reachable via
`ProgressiveAgentFactory` (which only has `Legacy`/`Improved` cases) or any live/production
construction path. Low risk (maintenance confusion only), but should be either wired in as a genuine
third option or deleted along with its dedicated test.

### 2.5 AGENT-14 — Live policy hot-swap may not reach Agent construction for new entry decisions (open question)

`LiveTrading/Configuration/LivePolicyRegistry.cs`'s `Activate` correctly keeps already-open positions
on their original policy revision (current/history split), but no code path was found in
`AgentSupervisor.cs` or `LiveEngineHostedService.cs` that re-registers/reconstructs an
`AgentInstanceState` after a policy activation. If confirmed, an approved and activated policy
revision's entry-decision changes would never take effect for new candidates without a full host
restart — directly relevant to the automated calibration-retraining pipeline, since "activation"
would then be a partial operation. Needs an end-to-end trace, not yet promoted from open question to
confirmed defect.

### 2.6 Position-level broker merge remains deliberately deferred

`StrategySimulationSession.cs:221` gives every strategy its own fresh `SimulatedBrokerClient`; there
is no `VirtualLot`/position-share concept anywhere in the repo. Two strategies trading the same
instrument in the same simulated account can't share a broker position at the sub-position level
(margin nets correctly at the account level; individual position management does not merge). This
was investigated twice and deliberately deferred both times as out of scope (would need a
virtual-lot rewrite of `StrategySimulationSession`) — listed here only so it isn't rediscovered as if
new.

### 2.7 Multi-instrument replay chart doesn't filter trade markers by instrument

Confirmed still true: `Dashboard/src/composables/useSimulationPlayback.ts` has no instrument
filtering at all. The candle series itself is correctly filtered per-instrument (via a selector shown
when a run trades more than one instrument), but trade markers are not, because `ReplayTrade` doesn't
expose an `Instrument` field in the API today. Needs a new field on the replay-trade DTO plus
verifying `InstrumentKey`'s exact JSON shape before wiring the frontend filter.

### 2.8 §21-23 of the original quantitative-enhancements audit remain not started

Instrument specs, execution-realism metadata, and live-certification work were never begun — live
certification is explicitly blocked pending real broker access, which this environment doesn't have.
Not urgent, but the oldest unaddressed item in the codebase's own audit trail.

### 2.9 No basket editor UI for currency-strength analysis

Enabling currency strength from the Dashboard defaults to a single-instrument basket (the traded pair
itself, which leave-one-out then correctly excludes from its own differential rather than being
silently self-referential — so it's safe, just not useful). Real multi-pair baskets require calling
the API/CLI directly; there's no UI to build one.

---

## 3. Dashboard/UI panel improvements

### 3.1 `SimulatorPanel.vue` is a 3,140-line monolith

Every other Dashboard feature area (experiments, live deployment) has already been split into a
`components/<area>/*.vue` sub-structure (`simulator/experiment/*.vue`, `live/*.vue`), each a focused
100-150 line component. `SimulatorPanel.vue` alone is 20x the size of the next-largest component and
covers request building, strategy/position-management config, replay/cache toggles, calibration
config, and results tables all in one file. This is the same restructuring the experiment panel
already went through — worth doing the same split here (request-builder, position-management-config,
calibration-config, results-table as separate components) both for maintainability and because it
would make gaps like §2.2 much easier to close (a results-table component with a clear props contract
is a much smaller unit to extend than finding the right spot in a 3,140-line file).

### 3.2 No indication of estimated run cost/time before submitting

Given §1.2/§1.3, a user has no way to estimate how long a run will take before clicking "Run
Simulation" — the replay toggle, auto-calibrate toggle, and profile-group count are all real,
substantial throughput multipliers with no visible cost signal anywhere in the form. Even a rough
static hint ("chart replay adds ~50% to run time", "auto-calibration adds 1-2 extra full backtests")
next to each control would close a real information gap surfaced directly by this session's own
investigation.

### 3.3 Reporting panel is thin relative to what's already computed

`ReportingPanel.vue` is 186 lines against a backend that already computes MFE/MAE, partial-exit
detail, stop-amendment history, and (per this session) exit-policy/target-plan/planned-vs-realized-R
data. Given `TradingHub.ReportRunner` can already export a full structured Markdown/JSON report for a
completed simulation (confirmed working — a real 84,686-candle report was generated successfully in
an earlier session), the gap here is specifically in the *interactive* Dashboard view, not the
underlying data or the batch-export path.

### 3.4 Known replay/proxy risk never exercised live

The gzip-streamed replay-chunk responses (`Content-Encoding: gzip`, `Vary` headers) added in
yesterday's throughput fix were build-verified but never exercised through a live dev server or
behind a reverse proxy after restart, per that fix's own handoff notes. Worth a manual pass before
relying on it in a proxied deployment.

### 3.5 [FIXED THIS SESSION] "Promote to live policy" and calibration-candidate review had zero
Dashboard UI, despite fully-implemented backends

Directly prompted by "I should be able to do all the task from Dashboard which I can see there are
couple of tasks that is not possible" - a systematic pass cross-referencing every backend endpoint in
`DashboardLive` against every Dashboard frontend call found real, high-impact gaps (not cosmetic ones):

1. **`POST /api/simulations/{id}/policy-profiles/{strategy}`** (promote a completed backtest into a
   live trading policy) had no UI, and - independently, found only by exercising it - the endpoint
   itself never persisted the profile it built (no `store.StoreAsync` call). Both fixed: the endpoint
   now saves, and `SimulatorPanel.vue` gained a "Promote to live policy" panel on completed runs
   (strategy/revision/description fields, "approve for demo" checkbox).
2. **`GET /api/trading-policy-profiles`** (every persisted policy, across both promotion paths) had no
   UI. Added a read-only table to `ResearchPanel.vue`.
3. **`GET/POST /api/calibration-candidates/{id}/approve|reject`** (the calibration pipeline's human
   review queue) had no UI at all - approving/rejecting a pending candidate was API-only. Added a full
   review table to `ResearchPanel.vue` with approve/reject actions.
4. **Simulation-profile revision history/diff** (`GET .../revisions/{revision}`, `GET
   .../revisions/{leftRevision}/diff`) - you could create/clone revisions but never see what changed
   between them. Added a "History" toggle + diff viewer to `StrategyProfileTable.vue`.

Exercising all four for real (not just building them) is what surfaced §1.6's critical persistence
bug - the promote button appeared to work before that fix, but the profile it wrote could never be
read back again.

**Deliberately not built this pass** (checked and confirmed NOT gaps - already covered another way):
`/api/simulations/{id}/performance` and `/strategies` are subsets of the main snapshot the Dashboard
already fetches; `/api/simulation-experiments/{id}/results` and `/manifest` are already embedded in
the experiment snapshot and shown via `ExperimentComparison.vue`; `/api/calibrations/setup|management|
metamodel` (raw artifact upload) is intentionally programmatic, not a form-fill target; health-check
endpoints are ops-only by design.

---

## 4. Suggested priority order

1. **§1.1 verification** — [DONE, fully resolved] the remaining slowdown after the first fix was a
   second real bug (a `Queue<Guid>`-based trim that gave up the instant it hit one active zone/pool),
   not GC pressure as first theorized - found via direct GC/allocation measurement rather than assumed.
   Fixed and proven: 16k-candle scaling check went from 66.81s (Gen2=271) to 17.67s (Gen2=3), and the
   full 100k-candle head-to-head now completes in ~3 min (previously killed after 57+ min without
   finishing). See §1.1 for full numbers. No further action needed here.
2. **§2.1 + §2.2 together** — target-plan advancement/rejection logic and its Dashboard visibility are
   the same feature's two halves; doing them together avoids building UI for data that still doesn't
   exist. **§2.2 (Dashboard field types/exposure) implemented** — `Dashboard/src/types.ts` gained
   `TradeTargetPlan`/`TradeTargetCandidate`/`TargetPlanRevision` interfaces and the new `ReplayTrade`
   fields; `SimulatorPanel.vue`'s trade list now shows exit policy/planned R/realized R when present.
   **§2.1 (target-plan advancement/rejection logic) is still not implemented** — it's the larger half,
   blocked on the `StructureBasedTradeManager.Evaluate` single-timeframe-snapshot interface question
   noted in its entry above, and wasn't attempted in this pass.
3. **§1.4** — [DONE] `SimulationExperimentPanel.vue`'s draft now defaults `maxProfileGroups` to `1`,
   matching the backend's own memory-safety default, with a comment explaining why.
4. **§3.2** — [DONE] added a concrete cost-estimate hint next to the auto-calibrate checkbox
   (`SimulatorPanel.vue`) alongside the replay-cost hint added earlier the same day.
5. **§2.4** — asked the user directly; decision was to leave `RuleBasedMultiTimeframeAgent` as-is for
   now, not wire in or delete.
6. **§2.5, §2.3** — not started. §2.5 needs an end-to-end trace before it's even confirmed as a
   defect; §2.3 needs offline empirical data analysis before any code change is justified at all -
   neither is a code patch in its current state.
7. **§3.1** — not started; restructuring work, valuable but not urgent.
8. **§1.6 + §3.5** — [DONE] built the four missing Dashboard→backend paths (promote-to-live-policy,
   live policy profile list, calibration-candidate review, profile revision diff), which in the
   process surfaced and fixed a critical, previously-latent bug: every `TradingPolicyProfile`/
   calibration payload stored via Postgres `jsonb` failed its own hash validation on every read,
   permanently. Fixed via an EF migration (`policy_document`/`payload`/`proposed_profile` →
   `text`), verified live end-to-end (delete → re-promote → re-read, clean). This was higher-impact
   than the UI work that surfaced it - see §1.6 for the full writeup.
