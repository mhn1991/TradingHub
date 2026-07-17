# TradingHub Agent Subsystem — Issue Register (2026-07-17)

Companion to `TradingHub_Agent_Subsystem_Audit_Report_2026-07-17.md`. Ordered by severity, then implementation priority within severity. All findings are sourced from direct code tracing (file:line citations); none are inferred from documentation, comments, or test names alone.

**Fix pass completed 2026-07-17 (same day, follow-up session):** AGENT-01 through AGENT-05 and AGENT-07 through AGENT-12 are fixed, each with a new deterministic regression test, verified with zero regressions beyond the pre-existing TEST-01 failure (`dotnet build`: 0 warnings/errors; `dotnet test`: 903/904 passing solution-wide). AGENT-06 was deferred at the user's explicit direction — see its entry below. AGENT-08's fix (removing trendline/channel contribution from `ConfidenceScorer.Total`) was confirmed with the user before implementation since it changes live decision behavior; AGENT-07's asymmetric state retention was documented as intentional rather than behavior-changed, per the same "don't change strategy choices automatically" policy.

---

## AGENT-01 — Auto-train calibration promotion does not derive `AgentOptions` from the actual backtested runtime settings

- **Severity:** High
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `CalibrationBundleWorkflow` now derives `AgentOptions` internally via a new shared `BacktestRuntimeOptions.ResolveProgressiveStrategyOptions(...)` method (extracted from `BacktestRequest`'s own version); `AgentOptions` removed from `CalibrationBundlePromotionRequest` entirely. New tests: `CalibrationBundleWorkflowTests.RunAndProposeAsync_DerivesAgentOptions_FromTrainingRuntime_NotBareDefaults`.
- **Subsystem:** QuantResearch.Training (calibration bundle promotion) — built this session, not yet in production use
- **Files and methods:** `QuantResearch.Training/Pipeline/CalibrationBundleWorkflow.cs:60`; `QuantResearch.Training/Pipeline/LiveCalibrationTrainingScheduler.cs:104`; `BacktestRunner/Program.cs:332`; contrast with `Simulator/Models/BacktestConfiguration.cs:482-510` (`ResolveProgressiveStrategyOptions`)
- **Observed behaviour:** Both current callers of `CalibrationBundleWorkflow.RunAndProposeAsync` supply `AgentOptions = new ProgressiveStrategyOptions()` (bare defaults) to `CalibrationBundlePromotionRequest`. `CalibrationTrainingRequest` has no `ProgressiveStrategyOptions` field to derive from at all — this is a structural gap in the request shape, not a missed wiring step. Meanwhile the backtest that actually generates the calibration trade population resolves its real `ProgressiveStrategyOptions` via `BacktestRequest.ResolveProgressiveStrategyOptions()`, reading real timeframes/feature settings from `Runtime`.
- **Expected behaviour:** The `AgentOptions` baked into the resulting `TradingPolicyProfile` should be exactly the options that produced the trade population the calibration artifacts were fit on — mirroring the pattern `ResolveProgressiveStrategyOptions()` itself was built to guarantee for the manual promotion path.
- **Failure scenario:** An operator runs auto-train calibration with non-default timeframes/feature settings (e.g., 15m/1h/4h instead of the bare-default 5m/15m/1h). The resulting `CalibrationBundleCandidate`, if approved and activated in live, constructs a live Agent using the bare-default timeframe plan while the calibration artifacts (setup calibration buckets, meta-model buckets) were fit on trades generated under the different, real timeframe plan — the calibration statistics no longer describe the population the live Agent will actually produce.
- **Why it matters:** Silently invalidates the statistical basis of any auto-trained calibration bundle that reaches live, defeating the purpose of leakage-safe calibration if the deployed Agent behavior diverges from the trained-against behavior.
- **Recommended correction:** Either (a) add a `ProgressiveStrategyOptions AgentOptions` field to `CalibrationTrainingRequest` and thread it through the identical resolution method the Simulator uses, or (b) have `CalibrationBundleWorkflow` compute `AgentOptions` internally from `request.Training.Runtime` via that same method, removing the caller-supplied `AgentOptions` field from `CalibrationBundlePromotionRequest` so there is no seam for mismatch. Option (b) matches this codebase's own established anti-drift pattern (`BacktestConfiguration.cs:478-480`'s doc comment).
- **Required test:** Integration test asserting that a `CalibrationBundleCandidate` produced via `CalibrationBundleWorkflow` (through either caller) has `ProposedProfile.AgentOptions` structurally equal to the `ProgressiveStrategyOptions` actually used by the backtest run that generated its training population.

---

## AGENT-02 — `MetaModelPolicyOptions.Enabled` is not read on the live construction path

