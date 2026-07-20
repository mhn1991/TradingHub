# Structural Agent Audit + Session Report — 2026-07-20

Status snapshot as of this document. Covers the full working session: infrastructure work, observability tooling, the confidence/meta-model investigation, the full structural-agent audit, and the fixes applied from it.

## 1. Infrastructure (done, verified)

- **Disk migration**: project + Postgres data directory moved from the nearly-full root disk (was 14G free / 94% used) to the new 3.6TB `/mnt/storage` disk (reformatted to ext4 by the user). Project is now a symlink (`/home/mhn70/RiderProjects/TradingHub` → `/mnt/storage/TradingHub`), verified identical (file count, size, git HEAD, clean `git status`). Postgres bind-mounted to the new location, verified via a real query returning real historical data. Root disk now at 69G+ free.
- **DashboardLive concurrency**: `MaxConcurrentJobs` was hardcoded to 3 despite the box having 20 cores (measured ~12% total CPU usage at "full" capacity). Raised to 12. Deployed and confirmed working — multiple simulations now run genuinely in parallel instead of queuing.
- **DashboardLive auto-calibration UX**: `POST /api/simulations` used to block the HTTP connection for the entire training duration (10-30+ min) with no job id to poll. Fixed with `PendingAutoCalibrationTracker` — returns a pollable placeholder immediately, resolves to the real job once training completes. Deployed, verified (sub-100ms response times instead of multi-minute blocks).

## 2. Observability tooling (done, verified)

- `ChunkedReplayWriter` now emits, per strategy per run: `funnel-summary.json` (event-type × reason-code funnel + signal-to-trade conversion), `signals.ndjson` (per-SetupId terminal disposition), `events.ndjson.gz` (flat consolidated event stream). All computed incrementally, no re-scanning needed for future investigations.
- `tools/analyze_simulation.py` — CLI wrapper (`funnel`/`signals`/`events`/`timeclusters` subcommands) replacing the ad hoc Python scripts written by hand for every investigation this session.
- Found and fixed a real bug in the above: `StreamWriter(..., Encoding.UTF8)` emits a UTF-8 BOM by default, breaking strict JSON parsers. Fixed with a shared no-BOM encoding.

## 3. Confidence / meta-model findings (done)

- `entryConfidence` (raw heuristic score) has ~zero correlation with realized R (Pearson r = -0.029) — established fact, unchanged by anything below.
- The existing calibration/meta-model layer (`CalibratedSetupMetaModel`, deterministic bucket lookup, not ML) was **inert in every test this session** (`enabled: false` everywhere) — confirmed via `manifest.json`.
- Root-caused why it would be weak even if enabled: `MetaModelCalibrator` bucketed cohorts by raw confidence *first*, before the dimensions that actually show signal (alignment, playbook). **Fixed**: `CalibrationTrainingRequest.MetaModelConfidenceBucketWidth` default changed 5→100 (one wide bucket = confidence stops fragmenting cohorts).
- **Fixed**: `EnableConfidenceScaledStagnation` (scales trade-management patience off the same noisy confidence signal) turned off on all 5 tested profiles.
- **In progress, not yet concluded**: a Legacy-as-test-bench run with real auto-calibration enabled (3 CV folds, reduced from 5 for speed) is still training as of this document (`e0c0f7bf-68d4-457c-8875-a73c8184b630`, "Queued/Training"). Goal: does calibration, once fixed and actually enabled, measurably help Legacy's 142-trade sample. **No result yet.**

## 4. Structural agent full audit (done)

Reviewed all 4 playbooks, the agent/arbitrator/evidence-factory/geometry-builder/identity/target-map-builder/options, the 5 upstream ChartAnnotator analyzers that feed it evidence, and the trade-management consumer. Full findings notes: `structural_audit_findings.md` (scratchpad, session-local). Summary by severity:

