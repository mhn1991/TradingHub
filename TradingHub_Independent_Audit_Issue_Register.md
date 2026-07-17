# TradingHub Independent Audit — Issue Register

**Companion report:** [`TradingHub_Independent_Audit_Report.md`](./TradingHub_Independent_Audit_Report.md)  
**Date:** 2026-07-17 (revised later the same day — see Revision note in the companion report)  
**Ordering:** Severity, then implementation priority (P0 before P1 before P2)

Severity key: **Critical** | **High** | **Medium** | **Low** | **Informational**

---

## Resolved since the first pass

### BUILD-01 — RESOLVED

| Field | Value |
|---|---|
| **Title** | Solution did not build — PositionSizing CS0136 variable shadowing |
| **Severity** | ~~Critical~~ Resolved (was Critical, blocking) |
| **Confidence** | Confirmed (build log), fix confirmed by direct re-run |
| **Subsystem** | RiskManager |
| **Files / methods** | `RiskManager/Risk/PositionSizing.cs` — `PositionSizer.Calculate` (~197–239 vs later method locals) |
| **Observed (first pass)** | `dotnet build TradingHub.slnx -c Release` failed with 9× CS0136 (`fixedStop`, `estimatedLoss`, `estimatedMargin`, `maximumAccountMargin`, `maximumSinglePositionMargin`, `currentMargin`, `openRisk`, `projectedOpenRisk`, `maxOpenRiskPercent`) |
| **Observed (this revision)** | The `FixedQuantity` branch's colliding locals were renamed with a `fixed*` prefix. `dotnet build TradingHub.slnx -c Release` now succeeds, 0 warnings / 0 errors. |
| **Residual action** | `dotnet test TradingHub.slnx -c Release` **has now been re-run** (final revision of this audit): 868/872 runnable tests pass, 4 fail. Two of the four failures are new, real production bugs found only because this fix unblocked the suite — see **PERSIST-01** and **PROMO-01** below. One is test-fragility (**TEST-01**). See the companion report §17 for the full breakdown. |
| **Why it mattered** | Hard stop for every environment; this was itself evidence of an in-progress, unfinished attempt to close the RSK-01 gap (fixed-quantity monetary risk checks) — see RSK-01. |

---

### PERSIST-01 — RESOLVED (fixed this pass)

