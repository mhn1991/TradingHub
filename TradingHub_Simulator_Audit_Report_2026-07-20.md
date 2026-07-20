# Simulator System Full Audit — 2026-07-20

**Status: all 4 findings below (§2) fixed same-day.** See §6 for what changed and how it was verified.

Full-rigor audit of the `Simulator/` project (18,216 lines across 12 subdirectories), matching the standard set by the same-day structural-agent audit (`TradingHub_Structural_Agent_Audit_Report_2026-07-20.md`). Read in full, file by file — no subsystem skipped. Working notes: `simulator_audit_findings.md` (scratchpad, session-local).

Requested mid-audit that this pass pay particular attention to whether data reaching the agent is valid — the findings below are organized with that lens.

## 1. Scope covered

| Area | Lines | Result |
|---|---|---|
| `Engine/StreamingComparativeEngine.cs` | 1,070 | 1 low finding |
| `Engine/StrategySimulationSession.cs` | 2,531 | clean |
| `Engine/SharedPortfolioRuntime.cs` | 960 | 1 low finding |
| `Broker/*` (fill/P&L engine) | 2,320 | clean |
| `Engine/SimulationRunner.cs` + `StrategyWorkerHost.cs` + `SimulationFactory.cs` | 918 | scope note (legacy path, not live) |
| `Services/BacktestApplicationService.cs` | 1,041 | clean |
| `MarketData/*` | 2,358 | clean |
| `Replay/ChunkedReplayWriter.cs` | 980 | clean |
| `Experiments/*` (walk-forward/calibration orchestration) | 2,597 | 1 moderate finding |
| `Models/*` | 2,046 | **1 high finding** |
| `Jobs/* + Execution/* + Financing/* + Time/* + Abstractions/*` | 1,046 | clean |
| **Total** | **~18,216** | |

## 2. Findings, by severity

### High
1. **`TradingPolicyPromotion` doesn't reconcile `SetupCalibration.Enabled`/`ManagementCalibration.Enabled` against whether an artifact was actually attached to the promotion — only `MetaModel` gets this treatment.**
   `Simulator/Models/TradingPolicyPromotion.cs` patches `MetaModel.Enabled = metaModelArtifactId.HasValue` before building a live `TradingPolicyProfile`, with a comment (tagged "AGENT-02") explaining why: the auto-train pipeline can reuse an unrelated source backtest's `Runtime`, whose `MetaModel.Enabled` reflects that other run, not this promotion. `SetupCalibration` and `ManagementCalibration` are passed straight through unreconciled. `TradingPolicyProfile.Validate()` (`TradingPolicies/TradingPolicyProfile.cs:150-157`) hard-fails on a Meta-Model Enabled/ArtifactId mismatch but has no equivalent check for the other two.
   **Failure scenario**: a promotion attaches a `setupCalibrationArtifactId` while the source runtime's `SetupCalibration.Enabled` is `false` → the resulting live policy stores the artifact ID but the feature reads as disabled everywhere downstream that gates on `.Enabled` (which is the pattern used throughout this codebase). Same silent-inert-feature bug class as the meta-labeling dead-toggle bug this session already found and fixed in `BacktestApplicationService.cs`. Not fixed as part of this pass — flagging for the same treatment MetaModel got.

### Moderate
2. **`AnalysisWarmupPlanner`'s reflection-based lookback discovery can silently under-count a strategy's true warmup requirement.**
   `Simulator/Experiments/AnalysisWarmupPlanner.cs` walks every property of a strategy's analysis/management config via reflection, treating an `int` property as a lookback requirement only if its name contains one of 6 hardcoded tokens (`Period`, `Lookback`, `Capacity`, `Window`, `Bars`, `Persistence`, `Confirmation`). A future config field representing a real lookback need but named outside this list (e.g. `MinimumHistoryCandles`) would never be discovered. The downstream `HasSufficientData`/`PromotionEligible` gate only checks "is there enough data for the *computed* requirement," not whether the computation itself is complete — so a strategy could pass promotion gating while genuinely starting evaluation with immature indicator state for the missed field. Structural fragility, not a confirmed live miscalibration (would need a field-by-field audit of every profile-reachable options class to confirm a live gap).

### Low
3. `StreamingComparativeEngine`'s `StopFailedStrategyOnly` exception handler misattributes the "culprit" session to an arbitrary first-active-session if the exception originates outside a per-session catch block (e.g. `sharedPortfolio.FlushAsync` itself throwing) — a misleading error message in a rare path, not data corruption.
4. `SharedPortfolioRuntime`'s per-instrument correlation snapshot (`existingDirections`) is computed once per frame-flush and not updated as opportunities within the same batch are allocated — currently dormant since the simulator only streams one instrument per run, but would need addressing before multi-instrument portfolio streaming lands (already flagged as future work by two separate comments in that file).

### Informational / scope notes
5. `Simulator/Engine/SimulationRunner.cs` (+ `SimulationFactory.cs`, `ComparativeBacktestRunner.cs`) is a materially simpler sibling of `StrategySimulationSession` — no trade-manager wiring, no partial-exit handling. Traced its call graph: reachable only from `Simulator.AotSmoke` and unit tests, never from the live `BacktestApplicationService → StreamingComparativeEngine` path the dashboard actually drives. No live impact; noted for the record since it's compiled, test-covered code with a real behavioral gap relative to its sibling.
6. The fresh-calibration *fitting* engine (`QuantResearch.Training/Pipeline/CalibrationAwareSimulationExperimentExecutor.cs`) and the underlying calibration/meta-model math (`/Calibration/*`, namespace `Simulator.Calibration`) live in separate top-level projects, not under `Simulator/`. Not covered by this pass — disclosing rather than silently skipping. The meta-model bucket-width/cohort-ordering issues in that area were already found and fixed earlier this session during the structural-agent work.