### High severity — both fixed today
1. **3 of 4 playbooks had no re-entry guard** (only sweep-reversal had been fixed before the audit, from a directly-observed bug: same setupId, two entries 25 min apart, second one stopped out in 1 minute). SD-pullback, break-retest, and indicator-confluence all had the identical gap. **Fixed** for all three today (indicator-confluence needed a different mechanism — a gap-detection heuristic instead of an identity check, since it has no discrete catalyst event to key on).
2. **`SupplyDemandZone.QualityScore` was computed once at formation and never recomputed.** Real `TouchPenalty`/`PenetrationPenalty`/`AgePenalty` weights existed but were dead — every consumer (entry gates, target-map tiering, *and* `StructureBasedTradeManager`'s stop-trailing candidate scoring) always saw a zone's day-one quality no matter how stale it became. Confirmed as a real gap by comparing against `LiquidityPool.QualityScore`, which does recompute correctly in the sibling analyzer. **Fixed** — now recomputes live every update cycle, mirroring the liquidity-pool pattern.

### Medium severity — 3 of 5 fixed today, 2 open
3. Two playbooks (SD-pullback, break-retest) classified lifecycle via `gates.Take(N)` — a positional index into the gate list, silently fragile to reordering. **Fixed** — converted to name-based exclusion (sweep-reversal's existing pattern).
4. `LiquidityBreakRetestOptions.MinimumAdx` was missing its upper-bound validation (unlike the equivalent field elsewhere). **Fixed.**
5. **Open**: SD-pullback never checks for a nearby liquidity pool as confluence (unlike sweep-reversal, which checks for a nearby S&D zone) — `SupplyDemandLiquidityConfluence` telemetry is structurally always-false for its trades. Not fixed — needs a design decision (add the check, or document the asymmetry as intentional).
6. **Open**: the sweep-reversal re-entry guard's identity includes the nearby zone as well as the sweep. If zone selection drifts between a failed attempt and a re-attempt on the *same* underlying sweep, the guard could theoretically miss it. Not tightened.
7. **Open, hypothesis only**: break-retest's continued 0 trades is most likely `RequireRetestEvent=true` combined with a tight retest-distance threshold, on top of having the most gates of any playbook (11+1). Reasoned from code + prior diagnostics, not freshly re-verified with a clean run since the last two fixes landed.

### Low / informational — 2 of 3 fixed today
8. Dead ATR fallback in indicator-confluence (`?? evidence.Trigger.Indicators.Atr` can never fire). **Fixed** — simplified.
9. `StructuralEvidenceRiskMultiplier` hardcoded to `1m` with no explanation. **Fixed** — added a comment (confirmed correct behavior: avoids double-counting evidence the agent's own Confidence already reflects).
10. **Open**: a few small DRY/consistency notes (duplicated sort logic between the agent and arbitrator, a stale profile default 2 bars behind the current code default) — cosmetic, not prioritized.

### Confirmed clean
Core agent evaluation loop, candidate arbitrator, evidence factory's no-lookahead discipline, playbook state store, geometry builder, identity hashing, market structure analyzer, most of the regime classifier. One honest self-correction: a claim I made earlier this session about `MarketRegimeClassifier.Range` requiring `Sideways` structure was an oversimplification of the real (OR-based) condition — didn't invalidate the fix built on it, which was verified empirically, but the stated reasoning was imprecise.

### Scope not exhaustively covered (disclosed honestly)
`PriceActionAnalyzer`'s sweep/compression-expansion detectors and scoring tail (~250 of 1068 lines), and ~1200 lines of `StructureBasedTradeManager` outside the structural-candidate-search path (break-even/scale-out/profit-floor logic). Spot-checked, nothing red-flagged, not traced line-by-line.

## 5. Fix verification (done)

- **Sweep-reversal re-entry fix**: re-ran the same test window. `StructuralSweepAlreadySignaled` fired 65 times in the funnel (guard is actively blocking, not dead code), all 3 trades now have distinct setupIds. Outcome shift: 0%→67% win rate, -$354→+$220 net profit, avg R -1.35→+0.43.
- **Indicator-confluence**: first-ever completed real result (previous attempts were cut off by the machine restart). 232 trades, 47.4% win rate, avg R **-0.47**, net **-$9,892**. Not a bug — a genuine finding that this playbook currently has no edge with its default settings. Needs investigation as its own item, separate from today's fix list.
- Full regression suite re-run after each fix: no new failures (only the one pre-existing, unrelated `InstrumentKeyJsonConverter` test failure that's been present all session).

## 6. Explicitly not done / blocked

- **Regression test for the re-entry guard fix**: blocked on a real gap — there is **zero existing test infrastructure** for constructing a `StructuralEvidencePacket` anywhere in the codebase (only ever built in the production factory). Writing a proper playbook-level test means building that construction path from scratch. Not started — needs a scope decision.
- **Task #10** (recurring ContentHash breakage on schema changes) — tracked since earlier in the session, still unaddressed structurally. Hit again during this session's disk/profile work; recovered manually each time, no durable fix.
- **SD-pullback's real bottleneck** (`StructuralZoneReactionMissing`, ~52% of all evaluated frames) — identified, not investigated further.
- **Break-retest's 0-trade result** — hypothesis stated (§4, item 7), not freshly re-verified.
- **Legacy auto-calibration test** — still training, no result yet (§3).

## 7. Suggested next steps (for discussion, not decided)

In rough priority order, but this is exactly what's open for you to redirect:
1. Decide the test-infrastructure question (§6) — build it now, defer it, or accept verified-but-untested.
2. Let the Legacy auto-cal training finish and report the result.
3. Investigate indicator-confluence's negative edge (232-trade sample, real finding, first look).
4. Re-run break-retest fresh to confirm/refute the RequireRetestEvent hypothesis.
5. Decide on the two open medium-severity items (§4.5, §4.6) — SD-pullback liquidity confluence, tightening the re-entry identity.
6. Circle back to task #10 (ContentHash) if it keeps costing time.