- **Severity:** High
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `TradingPolicyPromotion.CreateProfile` now derives `MetaModelPolicy.Enabled` from artifact-ID presence rather than trusting the source runtime's own unrelated toggle; `TradingPolicyProfile.Validate()` gained a hard cross-field invariant; `LivePolicyBundleFactory` also gates construction on `Enabled` directly. New tests in `TradingPolicyPromotionTests.cs`.
- **Subsystem:** LiveTradingHost / Calibration construction parity
- **Files and methods:** `LiveTradingHost/Configuration/LivePolicyBundleFactory.cs:57-65`; contrast with `Simulator/Services/BacktestApplicationService.cs:536-538`; `Calibration/CalibratedSetupMetaModel.cs:30-87`; `TradingPolicies/TradingPolicyProfile.cs:88`
- **Observed behaviour:** Simulator construction gates building `CalibratedSetupMetaModel` on `runtime.MetaModel.Enabled` (a fix from the prior 2026-07-16 audit pass). Live construction (`LivePolicyBundleFactory.cs:57-65`) gates purely on `profile.MetaModelArtifactId is { } metaModelId` — `profile.MetaModelPolicy.Enabled` is never read. `CalibratedSetupMetaModel.Evaluate()` itself never reads `_options.Enabled` either. `TradingPolicyProfile.Validate()` calls `MetaModelPolicy.Validate()` but never cross-checks `MetaModelArtifactId.HasValue == MetaModelPolicy.Enabled`.
- **Expected behaviour:** `MetaModelPolicy.Enabled = false` should fully bypass the meta-model on both simulator and live construction paths, consistent with the feature-switch audit's "false genuinely bypasses the feature" requirement.
- **Failure scenario:** A live policy profile carries a stale/leftover `MetaModelArtifactId` alongside `MetaModelPolicy.Enabled = false` (e.g., left over from a prior revision, or a config mistake). Live evaluates and acts on the meta-model's reject/reduce decisions; the identical configuration in the simulator would correctly skip it — simulator/live parity is broken for this specific combination.
- **Why it matters:** Breaks construction parity and the feature-switch "false bypasses the feature" guarantee specifically on the live path, which is the side that risks real/demo capital.
- **Recommended correction:** Add `profile.MetaModelArtifactId.HasValue == profile.MetaModelPolicy.Enabled` as an explicit invariant check in `TradingPolicyProfile.Validate()` (fail fast on mismatch), and gate `LivePolicyBundleFactory`'s meta-model construction on `profile.MetaModelPolicy.Enabled` in addition to artifact-ID presence, matching the simulator.
- **Required test:** Unit test constructing a `TradingPolicyProfile` with `MetaModelArtifactId` set and `MetaModelPolicy.Enabled = false`, asserting `LivePolicyBundleFactory.BuildAsync` does not construct a meta-model (or that `Validate()` rejects the profile outright).

---

## AGENT-03 — `TradingConditionsEnabled` defaults to `false`, silently disabling all trading-condition protection outside the Dashboard UI

- **Severity:** High
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `TradingConditionOptions.Enabled` and `BacktestCommandOptions.TradingConditionsEnabled` both now default to `true` (opt-out via `--no-trading-conditions`). Three pre-existing integration tests that implicitly relied on the old `false` default were updated to opt out explicitly. New test: `TradingConditionFilterTests.Options_BareDefault_IsEnabled`.
- **Subsystem:** RiskManager (TradingConditionFilter) / construction defaults
- **Files and methods:** `RiskManager/Conditions/TradingConditionModels.cs:62`; `BacktestRunner/BacktestCommandOptions.cs:183,408`; `DashboardLive/SimulationApi.cs:1046`; `Dashboard/src/components/SimulatorPanel.vue:114,276,345,418,481,1030`
- **Observed behaviour:** The audit prompt's expected shared default is `TradingConditionsEnabled = true`. The actual bare record default on `TradingConditionOptions` is `false`, and the CLI (`BacktestCommandOptions`) also bare-defaults to `false`, only flipping on with an explicit `--trading-conditions` flag. The Dashboard's DTO default is `true`, and the Vue frontend additionally always sends `true` explicitly in every preset — masking the underlying `false` default rather than fixing it.
- **Expected behaviour:** `TradingConditionsEnabled = true` by default across every construction path, per the audit prompt's explicit shared-defaults specification.
- **Failure scenario:** Any caller of `BacktestCommandOptions`/`TradingPolicyProfile` that is not the Dashboard's Vue frontend — a bare CLI invocation, a new promotion path, a test harness, a future automation script (including the newly-added `AutoTrainCalibration` CLI path from this session) — silently runs with no session filter, no rollover blackout, no pre-weekend protection, no spread/ATR gate, and no stale-data rejection, with no error or warning.
- **Why it matters:** A single default-value bug simultaneously disables every safety mechanism `TradingConditionFilter` provides, for any caller that doesn't happen to match the one path (Dashboard UI) that overrides it.
- **Recommended correction:** Change `TradingConditionOptions`'s bare record default to `Enabled = true`, and change `BacktestCommandOptions`'s default/parsing so trading conditions are on unless explicitly disabled (`--no-trading-conditions`), matching the audit's expected shared default.
- **Required test:** Construct `TradingConditionOptions` and `BacktestCommandOptions` with no explicit override; assert `Enabled == true` in both.