## 3. Confirmed clean (verified in detail, not just skimmed)

- **No-lookahead discipline**, checked at every layer: `StreamingComparativeEngine`'s k-way merge and per-instrument batch flushing; `StrategySimulationSession`'s snapshot-availability gating (`AvailableAt <= frame.AvailableAt` everywhere); `SimulatedBrokerState`'s `SubmittedMarketSequence < MarketSequence` fill-eligibility rule (an order — including a just-attached stop/target — can never fill on the candle that created it); stop-amendment `EffectiveFromExecutionSequence` always one frame ahead; `HistoricalSimulationClock.AdvanceTo` hard-rejecting time moving backwards.
- **P&L/commission bookkeeping**: weighted-average entry price, realized-P&L-on-close, partial-exit fractional commission allocation, and margin-additional-exposure-only charging all traced and verified consistent — no double-counting found anywhere.
- **Walk-forward isolation**: `SimulationExperimentTimeline` enforces a mandatory embargo gap (not just non-overlap) between learning and evaluation windows; frozen calibration artifacts are content-hash-pinned and re-verified before every evaluation run; fresh (unfrozen) calibration is explicitly refused by the base executor rather than silently evaluated.
- **Concurrency patterns**: every bounded channel / semaphore / pause-gate in the project (`StrategyWorkerHost`, `BacktestApplicationService`'s per-job actor, `SimulationExperimentApplicationService`'s per-experiment actor, `BoundedAsyncEventLog`, `PrefetchingCandleStream`, `AsyncPauseGate`, `SimulationResourceGovernor`) uses the same correct signal-captured-under-lock-then-released-outside-lock pattern, with no missed-wakeup or deadlock risk found in any of them.
- **File persistence**: every file-backed store in the project (candle cache, replay chunks, job repository, experiment repository) uses atomic write-to-temp-then-move, and every one that supports revisioned updates guards against writing an older revision over a newer one.
- **Request validation**: `BacktestRequest.Validate()`/`BacktestRuntimeOptions.Validate()` are thorough, cross-check derived analysis intervals for duration/divisibility against the analysis base, and include two previously-audit-found hard-blocks still in force today (`EconomicEventFilterEnabled` requires a wired provider that doesn't exist yet; at most one `Executable` strategy assignment per instrument).

## 4. What this pass did not do

- Did not extend the audit into `Calibration/`, `QuantResearch.Training/`, or `TradingPolicies/` beyond the one file (`TradingPolicyProfile.cs`) touched to fix finding #1 — those remain a separate top-level project, disclosed above rather than silently skipped.

## 5. Suggested next steps (for discussion, not decided)

1. If you want the same rigor applied to `Calibration/`, `QuantResearch.Training/`, or `TradingPolicies/` beyond the one fixed file, that's a clearly separate follow-up scope.
2. Task #10 (recurring ContentHash breakage on profile-revision schema changes, tracked since earlier in the session) is unrelated to this audit but still open.

## 6. Fixes applied (2026-07-20, same day as the audit)

All 4 findings from §2 were fixed and verified:

1. **High (calibration reconciliation)**: `Simulator/Models/TradingPolicyPromotion.cs` now reconciles `SetupCalibration.Enabled = setupCalibrationArtifactId.HasValue` and `ManagementCalibration.Enabled = managementCalibrationArtifactId.HasValue` at promotion time, mirroring the existing MetaModel pattern. `TradingPolicies/TradingPolicyProfile.cs`'s `Validate()` now hard-fails if either mismatches its artifact id, the same AGENT-02-style guard that already existed for MetaModel.
2. **Moderate (warmup lookback discovery)**: `Simulator/Experiments/AnalysisWarmupPlanner.cs`'s `IsBarRequirement` token list widened from 7 to 12 tokens (`Confirmed`, `Samples`, `Candles`, `Touch`, `History` added), grounded in a survey of real `*MinimumSamples`/`*MinimumBaseCandles`/`MinimumConfirmedMonoWaves`-shaped config fields across `ChartAnnotator`/`Agent`/`RiskManager`/`TradeManager` that the original list missed. This can only increase (never decrease) a computed warmup requirement, so it's a safe, backward-compatible hardening.
3. **Low (culprit misattribution)**: `Simulator/Engine/StreamingComparativeEngine.cs`'s `StopFailedStrategyOnly` handler no longer blames an arbitrary "first active session" when no session is actually marked `IsFailed` (i.e. the exception didn't originate from a per-session catch) - it now rethrows instead of guessing, since isolating a failure to one strategy requires knowing which one actually failed.
4. **Low (intra-batch correlation)**: `Simulator/Engine/SharedPortfolioRuntime.cs` now folds each opportunity's own direction into the working `existingDirections` map as it's processed, so a second correlated-instrument opportunity queued later in the *same* frame batch is evaluated against it - previously every opportunity in a batch was scored as if it were the only new position.

**Verification**: full solution build (`dotnet build TradingHub.slnx`) - 0 warnings, 0 errors. `Simulator.Tests` (832 tests) and `QuantResearchRunner.Tests` (73 tests, exercises `TradingPolicyProfile`/`CalibrationBundleWorkflowTests`) both run before and after the fixes via a temporary revert-and-restore of just the 5 changed files (not a stash - the repo carries other large uncommitted WIP that must never be touched). Failure counts were identical before and after (2 and 2 respectively) and confirmed to be pre-existing, unrelated failures (`InstrumentKeyJsonConverter` JSON deserialization, and two `QuantResearchRunner.Tests` failures unrelated to calibration-artifact reconciliation) - none of the 4 fixes introduced a new failure or fixed/masked an existing one.