| Field | Value |
|---|---|
| **Title** | Live checkpoint cannot be reloaded once any equity-protection tier has activated (JSON deserialization crash) |
| **Severity** | ~~High~~ Resolved (was Critical-adjacent — could turn a restart into an unrecoverable fault at exactly the moment the account is already risk-reduced) |
| **Confidence** | Confirmed (failing test, full stack trace); fix confirmed by direct re-run |
| **Subsystem** | LiveTrading Persistence / RiskManager Safety |
| **Files / methods** | `LiveTrading/Persistence/LiveTradingPersistence.cs:159,180` (`FileLiveTradingPersistence.LoadCheckpointAsync`); `RiskManager/Safety/TradingSafety.cs:147` (`EquityHighWatermarkSnapshot.ActivatedTierIds` declared `public required IReadOnlySet<string>`) |
| **Observed (before fix)** | `LiveTrading.Tests.Phase3.LiveTradingPersistenceTests.Checkpoint_RoundTripsTransactionCursorAndAppliedEventIds` and `Checkpoint_HashMismatchIsQuarantined` both failed with `System.NotSupportedException: The collection type 'System.Collections.Generic.IReadOnlySet\`1[System.String]' is abstract, an interface, or is read only, and could not be instantiated and populated. Path: $.payload.safety.equityProtection.activatedTierIds`. `System.Text.Json` cannot deserialize into an `IReadOnlySet<T>`-typed property without a custom converter. |
| **Fix applied** | Added `RiskManager/Safety/ReadOnlyStringSetJsonConverter.cs` (a `JsonConverter<IReadOnlySet<string>>` that reads/writes the set as a plain JSON string array) and applied `[JsonConverter(typeof(ReadOnlyStringSetJsonConverter))]` directly to `EquityHighWatermarkSnapshot.ActivatedTierIds`. Property-level attribute placement (rather than patching `FileLiveTradingPersistence`'s local `JsonSerializerOptions`) means the fix applies to any future serialization path for this type, not just the one call site that happened to have a failing test. |
| **Verification** | `dotnet test LiveTrading.Tests --filter FullyQualifiedName~LiveTradingPersistenceTests` → 3/3 pass (both previously-failing tests plus the third in the fixture). Full solution re-build: 0 warnings/errors. Full `dotnet test` re-run: no regressions (see companion report §17). |
| **Residual** | No explicit round-trip test with a *populated* `ActivatedTierIds` was added (the two existing tests happen to exercise this incidentally) — recommend an explicit test as defense-in-depth, not required to consider this closed. |

---

### PROMO-01 — RESOLVED (fixed this pass)

| Field | Value |
|---|---|
| **Title** | Promoted `TradingPolicyProfile` fails to reload from JSON — root cause was broader than originally diagnosed |
| **Severity** | ~~High~~ Resolved |
| **Confidence** | Confirmed (failing test, full stack trace); fix confirmed by direct re-run |
| **Subsystem** | Brokers / TradingCore / TradingPolicies / Agent / Simulator.Tests |
| **Correction to earlier diagnosis** | The original hypothesis (nullable `RegimeInterval` losing its null-ness on round trip) was **wrong** — a targeted repro proved `BarInterval?` round-trips `null` correctly. The **real** root cause, found by direct repro: `BarInterval` (a `readonly record struct` with a *validating* constructor) has no working JSON round trip **at all**, nullable or not. System.Text.Json's reflection-based deserializer prefers a record struct's implicit parameterless constructor over the custom validating one when both exist, then cannot populate the get-only `Value`/`Unit` properties afterward — every `BarInterval`-typed field (not just `RegimeInterval`) silently became `default(BarInterval)` (zeroed, invalid) after any JSON round trip. Fixing that then surfaced a **second**, independent bug: `RuntimeFeaturePolicy.ComputeHash()` hashed several sub-options via their auto-generated `ToString()`, which prints list/array-typed properties as their *runtime type name*, not their contents — a JSON-deserialized `List<T>` vs. the original compiler-synthesized empty-array default produced different strings for identical content, causing a spurious `ConfigurationHash` mismatch even after the `BarInterval` fix. Fixing *that* then surfaced a **third** issue: the test's own `Is.EqualTo(...)` assertions on `ProgressiveStrategyOptions`/`TradingConditionOptions`/`PositionManagementOptions` compare via record-generated `.Equals()`, which is reference-based for any `IReadOnlyList<T>`/array-typed property — meaning the test could never have passed post-JSON-round-trip regardless of any production fix, since a JSON-deserialized `List<T>` never reference-equals the original array default even with identical elements. |
| **Files / methods changed** | `Brokers/Models/BarInterval.cs` (added `[JsonConverter(typeof(BarIntervalJsonConverter))]`); `Brokers/Models/BarIntervalJsonConverter.cs` (new — reads/writes `{value, unit}` through the validating constructor); `TradingCore/Pipeline/RuntimeFeaturePolicy.cs` (`ComputeHash()` — canonicalizes `AnnotationOptions.PriceActionSetups.EnabledSetups` via `with {}` plus an explicit sorted-content component, mirroring the existing `RegimeIdentity`/`CurrencyStrengthIdentity` pattern instead of relying on `ToString()`); `Simulator.Tests/TradingPolicyPromotionTests.cs` (assertions now compare canonical JSON strings instead of record `Is.EqualTo`, which cannot verify true content equality across a JSON round trip for these types). |
| **Why the JSON-serialization approach, not attribute-on-type, for the hash fix** | `TradingCore`/`Brokers` both enforce AOT/trim-analyzer warnings-as-errors, which rules out generic reflection-based `JsonSerializer.Serialize<T>()` calls without a source-generated context. The hash fix therefore extends the codebase's existing hand-rolled "explicit content identity" convention (already used for `Policies`/`Baskets`) rather than introducing JSON serialization into `ComputeHash()`. |
| **Verification** | `dotnet test Simulator.Tests --filter FullyQualifiedName~TradingPolicyPromotionTests` → 1/1 pass (was 1/1 fail with 3 sub-assertion failures). Full solution re-build: 0 warnings/errors. Full `dotnet test` re-run: **Simulator.Tests 659/659 pass** (was 658/659), no regressions elsewhere. |
| **Residual** | This changes `RuntimeFeaturePolicy.ComputeHash()`'s output bytes (the algorithm changed, not just a bug fix that happens to preserve old hash values) — any *persisted* `TradingPolicyProfile` computed with the old, buggy hash algorithm will now fail its own `Validate()` hash check and need re-promotion. No hardcoded hash-value assertions were found in the test suite, and no such profile could plausibly have existed anyway (this exact path was already crash-broken before the fix), but flag this if any real `LivePolicyBundles` files exist outside this repo. |

---

### TEST-01 — separate, pre-existing, low-severity, not touched this pass

| Field | Value |
|---|---|
| **Title** | `HostRejectsBrokerWritesOutsideOandaPractice` fails on an unrelated namespace refactor, not a weakened guard |
| **Severity** | Low / Informational |
| **Confidence** | Confirmed — guard verified intact by direct source read |
| **Subsystem** | LiveTrading.Tests / LiveTradingHost |
| **Files / methods** | `LiveTrading.Tests/NoOrderPlacementGuardTests.cs:32-41`; `LiveTradingHost/Program.cs:125-128` |
| **Observed** | The test does a literal string-scan of `Program.cs` for `"oanda.Environment != Brokers.Models.BrokerEnvironment.Demo"`. The actual, currently-correct guard reads `oanda.Environment != Brokers.Abstractions.BrokerEnvironment.Demo` — `BrokerEnvironment` moved from the `Brokers.Models` to `Brokers.Abstractions` namespace at some point, and the test's literal string was never updated. Directly re-read `Program.cs:125-128`: the Demo-only write gate and its exact rejection message are both fully intact and correct. |
| **Expected** | A safety-canary test should fail only when the safety property it checks actually changes, not on cosmetic refactors |
| **Failure scenario** | None today (the guard works) — but this test-shape means a *future* genuine weakening of the guard could be masked by "oh, that test is just flaky/stale again" fatigue |
| **Why it matters** | False-positive safety alarms erode trust in the exact test category (write-safety canaries) that most needs to be trusted |
| **Recommended correction** | Convert to a behavioral test: construct `LiveTradingHost`'s startup path with `BrokerWritesEnabled=true` and a non-Demo `BrokerEnvironment`, assert it throws with the expected message — rather than matching literal source text |
| **Required test** | The new behavioral test passes; the old literal-text test is deleted or updated |

---

## Required before manual broker Demo (P0)

### LIVE-01

| Field | Value |
|---|---|
| **Title** | Live agent pipeline never evaluates trading conditions |
| **Severity** | High |
| **Confidence** | Confirmed |
| **Subsystem** | LiveTradingHost / TradingCore |
| **Files / methods** | `LiveTradingHost/Program.cs` — `StrategyDecisionPipelineFactory(new ShadowExecutionCoordinator())` with default `tradingConditions: null`; compare `Simulator/Engine/StrategySimulationSession.Create` |
| **Observed** | Promoted `TradingConditions` unused at decision time; live candidates skip session/spread/rollover gates |
| **Expected** | Same `TradingConditionFilter` behaviour as validated simulator policy |
| **Failure scenario** | Manual Practice entry during rollover/wide spread that research rejected |
| **Why it matters** | Invalidates “promoted from sim” safety story |
| **Recommended correction** | Build per-policy or shared `TradingConditionFilter` from bundle options; inject into pipeline factory / agent factory |
| **Required test** | Live DI integration: SessionNotAllowed / HardSpread produce Delayed/Rejected statuses |

---

### LIVE-02

| Field | Value |
|---|---|
| **Title** | Live ChartAnnotator ignores policy AnnotationOptions |
| **Severity** | High |
| **Confidence** | Confirmed |
| **Subsystem** | LiveTradingHost |
| **Files / methods** | `LiveTradingHost/LiveEngineHostedService.cs` — `BuildActor` → `new ChartAnnotationEngine()` |
| **Observed** | Default annotation options always used |
| **Expected** | Engine constructed with `FeaturePolicy.AnnotationOptions` from promoted profile |
| **Failure scenario** | Indicator periods / heavy analysis cadence differ → different decisions than research |
| **Why it matters** | Silent strategy drift |
| **Recommended correction** | Pass annotation options from registered policy/market definition into `ChartAnnotationEngine` |
| **Required test** | Actor built with non-default ATR period differs from default; hash of policy includes options |

---

### CAL-01

| Field | Value |
|---|---|
| **Title** | Live calibrated position management is silently and permanently inert (hardcoded `"Live"` volatility bucket) |
| **Severity** | High |
| **Confidence** | Confirmed (independently derived twice, matching file:line evidence both times) |
| **Subsystem** | LiveTrading Management / TradeManager |
| **Files / methods** | `LiveTrading/Management/LivePositionManagementService.cs` (`EntryVolatilityBucket = "Live"`, `EntryManagementProfileId = "live"`) vs `Simulator/Engine/StrategySimulationSession.cs` (real `VolatilityBucket(decision.AtrPercentile)`); consumed by `TradeManager/ManagementCalibration.cs` (`VolatilityBucket` exact-string-equality cohort lookup) |
| **Observed** | Every live position's calibration-cohort lookup misses (cohorts are keyed on real buckets like `"Low"/"Normal"/"High"`, never `"Live"`), so `Apply()` always returns the static/uncalibrated fallback — silently, with no error, warning, or telemetry distinguishing this from a legitimate "no matching cohort" case |
| **Expected** | Live positions carry a real, entry-time volatility bucket computed the same way as the simulator, so calibrated management (when enabled) actually applies |
| **Failure scenario** | Operator enables `ManagementCalibrationOptions.Enabled=true` in production believing calibrated management (validated in research) is now active for live trades; it silently never is |
| **Why it matters** | Currently latent (`CalibrationOptions.Enabled` defaults false) but will silently no-op the instant it's turned on for live, with no signal that it isn't working; also contaminates `QuantResearchRunner/Mapping/ResearchTradeMapper.cs` training data if live outcomes are ever fed back into calibration, since every live-trade row would carry the invalid `"Live"` bucket |
| **Recommended correction** | Thread the real ATR-percentile/volatility bucket and resolved regime-management-profile-id through the live entry path (from the same Agent decision object used in simulation) into the persisted live position record, instead of the two literal strings |
| **Required test** | Live position with a known ATR percentile, calibration enabled, asserts a real (non-`"Live"`) `VolatilityBucket` reaches `ManagementCalibration.Apply` and a matching cohort is found when one exists |

---

### PTF-02

| Field | Value |
|---|---|
| **Title** | Open lot without protective stop contributes zero portfolio heat |
| **Severity** | High |
| **Confidence** | Confirmed |
| **Subsystem** | PortfolioManager |
| **Files / methods** | `PortfolioManager/Risk/PortfolioRiskModels.cs` — heat calc `if (lot.ProtectiveStopPrice is not decimal stop) return 0m` |
| **Observed** | Unprotected lots free up admission capacity |
| **Expected** | Fail closed: non-zero assumed risk or block new entries while any owned lot lacks stop |
| **Failure scenario** | Lost stop → more risk admitted → cascade |
| **Why it matters** | Heat is primary portfolio safety spine |
| **Recommended correction** | Treat missing stop as full notional or configured assumed distance; trip/pause new entries |
| **Required test** | Reservation rejected when open lot has null ProtectiveStopPrice |

---

### PTF-01 / EXE-02

| Field | Value |
|---|---|
| **Title** | Simulator shared portfolio re-sizes after allocation (double sizing) |
| **Severity** | High |
| **Confidence** | Confirmed |
| **Subsystem** | Simulator / ExecutionManager / PortfolioManager |
| **Files / methods** | `Simulator/Engine/SharedPortfolioRuntime.cs` Flush (~432–443) sets allocation fields but **not** `QuantityIsPortfolioApproved`; `LiveOpportunityCoordinator` sets it true; `ExecutionManager/Execution/ExecutionCoordinator.cs` re-sizes when flag false then `Min(sizing, allocation)` |
| **Observed** | Risk budget applied twice / quantity may shrink below reserved; pending heat overstated |
| **Expected** | Authoritative portfolio quantity once (live parity) |
| **Failure scenario** | Sim results ≠ live; reservation accounting wrong |
| **Why it matters** | Parity and safety accounting |
| **Recommended correction** | Set `QuantityIsPortfolioApproved = true`, `SuggestedQuantity = AllocatedQuantity` on sim path; skip re-size |
| **Required test** | Shared portfolio places exact allocated quantity; journal shows no second shrink |

---

### RSK-01

| Field | Value |
|---|---|
| **Title** | FixedQuantity can approve without monetary risk estimate |
| **Severity** | High |
| **Confidence** | Confirmed |
| **Subsystem** | RiskManager |
| **Files / methods** | `RiskManager/Risk/PositionSizing.cs` — FixedQuantity branch returns Approved when stop/rate/account incomplete (~262–268); `LiveTrading/Portfolio/LiveOpportunityCoordinator.cs` `EvaluateCore` (~line 161, `GetValueOrDefault(candidate.Instrument, 0m)`) and reservation construction (~line 203, `EstimatedMargin = sizing.EstimatedMargin ?? 0m`) |
| **Observed** | Legacy path approves fixed lots without heat/margin numbers. Precise trigger identified this pass: `LiveOpportunityCoordinator` silently defaults the FX conversion rate to **zero** (not a rejection) whenever `QuoteToAccountCurrencyRates` lacks an entry for the instrument, which starves the sizer of one of its four required inputs; the resulting `null` `EstimatedMargin`/`EstimatedLossAtStop` is then coalesced to `0m` at the reservation layer, so `PortfolioReservationBook.TryReserve` admits a zero-risk reservation that cannot breach any cap. **Both the position-level and portfolio-level risk gates are defeated by the same missing-FX-rate condition simultaneously.** A stop-loss is always attached in this path (position is not literally unprotected), which is why this stays High rather than Critical. |
| **Expected** | Live/shared admission fail closed without complete inputs |
| **Failure scenario** | A momentary FX-rate-feed gap (untracked cross pair, refresh-timing race) — not active misconfiguration — causes an oversized fixed lot to be admitted as if it had zero margin usage and zero monetary risk |
| **Why it matters** | Risk bypass via the "default/quiet" path rather than requiring active misconfiguration |
| **Recommended correction** | Reject incomplete FixedQuantity unless explicit `AllowLegacyFixedQuantityWithoutRiskEstimate`; live always require estimate; additionally, `LiveOpportunityCoordinator`/`PortfolioReservationBook` should treat a `null` `EstimatedMargin`/`EstimatedLossAtStop` as "risk unknown" (reject) rather than coalescing to `0m` |
| **Required test** | FixedQuantity without stop rejected under live/shared admission flag; separately, a candidate with a missing FX rate is rejected rather than admitted at zero estimated risk |

---

## Required before autonomous broker Demo (P1)

### EXE-01

| Field | Value |
|---|---|
| **Title** | Reduction client-request IDs are not stably idempotent, unlike entry client order IDs |
| **Severity** | Medium |
| **Confidence** | Confirmed |
| **Subsystem** | LiveTrading Execution |
| **Files / methods** | `LiveTrading/Execution/LiveExecutionGateway.cs` — `CreateReductionId` (hashes `StrategyId \| PositionId \| BrokerTradeId \| Quantity \| quantity-to-reduce \| position.UpdatedAt`) vs `CreateClientOrderId` (hashes only stable decision-identity fields); `LiveOrderPositionRegistry.ApplyManagementObservation` bumps `UpdatedAt` on every evaluation frame |
| **Observed** | `UpdatedAt` changes on essentially every quote tick / analysis bar, independent of whether a reduction is being attempted, because `ApplyManagementObservation` runs unconditionally before dispatching on the recommended action |
| **Expected** | A retried reduction (after a failed/timed-out/`Unknown`-certainty attempt) produces the same client-request ID so the broker can deduplicate it |
| **Failure scenario** | A `ReducePositionAsync` call times out or returns `Unknown`; the same logical reduction is recommended again on a later cycle; `CreateReductionId` produces a **different** ID because `UpdatedAt` moved, defeating broker-side dedup and risking a duplicate reduction |
| **Why it matters** | Directly relevant to the audit's "confirm idempotent transaction handling" requirement — entries already do this correctly, reductions do not |
| **Recommended correction** | Derive `CreateReductionId` from the reduction's own stable identity (e.g. its `StageId` plus position identity), mirroring the entry path's approach |
| **Required test** | Two calls to `CreateReductionId` for the same logical reduction, with `position.UpdatedAt` advanced between them (simulating an intervening evaluation frame), produce the same ID |

---

### LEAK-01

| Field | Value |
|---|---|
| **Title** | Calibration/meta-model artifact production fits directly on the full backtest window — no purge or held-out split, despite existing walk-forward infrastructure |
| **Severity** | Medium |
| **Confidence** | Confirmed |
| **Subsystem** | QuantResearchRunner / Calibration |
| **Files / methods** | `QuantResearchRunner/Program.cs` — `RunCalibrateSetupsAsync`, `RunCalibrateManagementAsync`, `RunCalibrateMetaModelAsync`, each calling `ConfidenceCalibrator.Calibrate` / `TradeManagementCohortAnalyzer.Analyze` / `MetaModelCalibrator.Calibrate` on the full `plan.From..plan.To` window; contrast `QuantResearch/Validation/WalkForward.cs` (`WalkForwardPlanner`, `PurgedTimeSeriesCrossValidator`) |
| **Observed** | `trainingFrom`/`trainingTo` are recorded as metadata labels only, not used to hold out any portion of the data; `PurgedTimeSeriesCrossValidator` has zero call sites outside test code. One artifact is stored with the provenance label `"walk-forward plan ..."` despite no walk-forward split having actually been applied. |
| **Expected** | Calibration artifacts consumed by live/simulator trading should be produced with a purge gap and/or held-out evaluation slice, using the infrastructure that already exists for exactly this purpose |
| **Failure scenario** | An operator promotes a setup-calibration or meta-model artifact that looks well-calibrated because it was evaluated on the same data it was fit on; live/forward performance underperforms the backtest that justified promotion |
| **Why it matters** | Exactly the "interface/class exists but isn't wired into the capability that needs it" pattern this audit is required to flag rather than credit; corrects the first-pass report's more optimistic "Guarded tooling" leakage verdict |
| **Recommended correction** | Route the three `RunCalibrate*Async` commands through `WalkForwardPlanner`/`PurgedTimeSeriesCrossValidator` before fitting; fix the misleading "walk-forward plan" provenance label until this is done |
| **Required test** | A calibration artifact's reported training window and evaluation window do not overlap (or overlap only within a documented, intentional purge/embargo tolerance) |

### LIVE-03

| Field | Value |
|---|---|
| **Title** | Live CurrencyStrength hard-null — sim/live feature drift |
| **Severity** | High (parity) |
| **Confidence** | Confirmed |
| **Subsystem** | LiveTrading Agents |
| **Files / methods** | `LiveTrading/Agents/AgentSupervisor.cs` — `CurrencyStrength = null` |
| **Observed** | No cross-market strength in live decisions / meta-label CS differential |
| **Expected** | Wire coordinator or disable feature in both environments with matching policy hash |
| **Failure scenario** | Shadow outcomes not comparable to research |
| **Recommended correction** | Live `CrossMarketAnalysisCoordinator` or explicit policy flag forcing null on both |
| **Required test** | Parity test with CS enabled in sim and live context builder |

---

### TM-01

| Field | Value |
|---|---|
| **Title** | Live management fixed-stop-only by default while sim uses full TM |
| **Severity** | High (ops / parity) |
| **Confidence** | Confirmed |
| **Subsystem** | LiveTrading Management / TradeManager |
| **Files / methods** | `LiveTradingHost/appsettings.json`; `LivePositionManagementService` suppress paths; `LiveBrokerCapabilityMatrix` |
| **Observed** | BE/trail/partials suppressed unless certification flags |
| **Expected** | Documented mode; promote only fixed-stop policies **or** certify and enable |
| **Failure scenario** | Backtest expectancy with trails does not transfer |
| **Recommended correction** | Status surface “ManagementMode=FixedInitialStop”; dual validation suites |
| **Required test** | Flags false → no amend/partial broker calls |

---

### TM-02

| Field | Value |
|---|---|
| **Title** | Profit floor / PreferFloorStopBeforeHardExit ineffective without stop amendment |
| **Severity** | Medium |
| **Confidence** | Medium–High |
| **Subsystem** | TradeManager / LiveTrading |
| **Files / methods** | `PositionManagementOptions.PreferFloorStopBeforeHardExit`; live suppress amend path |
| **Observed** | Floor logged but stop not moved live |
| **Expected** | Convert floor breach to Exit when amend unsupported |
| **Failure scenario** | Giveback beyond intended floor |
| **Recommended correction** | Branch on capability matrix / flags |
| **Required test** | DynamicStop disabled + floor breach → full exit or explicit non-enforcement |

---

### LIVE-04

| Field | Value |
|---|---|
| **Title** | Decision pipeline safety controller not shared with account safety |
| **Severity** | Medium |
| **Confidence** | Confirmed |
| **Subsystem** | LiveTradingHost / TradingCore |
| **Files / methods** | `StrategyDecisionPipelineFactory` creates new controller if `sharedSafetyController` null; Program does not pass account controller into agent factory |
| **Observed** | Equity-protection multipliers at decision time may not match account `TradingSafetyController` |
| **Expected** | One account authority for safety state |
| **Failure scenario** | Decision-time risk mult out of sync with pause/trip state (mitigated later by runtime gates) |
| **Recommended correction** | Pass host `ITradingSafetyController` into live pipeline construction |
| **Required test** | Trip account safety → pipeline rejects new buys with same snapshot |

---

### LIVE-05

| Field | Value |
|---|---|
| **Title** | Management runtime state not fully restored after restart |
| **Severity** | Medium |
| **Confidence** | High |
| **Subsystem** | LiveTrading Persistence / Management |
| **Files / methods** | `LivePositionManagementService` `_states`; `LiveEngineCheckpoint` lacks full TM stage sets |
| **Observed** | Process-local counters/stages lost on restart |
| **Expected** | Restore MFE/MAE/stages or rehydrate conservatively without duplicate partials |
| **Failure scenario** | Re-fire reductions or miss stagnation exits |
| **Recommended correction** | Checkpoint essential TM fields or rebuild from journal with idempotent keys |
| **Required test** | Restart mid-trade: no double partial; stages consistent |

---

### REC-01

| Field | Value |
|---|---|
| **Title** | Unowned broker positions pause entries but remain unmanaged |
| **Severity** | Medium |
| **Confidence** | Confirmed (by design) |
| **Subsystem** | LiveTrading Reconciliation |
| **Files / methods** | `LiveBrokerReconciler`; `RecoverAsync` |
| **Observed** | No auto-adopt; operator must resolve |
| **Expected** | Explicit operator adopt/flatten playbook + UI |
| **Failure scenario** | Open risk sits outside TM |
| **Recommended correction** | Control API “adopt with strategy id” / force flatten unowned |
| **Required test** | Startup with extra broker position → paused + listed difference |

---

### RSK-02

| Field | Value |
|---|---|
| **Title** | Opposite-direction positions allowed by default |
| **Severity** | Medium |
| **Confidence** | Confirmed |
| **Subsystem** | RiskManager |
| **Files / methods** | `PreTradeRiskManager.Evaluate` — pyramiding check same-side only |
| **Observed** | Hedge/flip not blocked |
| **Expected** | `AllowOpposingPositions` default false for first production mode |
| **Failure scenario** | Accidental hedge / attribution mess |
| **Recommended correction** | Config flag default false; require flatten-first |
| **Required test** | Opposite side rejected when flag false |

---

### CFG-01

| Field | Value |
|---|---|
| **Title** | TradingConditions.Enabled defaults differ across surfaces |
| **Severity** | Medium |
| **Confidence** | Confirmed |
| **Subsystem** | RiskManager / BacktestRunner / DashboardLive |
| **Files / methods** | `TradingConditionOptions.Enabled` default false; Dashboard `TradingConditionsEnabled = true`; CLI `--trading-conditions` opt-in |
| **Observed** | Research runs incomparable |
| **Expected** | Align with adjustment doc (true) or document opt-in everywhere |
| **Failure scenario** | Overstated opportunity without session/spread |
| **Recommended correction** | Default true; CLI `--no-trading-conditions` |
| **Required test** | Default CLI/runtime has Enabled true |

---

### AUTH-01

| Field | Value |
|---|---|
| **Title** | Manual-approval/control API, several read/status endpoints, and the LiveTradingHost SignalR hub lack authentication |
| **Severity** | Medium |
| **Confidence** | Confirmed — independently found twice (Flow C/D review and the safety/operational review), from two separate passes over the API surface |
| **Subsystem** | LiveTradingHost / LiveTrading control API |
| **Files / methods** | LiveTradingHost control/approval endpoints; status/read endpoints; SignalR hub registration |
| **Observed** | No auth/authz check gates access to manual-approval actions, several read/status endpoints, or the SignalR hub |
| **Expected** | Dangerous or sensitive endpoints require authenticated, authorized, audit-logged access; loopback-only exemption should be explicit and narrow |
| **Failure scenario** | Any process with network access to the host can read live trading state, or (for control endpoints) potentially approve/reject/influence pending trades without identity or audit trail |
| **Why it matters** | Control-plane/information-disclosure exposure — not an execution-safety defect on its own (writes still require the separate Demo-only broker guard), but a real gap in the audit's required "dangerous remote APIs have auth/authz/audit-identity/confirmation" check |
| **Recommended correction** | Add authentication (e.g. the existing `ControlToken` mechanism, extended to cover read endpoints and the hub) with per-action authorization and audit logging |
| **Required test** | Unauthenticated request to a control/approval/status endpoint or the hub is rejected outside the explicit loopback exemption |

---

### CFG-02

| Field | Value |
|---|---|
| **Title** | DelayEntry is discard-with-label, not deferred revalidation |
| **Severity** | Medium |
| **Confidence** | Confirmed |
| **Subsystem** | RiskManager / TradingCore |
| **Files / methods** | `TradingConditionFilter`; `SafeTradingPipeline` DelayEntry → return |
| **Observed** | No persistence/retry |
| **Expected** | Queue + revalidate **or** rename to avoid false claims |
| **Failure scenario** | Operators expect later entry that never comes |
| **Recommended correction** | Document as skip; optional deferred store later |
| **Required test** | SessionNotAllowed never auto-enters later without new agent signal |

---

## Valuable later (P2)

### AGT-01

| Field | Value |
|---|---|
| **Title** | Regime EntryProfileId / ManagementProfileId telemetry-only |
| **Severity** | Low / Informational |
| **Subsystem** | Agent |
| **Files** | `RegimeRoutingPolicy.cs` |
| **Correction** | Wire into TM profiles or document as telemetry |

### AGT-02

| Field | Value |
|---|---|
| **Title** | `DetectSide`'s hard RSI band can reject valid trend-continuation setups |
| **Severity** | Medium (decision quality, not safety) |
| **Subsystem** | Agent |
| **Files** | `Agent/Strategies/ProgressiveStrategyBase.cs` — `DetectSide` (Buy requires RSI∈[45,75), Sell requires RSI∈(25,55], applied after all other evidence/gating passes) |
| **Observed** | Used for every timeframe role (primary/secondary/setup/confirmation/entry) and open-position reversal detection; an RSI reading outside the band returns `null` regardless of how strong structural/DMI/price-action evidence is, with no distinct reason code |
| **Correction** | Give it its own reason code for visibility/independent tuning, or relax it when `EnableDmiConfirmation` already confirms strong directional conviction |

### AGT-03

| Field | Value |
|---|---|
| **Title** | Channel-sourced trailing-stop candidates have no independent off-switch |
| **Severity** | Low/Medium |
| **Subsystem** | TradeManager |
| **Files** | `TradeManager/StructureBasedTradeManager.cs` — `FindStructuralCandidate` (channel candidates scored alongside swing/zone, de-weighted via `typeBonus` but not excluded) |
| **Observed** | Channel/trendline levels are already excluded from **initial** stop/target selection in `ImprovedProgressiveAgent` (per the user's standing judgment that RANSAC channel detection is unreliable) but ride along with the **trailing**-stop path once `DynamicStopReplacementEnabled` is certified, under the same flag as swing/zone trailing |
| **Correction** | Add a separate configuration toggle to exclude channel-sourced trailing candidates before certifying dynamic stop replacement generally |

### ARC-04

| Field | Value |
|---|---|
| **Title** | Dead broker-prefix branch in `RiskManager/Risk/InstrumentRiskSpec.cs` hardcodes broker/exchange names in a core project |
| **Severity** | Low/Medium |
| **Subsystem** | RiskManager |
| **Files** | `RiskManager/Risk/InstrumentRiskSpec.cs` — `ForInstrument` (~49-63) |
| **Observed** | Branches on `"FX:"`/`"CRYPTO:"`/`"BINANCE:"`/`"OANDA:"` prefixes, but every branch (including the fallback) returns the identical `UnitNotional` value — no behavioral effect today, but hardcodes a broker name (and an exchange this codebase has no adapter for) inside a core, environment-neutral project |
| **Correction** | Implement genuine broker-agnostic asset-class detection (an explicit `AssetClass` supplied by the adapter layer) or delete the dead branch and keep `ForInstrument` as an explicit `UnitNotional`-only implementation until real differentiation is built |

### PTF-03

| Field | Value |
|---|---|
| **Title** | Partial allocation binary search not max-feasible quantity |
| **Severity** | Low |
| **Subsystem** | PortfolioManager `CapitalAllocator` |
| **Correction** | Search for largest feasible step |

### ARC-01

| Field | Value |
|---|---|
| **Title** | Dual ExecutionCoordinator (`Agent.Execution` vs `ExecutionManager`) |
| **Severity** | Low |
| **Correction** | Delete or quarantine unused Agent.Execution path |

### ARC-02

| Field | Value |
|---|---|
| **Title** | TradingPolicies not in TradingHub.slnx |
| **Severity** | Low |
| **Correction** | Add project to solution for discoverability |

### ARC-03

| Field | Value |
|---|---|
| **Title** | Dual simulation runners (SimulationRunner vs StrategySimulationSession path) |
| **Severity** | Low / Medium maintenance |
| **Correction** | Mark legacy; single product path |

### LIVE-06

| Field | Value |
|---|---|
| **Title** | Live portfolio forces regime risk multiplier to 1 |
| **Severity** | Medium (parity) |
| **Subsystem** | LiveOpportunityCoordinator |
| **Correction** | Pass through decision RegimeRiskMultiplier when policy enabled |

### POS — positive controls (retain)

| ID | Note |
|---|---|
| POS-01 | Completed-candle-only annotator + ConfirmedAt swings |
| POS-02 | Sequence-based no same-bar fill |
| POS-03 | Stop-first OCO default |
| POS-04 | Risk multipliers capped ≤1 |
| POS-05 | Empty AllowedSessions fail-fast when enabled |
| POS-06 | Shadow agents cannot order; Practice-only writes |
| POS-07 | Live catch-up does not publish historical decisions |
| POS-08 | Live portfolio marks QuantityIsPortfolioApproved |
| POS-09 | Spread ATR defaults 0.25/0.50 centralized |
| POS-10 | Loopback + Demo write gates |

---

## Leakage status board

| Topic | Status |
|---|---|
| Unfinished candles | Not present |
| Pivot confirmation timing | Not present |
| Same-bar signal fill | Not present |
| Same-bar OCO ambiguity | Possible but guarded |
| Meta-label future features | Not present |
| Calibration/meta-model train-test split | **Confirmed leakage (`LEAK-01`)** — corrects earlier "Guarded tooling" verdict; walk-forward/purged-CV infra exists but is unwired from artifact production |
| Portfolio future-frame rank | Not present |
| Live catch-up trade emission | Not present |
| Sim vs live gate drift | **Confirmed** (LIVE-01..03, `CAL-01`) |
| DelayEntry semantics | Confirmed misnomer |

---

## Testing gaps (priority)

1. **`dotnet test` has now been re-run after PERSIST-01/PROMO-01 fixes**: 871/872 runnable tests pass. Only remaining failure is TEST-01 (pre-existing test-fragility, not a safety regression, not fixed this pass since it wasn't in scope).
2. ~~Checkpoint round-trip with a non-empty `ActivatedTierIds`~~ — **closed**: `Checkpoint_RoundTripsTransactionCursorAndAppliedEventIds`/`Checkpoint_HashMismatchIsQuarantined` now pass (PERSIST-01 resolved).
3. ~~`TradingPolicyProfile`/`ProgressiveStrategyOptions` JSON round-trip~~ — **closed**: `SimulatorPolicy_RoundTripsAsEnvironmentNeutralProfile` now passes (PROMO-01 resolved).
4. Shared portfolio quantity authority (PTF-01)
5. Missing-stop heat fail-closed (PTF-02)
6. Live trading-condition DI wiring (LIVE-01)
7. Live annotation options wiring (LIVE-02)
8. Live calibrated management reaches a real (non-`"Live"`) volatility bucket (CAL-01)
9. Duplicate manual approval / expiry / fingerprint
10. Unknown broker submission + restart after fill
11. Reconnect catch-up emits zero decision epochs
12. Policy hash mismatch blocks activation
13. Lease conflict second process
14. FixedQuantity incomplete inputs rejected live, including the missing-FX-rate case specifically (RSK-01)
15. Reduction retry after `Unknown`/timeout produces the same client-request ID (EXE-01)
16. Management flags false → no amend/partial
17. Opposing position policy
18. Calibration artifact training/evaluation windows do not overlap (LEAK-01)
19. Unauthenticated request to control/approval/status endpoints and the SignalR hub is rejected (AUTH-01)
20. Behavioral (not literal-text) coverage for the Demo-only write guard (TEST-01)
21. Identical replay → identical decisions (determinism soak)
22. **New (defense-in-depth, not required to close PERSIST-01/PROMO-01)**: explicit round-trip test with a populated `ActivatedTierIds`; explicit test asserting `RegimeInterval = null` survives round trip; explicit test asserting `RegimeInterval` set to a non-default value survives round trip.

---

## Severity tally

| Severity | Count (actionable) |
|---|---|
| Critical | 0 (BUILD-01 resolved earlier this revision) |
| High | 8 (CAL-01 remains High; PERSIST-01 and PROMO-01 resolved this pass — see their entries) |
| Medium | 13 (EXE-01, LEAK-01, AGT-02, AUTH-01) |
| Low / Medium | 2 (AGT-03, ARC-04) |
| Low / Info | 6+ positives (includes TEST-01) |
| **Resolved this pass** | **2 (PERSIST-01, PROMO-01)** — see "Resolved since the first pass" section for BUILD-01, and their own entries above for fix details |

---

*Register ends. Implement P0 before any Practice write; P1 before autonomous Demo.*