---

## AGENT-04 — `LegacyProgressiveAgent` can construct a zero-distance stop; no defensive guard at the Agent layer

- **Severity:** High
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `LegacyProgressiveAgent` now uses the same positivity-checked ATR floor and explicit `InvalidStopSide`/`ZeroRisk` guards as `ImprovedProgressiveAgent`. New test: `LegacyExitModeAndRiskTests.ZeroAtrWithNoPriceActionSetup_UsesPositiveFallbackDistance_NotAZeroDistanceStop`.
- **Subsystem:** Agent (stop/target selection, Legacy path)
- **Files and methods:** `Agent/Strategies/LegacyProgressiveAgent.cs:27` (`decimal atr = entry.Indicators.Atr ?? price * 0.002m;`), `LegacyProgressiveAgent.cs:18-60` (`CreateEntryDecision`, no `risk <= 0m` guard); contrast with `Agent/Strategies/ImprovedProgressiveAgent.cs:35-37,58-64` (positivity-checked ATR, explicit `ZeroRisk` guard)
- **Observed behaviour:** Legacy's `?? price * 0.002m` only substitutes when ATR is `null`, not when it is exactly `0m` (a value the ATR indicator can legitimately produce, e.g. a flat run of identical closes). Legacy also has no `risk <= 0m` rejection anywhere in `CreateEntryDecision`. If the fallback branch is taken (no valid PA-setup reference level) with `atr == 0m`, `stop = price - 0m*FallbackStopAtr = price` for a buy — a zero-distance stop is constructible and passed straight to `Trade()`.
- **Expected behaviour:** Same defensive discipline as `ImprovedProgressiveAgent`: ATR positivity check (`is > 0m`) and an explicit `risk <= 0m → Observe("ZeroRisk")` guard before any candidate is emitted.
- **Failure scenario:** Entry-timeframe ATR reports exactly `0m` on a candle, Legacy takes the fallback stop branch, and emits a Buy/Sell candidate with `stop == price`. `RiskManager/Risk/PreTradeRiskManager.cs:219-223` independently recomputes `riskDistance` and rejects with `"stop-loss distance must be positive"` — so the candidate is caught before execution today, but only because RiskManager doesn't trust the Agent's own arithmetic, not because the Agent itself is safe.
- **Why it matters:** The Agent should never construct or emit an economically invalid candidate in the first place; relying solely on a downstream independent check for a source-level defect is fragile — any future refactor of `PreTradeRiskManager`'s validation order could silently remove this net.
- **Recommended correction:** Change `LegacyProgressiveAgent.cs:27` to `entry.Indicators.Atr is > 0m ? entry.Indicators.Atr.Value : price * 0.002m`, and add the same `risk <= 0m → Observe(ReasonCode: "ZeroRisk")` guard `ImprovedProgressiveAgent` already has.
- **Required test:** Unit test constructing an entry snapshot with `Atr = 0m` and no PA-setup reference level; assert `LegacyProgressiveAgent.CreateEntryDecision` returns `Observe` with `ReasonCode == "ZeroRisk"`, not a Buy/Sell candidate with `stop == price`.

---

## AGENT-05 — `RuntimeFeaturePolicy.ComputeHash()` is computed and stored but never enforced; the real compatibility gate uses a fixed constant instead

- **Severity:** Medium
- **Confidence:** Confirmed
- **Status:** **Fixed via documentation** (2026-07-17, option 2 from the recommended correction) — added an explicit in-code comment at `StrategyDecisionPipelineFactory.Create`'s compatibility-check call site clarifying `MetaLabelFeatureFactory.SchemaVersion` (enforced) is not `featurePolicy.ComputeHash()` (provenance-only), so this isn't mistaken for an enforced control. New test confirming hash content-sensitivity: `StrategyDecisionPipelineFactoryTests.FeaturePolicyHash_ChangesWithMarketRegimeRoutingContent`. Wiring real enforcement was not done — it would require a new artifact schema field and was judged out of scope for this pass.
- **Subsystem:** TradingCore (pipeline construction) / Calibration artifact compatibility
- **Files and methods:** `TradingCore/Pipeline/RuntimeFeaturePolicy.cs:12-21,33-70`; `TradingCore/Pipeline/StrategyDecisionPipelineFactory.cs:38-43`; `RiskManager/Calibration/MetaLabel.cs:65` (`MetaLabelFeatureFactory.SchemaVersion = "tradinghub-meta-v1"`); consumers `TradingPolicies/TradingPolicyProfile.cs:166`, `LiveTradingHost/Configuration/LivePolicyBundleFactory.cs:76`, `Simulator/Engine/StreamingComparativeEngine.cs:674`
- **Observed behaviour:** `RuntimeFeaturePolicy.ComputeHash()` bundles annotation options, regime routing, value-location/currency-strength evidence, RSI/Bollinger, DMI, and setup-calibration settings into a content hash, per its own doc comment, "so a later live host can verify a running agent matches what it was declared to be." In practice the hash is only ever populated into display/provenance fields (`FeatureSchemaHash`/`FeaturePolicyHash`) and never compared against anything. The actual artifact-compatibility check at the real consumption site (`StrategyDecisionPipelineFactory.cs:42`) passes a fixed string constant (`MetaLabelFeatureFactory.SchemaVersion`), not the computed content hash.
- **Expected behaviour:** A calibration artifact trained under one set of feature-policy options (e.g., a different `MarketRegimePolicyOptions.Policies` configuration) should be detectably incompatible with a runtime configured differently, if the audit's "content/schema hash validation" requirement is to mean anything beyond the meta-label feature schema version alone.
- **Failure scenario:** A calibration artifact trained with regime routing disabled is later served under a runtime with regime routing enabled and materially different regime policy risk multipliers. Both configurations produce the same `MetaLabelFeatureFactory.SchemaVersion` string, so the artifact passes validation and is trusted despite the semantic drift.
- **Why it matters:** The infrastructure for detecting this exact class of drift already exists and is well-designed, but is currently decorative — a real configuration-provenance gap for anything other than the meta-label feature schema itself.
- **Recommended correction:** Either wire `RuntimeFeaturePolicy.ComputeHash()` into the actual compatibility check at `StrategyDecisionPipelineFactory.cs:38-43` (in addition to or instead of the fixed schema constant), or explicitly document in-code that feature-policy content hashing is provenance-only and not a compatibility gate, so this isn't mistaken for an enforced control during future work.
- **Required test:** Construct two `RuntimeFeaturePolicy` instances differing only in `MarketRegimeRouting` settings; assert their `ComputeHash()` values differ, and (once wired) assert an artifact trained under one is rejected when served under the other.

---

## AGENT-06 — Setup calibration and meta-model are structurally near-identical, conditioning on largely overlapping axes

- **Severity:** Medium
- **Confidence:** Plausible (mechanism duplication confirmed; empirical data-population divergence not verified)
- **Status:** **Deferred at user's explicit direction (2026-07-17)** — no code change made. The user confirmed this needs offline empirical analysis against historical data (do `InstrumentGroup` and `MultiTimeframeAlignment` bucket populations actually diverge?) before any merge/simplification decision, consistent with this entry's own "Required test: N/A until the empirical check..." line.
- **Subsystem:** RiskManager.Calibration (SetupCalibrationPolicy vs CalibratedSetupMetaModel)
- **Files and methods:** `RiskManager/Calibration/SetupCalibration.cs:106-131`; `Calibration/CalibratedSetupMetaModel.cs:30-87`
- **Observed behaviour:** Both mechanisms use an identical reject/reduce/pass-through decision tree, the same `ExpectedR < 0m` reject threshold, the same `WeakPositiveExpectedRThreshold`/`WeakPositiveRiskMultiplier` (default `0.50m`) reduce threshold, the same neutral (`1m`) pass-through, and both condition on `Regime` and `Confidence`. The only differing input axis is `InstrumentGroup` (setup calibration) vs `MultiTimeframeAlignment` (meta-model).
- **Expected behaviour:** Per the audit prompt, the meta-model should either meaningfully complement setup calibration or the overlap should be an explicit, justified design decision — not an unexamined duplication.
- **Failure scenario:** If `InstrumentGroup` and `MultiTimeframeAlignment` do not produce materially different bucket populations for the same `(StrategyId, Regime, Confidence)` slice, a candidate can be independently reduced or rejected twice by what is effectively the same statistical signal, compounding a single piece of evidence into two multiplicative risk reductions.
- **Why it matters:** Either genuine complementary evidence (no action needed) or unnecessary double-filtering that reduces trade frequency and risk budget without adding real information — the distinction matters for both the gate-stacking/trade-starvation question and general system simplicity.
- **Recommended correction:** Run an empirical check (offline, using the training pipeline against historical data) comparing bucket populations for the same `(StrategyId, Regime, Confidence)` slice split by `InstrumentGroup` vs `MultiTimeframeAlignment`. If they are highly correlated, consider having one mechanism absorb the other's axis rather than running two independently-thresholded filters.
- **Required test:** N/A until the empirical check above determines whether a code change is warranted; if one mechanism is retired or merged, add a regression test asserting the remaining mechanism's behavior is unchanged for previously-passing cases.

---

## AGENT-07 — Asymmetric state retention on confirmation-evidence failure between `WaitingForConfirmation` and `WaitingForEntry`

- **Severity:** Medium
- **Confidence:** Confirmed (behavior); intent unconfirmed
- **Status:** **Documented as intentional, not behavior-changed** (2026-07-17) — added explicit code comments at both branches in `ProgressiveStrategyBase.cs` explaining the asymmetry's rationale, per the audit's own "add a comment if deliberate, unify if not" guidance; changing state-machine retention behavior was judged a strategy choice, not a correctness defect, so it was left as-is. New tests pin the documented behavior for both stages: `ProgressiveStrategyStateRetentionTests.cs`.
- **Subsystem:** Agent (ProgressiveStrategyBase state machine, shared by both Legacy and Improved)
- **Files and methods:** `Agent/Strategies/ProgressiveStrategyBase.cs:298-306` (WaitingForConfirmation: state retained on `!Satisfied`), `ProgressiveStrategyBase.cs:331-341` (WaitingForEntry: state cleared on the same condition, `ReasonCode = "ConfirmationConsensusLost"`)
- **Observed behaviour:** The identical underlying confirmation-evidence assessment (`!Satisfied`, not `StrongOpposition`) is handled differently depending on which stage discovers it: retained while `WaitingForConfirmation`, cleared while `WaitingForEntry`.
- **Expected behaviour:** Either the same evidence condition should have the same retention policy regardless of stage, or the asymmetry should be documented as an intentional design choice ("losing confirmation once you've reached the entry window is more decisive than still waiting for it").
- **Failure scenario:** Not a crash or leakage scenario — a diagnostics/predictability scenario: an operator investigating "why did this setup disappear" sees inconsistent behavior for what looks like the same evidence-failure condition, depending only on which stage it occurred in, with no code comment explaining why.
- **Why it matters:** Matches the audit's explicit "stale setup state" checklist item; undocumented asymmetries in a state machine are a common source of future regressions when one branch is edited without noticing its sibling has different behavior.
- **Recommended correction:** Add an explicit code comment stating the intended asymmetry if it is deliberate, or unify the two branches' retention policy if it is not.
- **Required test:** State-machine test asserting the chosen (post-decision) retention behavior explicitly for both stages under the identical `!Satisfied` condition, so future changes cannot silently re-diverge the two branches without failing a test.

---

## AGENT-08 — Trendlines/channels still materially influence entry-gating confidence despite being excluded from stop/target selection

- **Severity:** Medium
- **Confidence:** Confirmed
- **Status:** **Fixed with explicit user confirmation** (2026-07-17) — trendline/channel contributions removed entirely from `ConfidenceScorer.Total` (previously up to ~20 points), matching the standard already applied to stop/target selection. This changes live decision behavior; the user confirmed this specific approach ("zero out the contribution") before implementation. New test: `AnalysisTests.ConfidenceScorer_TrendlinesAndChannelsDoNotContributeToTotal`. Zero regressions in the full Simulator.Tests suite after the change.
- **Subsystem:** ChartAnnotator (ConfidenceScorer) / Agent (DetectSide gating)
- **Files and methods:** `ChartAnnotator/Structure/ConfidenceScorer.cs:141-192` (up to +10 trendline, +10 channel, ±4/-6 structure-alignment); `Agent/Strategies/ProgressiveStrategyBase.cs:920-927` (`DetectSide`'s confidence-floor check, applied at trend/secondary-trend/setup/confirmation/entry layers); contrast with `ImprovedProgressiveAgent.cs:219-222,329-331` (explicit removal of trendline/channel from stop and target selection)
- **Observed behaviour:** A prior cleanup pass removed RANSAC-trendline/channel boundaries as stop or target sources, with explicit in-code comments stating they are "not considered reliable enough to anchor invalidation levels" — corroborating the standing project judgment that these signals are unreliable. However, `ConfidenceScorer.Total` — which gates `DetectSide` at every one of the state machine's timeframe layers — still incorporates up to a ~20-point swing from trendline/channel evidence, unchanged by that cleanup.
- **Expected behaviour:** If trendlines/channels are judged unreliable enough to exclude from stop/target selection (the most safety-critical decisions), the same judgment plausibly extends to whether they should influence setup detection/confirmation gating at all — this is a scope question the prior cleanup did not address.
- **Failure scenario:** A setup that would otherwise fail the confidence floor at, say, the trend or confirmation layer instead passes because of trendline/channel-derived points from the same evidence already judged too unreliable to anchor a stop.
- **Why it matters:** A narrower, previously-undiscovered residual dependency on a signal the project has already decided not to trust for safety-critical decisions.
- **Recommended correction:** Product-owner decision required, not a mechanical fix — either reduce/remove the trendline/channel contribution to `ConfidenceScorer.Total`, or explicitly document that confidence-gating and stop/target-anchoring are different reliability bars and the current split is intentional.
- **Required test:** Once a decision is made, a `ConfidenceScorer` unit test locking in the chosen trendline/channel contribution (zero, reduced, or unchanged) so it cannot silently regress.

---

## AGENT-09 — Orphaned `LiveAgentStrategyOptions` configuration type documents a nonexistent construction path

- **Severity:** Low
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `LiveTradingHost/Configuration/LiveAgentStrategyOptions.cs` deleted (confirmed zero references anywhere else in the repo before deletion).
- **Subsystem:** LiveTradingHost configuration
- **Files and methods:** `LiveTradingHost/Configuration/LiveAgentStrategyOptions.cs:9-13`; contrast with the real path `LiveTradingHost/appsettings.json:52` (`LivePolicyBundles`) consumed at `LiveTradingHost/LiveEngineHostedService.cs:441-442`
- **Observed behaviour:** `LiveAgentStrategyOptions` is a config-bindable type with its own doc comment claiming it is "Bound from the `LiveAgentStrategies` section." A repo-wide search found zero references to it outside its own file — no `Configure<...>()` registration, no reader, no matching config section anywhere.
- **Expected behaviour:** Either the type is wired up and used, or it is removed.
- **Failure scenario:** A future engineer edits a `LiveAgentStrategies` config section believing it affects live Agent construction; it has no effect because nothing reads it.
- **Why it matters:** Misleading, self-contradicting documentation-as-code; low risk but a real hygiene issue given the audit's environment-neutrality/construction-parity focus.
- **Recommended correction:** Delete `LiveAgentStrategyOptions.cs` (confirmed dead code, not merely unused-but-safe).
- **Required test:** None required (deletion of genuinely dead code).

---

## AGENT-10 — No aggregated, queryable per-instance diagnostics funnel-counter object

- **Severity:** Medium
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `AgentInstanceState` gained `Evaluations`, `CandidatesRejectedByDataQuality`, `CandidatesRejectedBySafety`, a generic `ObserveReasonCounts` dictionary (keyed by `AgentDecision.ReasonCode`, covering the trend/setup/confirmation/price-action/regime/invalid-stop breakdown without a hand-maintained enum), and `DuplicateDecisions`; all updated in `AgentSupervisor.EvaluateAsync` alongside existing diagnostics writes, without altering decision logic. New tests in `AgentSupervisorTests.cs` (3 added).
- **Subsystem:** Agent / LiveTrading diagnostics
- **Files and methods:** `LiveTrading/Agents/SignalFunnel.cs` (per-decision audit builder, not a counter); `TradingCore/Pipeline/SafeTradingPipeline.cs:16-26` (`TradingPipelineStatus`, 8 coarse values); `Agent/Models/AgentModels.cs:43` (free-text `ReasonCode` per decision)
- **Observed behaviour:** ~50+ distinct `ReasonCode` string literals exist and are correctly, distinctly labeled per rejection category (confirmed no cross-subsystem mislabeling in the codes enumerated). However, no first-class object anywhere aggregates these into running counts (Evaluations, ObserveCount, BuyCandidateCount, per-category rejection counts, "last candidate," "last error") for a given Agent instance. `SignalFunnel` only fires for decisions that already survived to Buy/Sell — it does not track Observe/rejection volume at all.
- **Expected behaviour:** Per the audit prompt's diagnostics/observability requirements, a per-instance structured diagnostics surface covering evaluation counts, decision-type counts, and per-category rejection counts.
- **Failure scenario:** Answering "how many Setup rejections did strategy X have on instrument Y today" requires re-scanning historical decision records and grouping by `ReasonCode`/`Status` after the fact — there is no live counter to read directly, making operational monitoring and alerting harder than necessary.
- **Why it matters:** A genuine, confirmed observability gap relative to what the audit describes as desired, though not a correctness defect — reason-code granularity itself is good.
- **Recommended correction:** Add a per-`AgentInstanceState` (or per-strategy-instrument) counter object, updated alongside existing diagnostics writes, without altering decision logic (per the audit's "diagnostics must not alter logic" requirement) — plain records, no DB coupling, consistent with existing `AgentDecision`/`TradingPipelineResult` persistability.
- **Required test:** Unit test asserting counters increment correctly for a scripted sequence of Observe/Buy/Sell/rejection decisions, and that reading them never changes subsequent decision outcomes.

---

## AGENT-11 — DMI's dual role (hard veto and soft score) is not distinguishable in diagnostics

- **Severity:** Low
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `DetectSide` gained an `out string? vetoReasonCode` overload that reports `"DmiVetoed"` distinctly from other null-side outcomes; the two trend-check call sites that surface a reason code from a null `DetectSide` result now use it. New test: `DmiVetoReasonCodeTests.DmiOpposition_ProducesDmiVetoedReasonCode_NotGenericPrimaryTrendNotReady`.
- **Subsystem:** Agent (DetectSide) / ChartAnnotator (ConfidenceScorer)
- **Files and methods:** `Agent/Strategies/ProgressiveStrategyBase.cs:888-897,904-905` (hard veto inside `DetectSide`, runs before the confidence-floor check at `:920-927`); `ChartAnnotator/Structure/ConfidenceScorer.cs:195-212` (soft ADX/DMI score contribution)
- **Observed behaviour:** When `EnableDmiConfirmation` is on, DMI/ADX acts as both a hard conflict-resolver/veto inside `DetectSide` and a soft scored contribution to `ConfidenceScorer.Total` — different roles, same signal, same call. The hard veto runs first and can prevent a side from ever being detected, making its own soft-score contribution moot for that cycle. No distinct reason code (e.g., `DmiVetoed`) exists to tell an operator, from the reason code alone, whether DMI specifically was the deciding factor versus "no structural/tactical candidate at all."
- **Expected behaviour:** A reason code specific enough to distinguish the DMI hard-veto outcome from other `PrimaryTrendNotReady`-class rejections.
- **Failure scenario:** An operator tuning `EnableDmiConfirmation` or its thresholds cannot determine from decision logs alone how often DMI specifically was the rejecting factor.
- **Why it matters:** Diagnostics-only gap; does not affect decision correctness, only explainability/tunability.
- **Recommended correction:** Add a distinct reason code inside `DetectSide`'s DMI-veto branch (e.g. `"DmiVetoed"`) without changing the veto's actual effect.
- **Required test:** Unit test asserting the DMI-veto branch produces the new distinct reason code under a scripted opposing-DMI scenario.

---

## AGENT-12 — Dead enum member `SetupStage.WaitingForTrend`

- **Severity:** Informational
- **Confidence:** Confirmed
- **Status:** **Fixed** (2026-07-17) — `SetupStage.WaitingForTrend` removed (confirmed zero references anywhere before removal); added a clarifying comment on `SetupStage` explaining the "no scope" condition is represented structurally.
- **Subsystem:** Agent (ProgressiveStrategyBase)
- **Files and methods:** `Agent/Strategies/ProgressiveStrategyBase.cs:14`
- **Observed behaviour:** Declared but never assigned anywhere in the class; the "waiting for a primary trend" condition is actually represented by the absence of a `_states[instrument]` dictionary entry, not this enum value.
- **Expected behaviour:** Either the enum member is wired in to represent that condition explicitly, or removed.
- **Failure scenario:** A maintainer reading the enum reasonably assumes 3 real `ScopeState.Stage` values exist in practice; only 2 ever do — a documentation-accuracy risk, not a runtime one.
- **Why it matters:** Low risk, but a real discrepancy between the type system's declared states and the state machine's actual behavior, directly relevant to the audit's "unreachable or terminal states" checklist item.
- **Recommended correction:** Remove `SetupStage.WaitingForTrend`, or replace the implicit "no dictionary entry" condition with an explicit state value for clarity — either is acceptable; removal is lower-risk.
- **Required test:** None required either way (behavior is unchanged; this is a documentation-accuracy fix).

---

## AGENT-13 — Dead/test-only `RuleBasedMultiTimeframeAgent` not wired into production

- **Severity:** Informational
- **Confidence:** Confirmed
- **Subsystem:** Agent
- **Files and methods:** `Agent/Strategies/RuleBasedMultiTimeframeAgent.cs`; `Agent/Strategies/ProgressiveAgentFactory.cs:18-22` (only `Legacy`/`Improved` cases exist)
- **Observed behaviour:** A third Agent implementation exists with its own entry/confidence logic but is referenced only by itself and `Simulator.Tests/AgentStrategyEdgeTests.cs` — never reachable via `ProgressiveAgentFactory` or any production/live construction path.
- **Expected behaviour:** Production code should not contain an unreferenced alternate implementation without explicit deprecation markers.
- **Failure scenario:** None directly — risk is maintenance confusion (a future engineer might assume it's live) and audit-scope ambiguity (unclear whether this file should be held to the same correctness bar as the two production Agents).
- **Why it matters:** Low risk; flagged for cleanup/explicit-deprecation decision.
- **Recommended correction:** Either wire it into `ProgressiveAgentFactory` as a genuine third option if it has ongoing value, or delete it and its dedicated test file if it does not.
- **Required test:** None required for removal; if retained and wired in, it would need the same construction-parity/state-machine test coverage as the other two Agents.

---

## AGENT-14 — Unclear whether a live policy hot-swap actually reaches Agent construction, or requires a host restart

- **Severity:** Informational (open question, not a confirmed defect)
- **Confidence:** Unable to determine
- **Subsystem:** LiveTrading (AgentSupervisor / LivePolicyRegistry)
- **Files and methods:** `LiveTrading/Agents/AgentSupervisor.cs` (single `Register` call site, at startup only, no `Unregister`/re-registration path found); `LiveTrading/Configuration/LivePolicyRegistry.cs:101-112` (`Activate`, confirmed to swap `_current`/append to `_history` correctly for already-open-position continuity)
- **Observed behaviour:** `LivePolicyRegistry.Activate`'s data-retention design (current pointer + append-only history) was confirmed correct for keeping already-open positions on their original policy revision. Whether `Activate` (or anything else) causes `AgentSupervisor` to construct a *new* `AgentInstanceState`/`StrategyDecisionRuntime` reflecting the newly-activated Agent options for *new* candidates is unconfirmed — no re-registration code path was found in either `AgentSupervisor.cs` or `LiveEngineHostedService.cs`.
- **Expected behaviour:** N/A until confirmed either way — this is a question, not yet a classified defect.
- **Failure scenario (if the gap is real):** An approved and activated policy revision's *entry-decision* changes (new `AgentOptions`) would never take effect for new candidates without a host restart, even though position-management continuity for already-open positions would correctly still work — silently limiting what "activation" actually accomplishes.
- **Why it matters:** Directly relevant to the calibration-training-pipeline work from earlier this session (Phase 3/4 of the automated calibration plan) — if hot-swap doesn't reach Agent construction, "activation" is a partial operation for entry decisions specifically.
- **Recommended correction:** Trace `LiveEngineHostedService`/`AgentSupervisor` end-to-end for what "activation" is actually expected to accomplish, and either implement a re-registration path or document that a host restart is required to pick up new entry-decision options after activation.
- **Required test:** Once resolved, an integration test activating a new policy revision and asserting whether a subsequent *new* candidate for that key uses the new or old `AgentOptions`.

---

## Open questions not promoted to issues (tracked for future follow-up, no confirmed defect)

These were explicitly flagged by the research forks as unresolved from source alone; none currently has a concrete failure scenario, so none is registered as a defect:

1. RANSAC trendline/channel internal fitting math (`ChartAnnotator/Structure/RansacTrendlineDetector.cs`, `ChannelDetector.cs`) was confirmed to take only already-confirmed-swing input, but was not read line-by-line for a subtler internal lookahead (e.g., centered smoothing before fitting). Classified **Possible but guarded**, not confirmed leakage.
2. `WarmupDays = 21` (`Simulator/Models/RecommendedSimulationDefaults.cs:14`) numeric sufficiency against the largest indicator lookback was not proven with an explicit trace.
3. Live's `AgentMarketContext.Timestamp` is wall-clock (`timeProvider.GetUtcNow()`, `LiveTrading/Actors/MarketAnalysisActor.cs:187`) versus Simulator's candle/replay-clock-derived timestamp (`StrategySimulationSession.cs:480`). No branching or leakage was found from this difference, but `SetupId`/`DecisionId` embed `context.Timestamp:O`, so the two environments compose IDs from different timestamp provenance — worth confirming this creates no ID-stability issue under replay-vs-live comparison.
4. Precedence when Agent-driven `Close` (thesis invalidation), TradeManager-driven management exit, and an active account-safety pause could all be relevant to the same open position in the same evaluation cycle — no single unified arbitration function was located; `SimulatedTradeExitReason` cleanly distinguishes the *source* of an exit after the fact, but no ranked precedence table was confirmed in code.
5. Whether a stop distance computed from the ATR floor (`Math.Max(price * 0.002m, 0.00000001m)`) can round to zero or to the entry price after downstream `PriceIncrement` rounding (`LiveTrading/Portfolio/LiveOpportunityCoordinator.cs:417-422`) for very low-precision instruments — not traced end-to-end.
6. Whether Simulator and Live point at the same physical `ICalibrationArtifactRepository` filesystem path in actual deployment configuration.
7. `CurrencyStrengthOptions.Enabled` (portfolio-level) exact consumption gate and its interaction with the per-decision `CurrencyStrengthEvidenceOptions.Enabled` — both flags confirmed to exist; the two-flag interaction was not fully traced.
8. Whether `StrategyAssignments` can produce two assignments with the same `(StrategyType, Instrument)` pair, which would create two Agent instances silently competing on one instrument — no validation for this was found, but a dedicated check was not performed either.
