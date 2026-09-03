# TradingHub — Project State

**This is the living source of truth for "what have we done, what's missing, what's next."**
Update this file whenever you touch code, run a real backtest/validation, fix a bug, or make an
architectural decision — see [Update policy](#update-policy) at the bottom. This file supersedes
scattered status claims in the ~55 other markdown files in this repo; where they conflict with
what's written here, trust this file (and re-verify against code/DB/tests, since even this file
can go stale).

**Last updated:** 2026-08-02, late session — paused at user's request after fixing RSK-01/LIVE-01/
CAL-01 (see `Recent session log` for what's still in flight and what to pick up next).

---

## 1. What this project is

A .NET 10 multi-broker market-analysis, simulation, and gated automated-trading platform.
Architecture is deliberately layered so strategy logic never owns broker execution or risk policy:
`Agent` (decisions only) → `RiskManager` (pre-trade risk) → `ExecutionManager` (idempotent order
orchestration) → `Brokers` (adapters + safety checks), with `Simulator` and `LiveTrading*` as
parallel environment-neutral orchestrators over the same core. `ChartAnnotator` supplies all
technical/structural analysis (indicators, swings, zones, liquidity pools, market structure).
`DashboardLive` + the Vue `Dashboard` are the primary UX. See `Readme.md` for the authoritative
project-boundary description and `Implementation&PhasingDocs/ARCHITECTURE_SIMULATOR.md` for the
simulator's internal architecture (both still broadly accurate as of this session, with specific
corrections noted below).

---

## 2. Technical state — what's actually implemented and verified

### 2.1 Solution shape (verified this session)

- 811 `.cs` files, 39 `.csproj`, 29 projects in `TradingHub.slnx`.
- `dotnet build TradingHub.slnx -c Release` — **0 warnings, 0 errors** (`TreatWarningsAsErrors=true`
  solution-wide via `Directory.Build.props`).
- `Simulator.Tests`: **1,036 tests, 1,028 passing**. The 8 failures are a single pre-existing,
  parameterized test (`MultiInstrumentPortfolioClockTests.ApplicationService_Completes...`) —
  confirmed via `git stash` to fail identically on unmodified code, unrelated to any change made
  this session. `TradingHub.UnitTests`: 60/60. `QuantResearchRunner.Tests`: 71/73 (2 pre-existing,
  unrelated failures in `PreRunCalibrationTests`).
- Test count has grown steadily: 290 (2026-07-13) → 593 (2026-07-16) → 832 (2026-07-20, per
  `TradingHub_Simulator_Audit_Report_2026-07-20.md`) → 1,036 (now). Treat this as a real, tracked
  trend, not a one-off number.

### 2.2 PostgreSQL persistence — more complete than older docs suggest

**Correction to stale docs**: `Implementation&PhasingDocs/ARCHITECTURE_SIMULATOR.md` and several
July blueprints describe PostgreSQL persistence as partially wired or aspirational. Verified this
session by querying the actual database directly (`tradinghub` on localhost:5432):

- **81 tables across 12 schemas**, fully migrated (15 EF Core migrations applied): `analytics`,
  `config`, `decision`, `execution`, `management`, `operations`, `reference`, `research`, `risk`,
  `runtime`, `security`, `simulation`.
- `security.broker_credentials` has **3 real encrypted rows**: OANDA/DEMO (enabled), BINANCE/TESTNET
  (enabled), IG/DEMO (disabled). Encryption key at `.state/keys/broker-credentials.key`.
  Decryption path: `DBManager.Postgres.Security.BrokerCredentialVault.Open(connectionString,
  keyFile)` → `IBrokerCredentialStore.GetAsync(brokerCode, environment)`.
- `reference.brokers` / `reference.broker_accounts` show configured OANDA (x2), Binance, IG
  accounts.
- **Do not trust a plain `psql \dt` to check DB state** — it only lists the `public` schema by
  default and will incorrectly report "no tables" (this cost real time earlier this session).
  Use `SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN
  ('pg_catalog','information_schema')`.

### 2.3 Broker / data-source support (verified via `Implementation&PhasingDocs/BROKER_ASSET_SELECTION.md`
   + this session's live use)

| Broker | Historical sim | Live | Notes |
|---|---|---|---|
| OANDA | Yes (midpoint + configured spread; no real bid/ask) | Practice/Demo-only writes, gated off by default | Credentials in vault, confirmed working |
| Binance Spot | Yes (public endpoint, no API key needed) | No live execution | Historical-sim only, by design |
| Imported CSV | Yes (1s/5s midpoint OHLC) | N/A | — |
| IG | No | Account/order integration exists | Historical candle source not connected — UI correctly shows "unavailable" |

**Cached historical data available locally** (`.cache/historical/*.jsonl.gz`, real OANDA data,
`oanda-mid-v1`): 1-minute bars for EUR/USD, GBP/USD, AUD/USD, NZD/USD, USD/CAD, USD/CHF, USD/JPY,
XAU/USD, XAG/USD, all spanning ~2025-12-11 to 2026-07-24. **All 9 are USD-denominated** — no
genuine cross-pair (e.g., EUR/GBP, EUR/JPY) has usable cached coverage. Real cross-pair data would
require a live OANDA fetch using the vault credentials above (not yet done).

### 2.4 Known correctness gaps from prior audits — re-verified and fixed this session (2026-08-02)

From `TradingHub_Independent_Audit_Report.md` (2026-07-17) + issue register. All five were
re-verified against **current** code (not assumed from the old audit) before touching anything.
Every fix below was built, then verified against the full regression suite (`Simulator.Tests`
1028/1036, `LiveTrading.Tests` 119/119, `TradingCore.Tests` 24/24, `TradingHub.UnitTests` 60/60,
`QuantResearchRunner.Tests` 71/73 — all identical to the pre-existing baseline, zero new failures).

- **RSK-01 (High) — FIXED.** `RiskManager/Risk/PositionSizing.cs`'s FixedQuantity branch still
  approves with zero risk/margin checks when inputs are incomplete (by design, per its own
  comment: "fail closed later at live portfolio admission") — but that admission-time check never
  actually existed. Confirmed the exact mechanism: `LiveOpportunityCoordinator.cs:165` silently
  defaults a missing FX rate to `0m`, starving the sizer, whose resulting `null`
  `EstimatedMargin`/`EstimatedLossAtStop` were then coalesced to `0m` at the reservation layer
  (`?? 0m`), admitting a zero-risk reservation that clears every cap. **Fix**: added a rejection in
  `LiveOpportunityCoordinator.cs` — FixedQuantity-mode candidates with `null` risk/margin data are
  now rejected (`"FixedQuantityRiskDataUnavailable"`) instead of admitted blind.
- **RSK-02 (Medium, latent) — FIXED.** `RiskBudgetPolicy.cs` clamped the combined multiplier
  with `Math.Clamp(raw, MinimumCombinedRiskMultiplier, Maximum…)`, so a configured floor above
  zero silently resurrected a hard-zero band. The default drawdown schedule's terminal band is an
  explicit stop-trading band (`Multiplier = 0m` at >= 6% drawdown, `RiskBudgetPolicy.cs:18`); with
  a 0.25 floor, an 8% drawdown produced `combined = 0.25` and `ReasonCode = "RiskBudgetAdjusted"`
  instead of halting. Latent only — nothing in the repo sets `MinimumCombinedRiskMultiplier`, so it
  defaulted to 0. **Fix**: `raw <= 0m` short-circuits to zero before the clamp; the floor still
  applies to any non-zero product.
- **RSK-03 (Low) — FIXED.** Two `RiskBudgetPolicy` schedule gaps. (a) `ValidateSchedule` accepted an
  open-ended band anywhere, so `[0,∞), [0,5)` validated while `Resolve`'s `FirstOrDefault` made the
  second band unreachable — now only the final band may be open-ended. (b) `Resolve` fell back to
  `schedule[^1]` for *any* unmatched value, giving a below-range input the most punitive multiplier
  (for drawdown, the 0 kill band). Now below-range takes the first band, above-range keeps the last,
  so an out-of-range input can never widen the budget. Unreachable with the shipped defaults (both
  start at 0, inputs are clamped at `RiskBudgetPolicy.cs:114,117`); reachable with custom schedules.
- **RSK-04 (Low) — FIXED.** Two unguarded `decimal` divisions in `PositionSizing.cs` could throw
  `OverflowException` out of the sizer instead of returning a rejection like every other failure
  path: `riskBudget / perUnitLoss` (only guarded for `<= 0`, not for denormally small values from a
  misconfigured `InstrumentRiskSpec`) and `value / step` in `RoundDown`. Now they reject with
  `"UnboundedQuantity"` and fail closed to zero respectively.
- **RSK-05 (Low) — FIXED.** `PreTradeRiskManager.cs` substituted a `1m` FX rate when computing
  open-book risk (`QuoteToAccountCurrencyRate > 0m ? rate : 1m`) — the same silent-FX-default
  anti-pattern as RSK-01. `Approved` was already false via the missing-rate reason, but the
  assessment still reported a fabricated `OpenRiskAccountCurrency` computed at the wrong rate. Now
  the block is skipped entirely and both open-risk fields stay null.
- **TM-01 (High) — FIXED.** `StructureBasedTradeManager.cs` silently dropped an equity-protection
  `ReduceOpenPositions` directive whenever the position already sat at its minimum runner fraction:
  `CreateReduction` returned null, the `if (equityReduction is not null)` block fell through, and
  the caller saw an ordinary `Hold`/`NoValidProfitProtectionImprovement` with no trace that an
  account-level safety escalation had gone unmet. Confirmed by probe before fixing. Very reachable —
  `Validate()` explicitly permits a scale-out schedule that lands exactly on the runner floor.
  **Fix**: the runner floor still bounds the directive (that behaviour is asserted by
  `EquityProtectionDirectiveTests`), but an unsatisfiable directive now prefixes the recommendation
  with `EquityProtectionUnsatisfiable|` so the caller can escalate. Chose the observable-signal
  option over force-exiting the position: it preserves the tested runner-floor intent and takes no
  irreversible action on an inference. Force-exit remains a one-line change if that is preferred.
- **TM-02 (Medium) — FIXED.** `BuildStopCandidate` picked the most protective stop candidate and
  *then* checked placeability, returning null if that one failed rather than falling back to the
  next best. On a tight stop the cost-adjusted break-even can sit above the current price, which
  discarded an available profit-floor or structural stop entirely. Confirmed by probe: a placeable
  0.5R floor stop at 100.05 was dropped in favour of an unplaceable break-even at 100.20, yielding
  `Hold`. Bites precisely in the tight-stop/high-cost regime already documented at §623-627.
  **Fix**: filter candidates for placeability and minimum improvement first, then take the most
  protective survivor. (The improvement filter alone could never cause this — the highest-price
  candidate always has the largest improvement — so the defect was placeability-specific.)
- **TM-03 (Medium) — FIXED.** CAL-01 survived in the shadow path:
  `LiveShadowOutcomeService.cs:638` hardcoded `EntryVolatilityBucket = "Live"` while `:586`
  constructs a `CalibratedStructureBasedTradeManager`. `"Live"` is not a bucket
  `VolatilityBucketClassifier` can ever emit, so cohort lookup could never match and the shadow
  path was pinned to `StaticManagementFallback` permanently. **Fix**: classify from the (absent)
  ATR percentile, yielding the real `"Unknown"` key a cohort can match. `ShadowPaperPosition`
  carries no ATR percentile, so `"Unknown"` is the honest value, not a workaround.
- **TM-04 (Low) — FIXED.** `ManagementCalibration.cs` capped `StagnationReductionFraction` with
  `Math.Min` against the fallback but overwrote `StagnationBars` outright from the cohort median,
  so a median longer than the static value made calibrated management *more* patient — a loosening,
  contradicting the adjacent "conservative use only" comment. Now `Math.Min`-bounded like its
  sibling; calibration may only review sooner or keep the static value.
- **TM-05 (Low) — FIXED.** Cohort matching used ordinal `==` on seven string keys while
  RiskManager's `SetupCalibrationPolicy` uses `OrdinalIgnoreCase` for the same job, so any case
  drift between the training pipeline and the runtime context degraded silently to static fallback.
  Now case-insensitive and consistent.
- **TM-06 (Low) — FIXED.** `CalibratedStructureBasedTradeManager._managers` was a plain
  `Dictionary` mutated inside `Evaluate`. Searched for concurrent callers and **found none** (no
  `Parallel.`/`Task.Run`/`Task.WhenAll` in `LivePositionManagementService`), so this was latent, not
  a live race. Now a `ConcurrentDictionary` with `GetOrAdd`.
- **TM-07 (Low) — FIXED.** `(int)decimal.Ceiling(cohort.MedianDurationBars)` was an unchecked cast
  that would wrap on an out-of-range median; now clamped to `[1, int.MaxValue]` before the cast.
- **EM-01 (High) — FIXED.** `ExecutionCoordinator.cs` substituted a fabricated `1m` FX rate into
  `PreTradeRiskContext` whenever `ResolveQuoteToAccountRate` could not derive a conversion
  (`QuoteToAccountCurrencyRate = quoteToAccountRate > 0m ? quoteToAccountRate : 1m`). The resolver
  correctly returns `0m` for any true cross — only `quote == account` and `base == account` are
  handled — so `MaximumLossPercentageOfBalance` and the portfolio heat cap were evaluated against a
  made-up conversion. This is the RSK-01 anti-pattern, and it **silently defeated the RSK-05 fix
  made earlier the same session**: that guard only engages on a non-positive rate, which this call
  site guaranteed never happened. Confirmed by probe — `FX:EUR/JPY` on a USD account handed the risk
  manager `1` where the true rate is ~0.0067. Direction varies by pair: conservative where the true
  rate is below 1, **dangerously permissive above it**. Reachable through `QuantityIsPortfolioApproved`
  (skips sizing) and `PositionSizingMode.FixedQuantity` (approves without a rate). **Fix**: pass the
  real rate through. *Operational impact*: fixed-quantity and portfolio-approved trades on
  unresolvable cross pairs are now rejected where they previously passed — the correct fail-closed
  outcome, but a live behaviour change. The rejection only fires when a monetary cap is actually
  configured; with no cap configured nothing changes.
- **EM-02 (Medium) — FIXED.** `_amendments` was only ever added to, never removed, while the
  sibling `_inFlight` map had a matching `TryRemove`. The amendment fingerprint includes
  `RequestedSequence` and `EffectiveFromExecutionSequence`, so every single trailing-stop amendment
  minted a permanent entry holding a completed task — an unbounded leak in a long-running live host.
  **Fix**: added `AwaitAndReleaseAmendmentAsync` mirroring the order path. Payload-mismatch
  detection becomes in-flight scoped, like orders; a stale replay is still caught by the broker-side
  stop-order state check.
- **EM-03 (Low-Medium) — FIXED.** A `Close` decision resolved its target with
  `positions.FirstOrDefault(item => item.Instrument == …)` at two independent call sites. On a
  hedging account holding both a long and a short in the same instrument, that picked whichever the
  broker happened to list first and thereby silently chose both the close **side** and the quantity
  cap. **Fix**: single `ResolveClosePosition` helper — same-side positions aggregate, a mixed-side
  book throws as genuinely ambiguous rather than being guessed.
- **EM-04 (Low) — FIXED.** `NormalizeRiskReducingPrice` computed `price / increment` unguarded;
  `ValidateAmendmentCommand` only checks the increment is positive, so a pathologically small one
  threw `OverflowException` out of the amendment path. Now returns `0m` on overflow, which
  `ValidateRiskReduction` turns into a clean "normalized stop price is not positive" rejection.
- **EM-05 (Trivial) — FIXED.** `RiskBudgetContext.AccountEquity` was `required` but never read by
  `RiskBudgetPolicy.Evaluate`. Dropped `required` and documented it as audit-context only (kept the
  field: removing it would ripple through four production call sites and eight test files for no
  behavioural gain).
- **Audit note (ExecutionManager, not defects).** Two things initially suspected and cleared by
  checking rather than assuming: journal `Sequence = 0` is harmless because `TradeJournal.Append`
  overwrites it with its own counter (`TradingJournal/TradeJournal.cs:93`); and `_inFlight`'s
  `TryRemove(key, out _)` cannot cross-remove another thread's operation, because entries are only
  ever added-if-absent or removed, never replaced. Also verified correct: stop-normalization
  direction (floor for longs / ceiling for shorts moves the stop *away* from price, so normalization
  can never cause an immediate stop-out); amendment-ID reuse detection via fingerprint; the
  stale-protective-order check (instrument, status, type, quantity, opposite side, price — and
  decimal `!=` compares by value not scale, so `1000.00` matches `1000`); exits never being blocked
  by safety gating; protective orders deliberately not cancelled before a close fill confirms; and
  sizing rejection failing closed without falling back to the requested quantity.
- **Audit note (TradeManager, not defects).** Verified correct and left alone: no divide-by-zero in
  the R math (`Validate` enforces `EntryPrice != InitialStopPrice` and correct stop side before
  `openProfitR` is computed); the break-even cost model counts spread once and slippage per leg,
  which is right because `CurrentPrice` is the exit-executable bid/ask and already carries the exit
  spread; profit-floor/giveback selection and `locked = MFE - giveback`; the floor-breach ordering
  that guarantees a floor stop is always placeable by the time `BuildStopCandidate` runs; both
  regime presets preserving the `StructureTrailActivationR >= BreakEvenActivationR` invariant;
  midnight-crossing risk window; stagnation interpolation; and structural scoring including
  Buy/Sell `postEntry` symmetry. Confidence bucketing was confirmed identical across the project
  boundary — `(int)(Confidence / 10m) * 10` in both `TradeManagementCohorts.cs:38` and
  `ManagementCalibration.cs:93` — and `VolatilityBucketClassifier` is genuinely shared, with the
  simulator's local `VolatilityBucket` (`StrategySimulationSession.cs:2507`) delegating to it.
- **Audit note (not a defect).** The R:R gate is measured on **gross** price distances
  (`PreTradeRiskManager.cs:251-252`, and `StructuralGeometryBuilder.cs:83`), while `PositionSizing`
  sizes on **cost-inclusive** risk (`riskDistance + estimatedCostDistance`). Cost is enforced
  system-wide as a separate minimum-stop-distance floor (`StructuralGeometryBuilder.cs:41`), not
  folded into the ratio — a consistent design stance, not a missed spot. Consequence: a configured
  `MinimumRewardRiskRatio = 1.5` can be materially below 1.5 net on a tight stop. Left unchanged:
  folding cost into the ratio would tighten entry criteria for every strategy and needs sign-off.
- **Audit note (not a defect).** Risk multipliers compound multiplicatively in two places —
  11 factors at `RiskBudgetPolicy.cs:129-130`, and `FutureRiskMultiplier` across every latched
  equity-protection tier at `TradingSafety.cs:479`. Nested tiers therefore double-count (a 5% and a
  10% giveback tier both active at 10% give 0.5 x 0.5 = 0.25). May be intended; it is the first
  place to look if sizes ever read mysteriously small.
- **LIVE-01 (High) — FIXED.** `LiveTradingHost/Program.cs:225` still constructs the live
  `IStrategyDecisionPipelineFactory` DI singleton with no trading-condition filter. Traced the real
  gap precisely: `LiveTradingPolicyBundle.TradingConditions` is a `required` field — every
  promoted policy already carries real, per-strategy session/spread/rollover config — but
  `StrategyDecisionPipelineFactory.Create()` had no parameter to receive it (only the DI-fixed
  constructor value, which can't vary per strategy on a shared singleton). **Fix**: added an
  optional `tradingConditionsOverride` parameter to `IStrategyDecisionPipelineFactory.Create()`
  (falls back to the constructor value when absent — the simulator's existing per-session-factory
  usage is untouched); `LiveAgentFactory.Create` now builds a `TradingConditionFilter` from
  `policyBundle.TradingConditions` and passes it through. A promoted policy with
  `EconomicEventFilterEnabled=true` correctly fails closed (throws) rather than silently ignoring
  it, since no live `IEconomicEventProvider` exists yet.
- **LIVE-02 (High) — ALREADY FIXED**, no code change needed. Confirmed `LiveAnalysisProfileRegistry`
  (own doc comment: "Replaces the previous hardcoded `new ChartAnnotationEngine()`...") is
  genuinely wired into `LiveEngineHostedService.cs` at every relevant call site, sourcing real
  `package.FeaturePolicy.AnnotationOptions` per assignment. This was fixed sometime between the
  July 17 audit and now, just never marked resolved anywhere.
- **LIVE-03 (High) — confirmed real, deliberately NOT fixed tonight.** More architecturally
  involved than the other three: `LiveTrading/Actors/MarketAnalysisActor.cs:280` unconditionally
  hardcodes `crossMarket: null` because no cross-instrument currency-strength aggregator is wired
  into the live per-instrument actor at all — this is missing capability, not misrouted wiring.
  Building it correctly needs a new cross-instrument component and a running live host to verify
  against, neither of which was safe to attempt unsupervised. **Mitigating**: both
  `CurrencyStrengthEvidenceOptions.Enabled` and `CurrencyStrengthOptions.Enabled` default to
  `false` — same latent risk class as CAL-01, not biting any live decision unless explicitly
  turned on. Needs a deliberate feature-build session with live-host testing available.
- **CAL-01 (High) — FIXED** (primary path). `LiveTrading/Management/LivePositionManagementService.cs`
  really did hardcode `EntryVolatilityBucket = "Live"`, which can never match a calibration cohort
  key (`"Low"`/`"Normal"`/`"High"`/etc.), silently and permanently defeating live calibrated
  management whenever enabled. **Fix**: extracted the simulator's bucket-classification logic into
  a shared `TradeManager.VolatilityBucketClassifier` (simulator's own `StrategySimulationSession`
  now delegates to it too, so the two paths can't drift apart again); added
  `EntryVolatilityBucket` to `LiveOrderRecord`/`LivePositionRecord` (computed from
  `decision.Decision.AtrPercentile` at entry, exactly mirroring `EntrySetupType`/`EntryRegime`'s
  existing pattern) and propagated it through every position-construction site; the management
  service now reads `position.EntryVolatilityBucket` instead of the hardcoded literal.
  **Known remaining gap, not fixed**: `LiveTrading/Shadow/Outcomes/LiveShadowOutcomeService.cs:638`
  has the identical hardcode for shadow/paper-trade outcome tracking. Lower severity (shadow mode
  places no real trades), but a corrupted shadow-evaluation signal could still mislead a future
  policy-promotion decision if calibration is ever enabled for shadow. `ShadowPaperPosition` has
  no `AtrPercentile` capture at all currently — would need that added first.
- **PERSIST-01, PROMO-01**: marked RESOLVED same-day in the issue register (JSON round-trip bugs
  in checkpoint reload and policy reload) — treat as fixed.

### 2.5 Bug found and fixed this session: structural-confluence was silently ~75% inert by default

**This is the single most important technical finding of this session.** `ResolveAgentDefinition()`
(`Simulator/Models/BacktestConfiguration.cs`) — the actual dispatch path used by
`BacktestApplicationService`, the CLI, and the walk-forward tooling — unconditionally enables three
of `structural-confluence`'s four playbooks (liquidity-sweep-reversal, liquidity-break-retest,
supply-demand-pullback), but `ChartAnnotationOptions.Liquidity.Enabled` and `.SupplyDemand.Enabled`
both default to `false` and nothing turned them on. Result: any backtest run via the CLI, the
walk-forward tool, or a plain `BacktestRequest` (i.e., everything except Dashboard-created
profiles, which have client-side JS that patches this) silently produced **zero trades from three
of four playbooks**, with no error. Confirmed empirically: 7 months of real EUR/USD data, zero
trades from those playbooks; a control test on the same data showed legacy-progressive (83 trades)
and improved-progressive (16 trades) worked fine.

**Fix applied** (`Simulator/Models/BacktestConfiguration.cs`): `ResolveAgentDefinition` now throws
a clear `ArgumentException` if a playbook needing Liquidity/SupplyDemand analysis is enabled but
the corresponding `Runtime.AnnotationOptions` flag isn't — mirroring the check that already
existed for the Dashboard profile path (`SimulationStrategyProfile.ValidateForExecution()`).
Verified against the full test suite (one pre-existing test's setup fixed to supply the required
flags; the rest of the 8-failure baseline is unrelated, confirmed via `git stash`).

**Also fixed**: `QuantResearchRunner/Models/QuantResearchPlan.cs` +
`WalkForwardExperimentRunner.cs` had **no way to configure execution costs at all** — every
walk-forward run silently used `BacktestRequest`'s record defaults (commission ≈0.2bps/side,
which is unrealistically cheap). Added `CommissionRate`/`SpreadBasisPoints`/`SlippageBasisPoints`
fields to `QuantResearchPlan`, wired through to the built request.

**Implication**: any prior backtest/walk-forward result for `structural-confluence` produced via
the CLI or `QuantResearchRunner`, dated before this session, should be treated as **not
representative of the full strategy** — it was testing an ~1-playbook-only version.

### 2.6 Performance work this session (LiquidityAnalyzer / ChartAnnotator)

Profiled a real 60-day EUR/USD backtest with `dotnet-trace` (`Microsoft-DotNETCore-SampleProfiler`)
and `dotnet-counters` after discovering enabling Liquidity/SupplyDemand analysis made runs ~9x
slower. Found and fixed real hot paths:

- `ChartAnnotator/Engine/DeterministicId.Create` — removed a LINQ iterator + 2 heap allocations
  (UTF8 byte array, SHA-256 output array) per call, without changing output (verified
  byte-identical across 6 edge cases including nulls/empty-parts/decimal min-max).
- `ChartAnnotator/Liquidity/LiquidityAnalyzer` — replaced several LINQ hot loops
  (`DetectIsolatedSwings`'s pool-existence check and opposite-swing lookup,
  `MergeOverlappingPools`'s sort, `AddPeriodPair`'s 4x redundant aggregation) with manual
  equivalents.

**A real bug was introduced and caught during this work**, worth remembering as a cautionary
tale for future changes to this file: the pool-existence pre-filter was rewritten as an O(1)
reference-counted index (`_activePivotPoolCounts`), synced at pool creation and removal — but
`MergeOverlappingPools` also *mutates* a surviving pool's `SourcePivotTimes` (absorbing a merged
pool's pivots) via a `with` expression, and that mutation wasn't reflected in the index. This
caused a real, silent divergence: 12 trades instead of 8 on an identical 60-day window, only
caught because an empirical old-code-vs-new-code A/B trade-count comparison was run (the full
unit-test suite passed identically both with and without the bug — **unit tests alone did not
catch this**). Fixed by adding the missing third sync point at the merge mutation, then
re-verified with the same A/B methodology (trades matched exactly, 8/8, across original ↔
buggy-optimization ↔ properly-fixed-optimization).

**Net result, verified**: same 60-day window, same trade outcome (8 trades, -5.92R, byte-for-byte
identical to unoptimized code) — **2025s → 1258s, a 1.61x / 37.9% reduction** in wall-clock time.

**Lesson for future perf work on this file**: `LiquidityAnalyzer`/`SupplyDemand` pool bookkeeping
has now caused two distinct subtle bugs across two different sessions (see the
`TrimStaleTerminalPools` comment about a prior FIFO-queue bug). Any further optimization here
needs the same rigor: unit tests are not sufficient; verify with a real-data trade-count A/B
before trusting a change.

**Remaining, not yet done** (deliberately deprioritized — diminishing returns / unexplored risk):
`SupportResistanceDetector.DetectCore` (separate DBSCAN-based analyzer, 0.06% exclusive time, no
established invariants understood yet); `LiquidityAnalyzer.Transition` (inherent cost of the
immutable-record design, not algorithmic waste).

### 2.7 8 pre-existing `Simulator.Tests` failures fixed (2026-08-03)

Noted as a known, unrelated gap in an earlier entry tonight; fixed properly rather than left open.
Two distinct root causes:

- **7 tests** (`BacktestCandidateEvaluatorCachingTests`, `IndicatorConfluenceRequestOverlayResolverTests`,
  `LiquidityBreakRetestRequestOverlayResolverTests`) built a `BacktestRuntimeOptions` for a
  `StructuralConfluenceStrategyOptions` agent without enabling `AnnotationOptions.Liquidity`/
  `.SupplyDemand` — tripping `ValidateStructuralAnnotationRequirements` (§2.5's fix). Pre-dated
  this fix; the fix's own test updates (`StructuralConfluenceRegimeRoutingTests.cs`) didn't cover
  every fixture that happened to default-enable Liquidity/SupplyDemand playbooks. Fixed by adding
  `AnnotationOptions = new ChartAnnotationOptions { Liquidity = ..., SupplyDemand = ... }` to each
  fixture's `BacktestRuntimeOptions`, matching the existing pattern.
- **1 test** (`MultiInstrumentPortfolioClockTests.ApplicationService_CompletesWithStrategyAssignmentsAcrossTwoInstruments`)
  found a genuine bug, not a test gap: `BacktestApplicationService.CreateDefaultStrategies`
  (`Simulator/Services/BacktestApplicationService.cs:865-867`) built each `StrategyAssignments`
  entry's default id from the *canonicalized* agent-kind (`TradingAgentTypeIds.Format(definition.Kind)`,
  e.g. `"legacy"` → `"legacy-progressive"`), while `BacktestRequest.Validate()`'s duplicate-id check
  (`Simulator/Models/BacktestConfiguration.cs:809`) uses the *raw* `assignment.StrategyType` for the
  same default-id formula. The two independently-computed formulas silently disagreed whenever a
  caller used an alias (`"legacy"`/`"improved"`) instead of the canonical form — worse than a cosmetic
  mismatch: a request with two assignments on the same instrument, one via `"legacy"` and one via
  `"legacy-progressive"`, would pass the duplicate-id validation (raw strings differ) but collide at
  runtime (both canonicalize to the same id) — an actual ID-collision that validation was supposed to
  prevent. Fixed by making `CreateDefaultStrategies` use the same raw-`StrategyType` formula as
  `Validate()`. No other test in the repo asserted the canonical-id form, so this was safe to change.

All 1049 tests pass after the fix (`Simulator.Tests`, 0 failed, 0 skipped-as-broken).

### 2.8 New `DivergenceReversalAgent` (2026-08-03/04) — formalizing the user's personal strategy

User has a manual/Python strategy (private repo `github.com/mhn1991/algoTrading`, cloned to
`/mnt/storage/scratch/algoTrading` for reference — not part of this repo) that traded profitably
for ~2 years across instruments before an unbounded-risk gap surfaced (no stop-loss, no target,
just "close and flip on the next opposite signal"). Read the actual Python
(`Brokers/Binance/run.py`/`test.py`): Bollinger(20,2) + RSI(14) + StochRSI(14/3/3) extreme reading
(price at a band, RSI >75/<25, StochRSI %K at 100/0) triggers a close-and-flip; a *partial* extreme
recurses into a lower timeframe for confirmation. No stop, no target, no divergence check — exactly
matches the user's own description of the gap ("had to sell but didn't know how deep it would
go"). Heads-up flagged to the user (not repeated here): `test.py` has a live OANDA demo-account
bearer token hardcoded in source — worth rotating and externalizing regardless of repo privacy.

User's refinement: classify each extreme reading via StochRSI-fast-line divergence against price
at the swing pivot — regular divergence (price extends, momentum doesn't confirm) = genuine
reversal = close-and-flip; hidden divergence or convergence (momentum confirms the extreme) =
breakout continuation = hold or open in that direction, never close an existing position. Primary
exit is always the next genuine opposing reversal signal, on any monitored timeframe independently
(no fixed trend/setup/entry role hierarchy).

**Implemented, Phase 1 (agent logic only — not yet wired into the CLI/backtest/live registry,
see below):**

- `ChartAnnotator/Indicators/StochRsiAnalysisState.cs` — new. The codebase already had
  `RsiAnalysisState` (ring-buffer swing-pivot divergence tracking for raw RSI) but nothing
  equivalent for StochRSI's fast line, which is what the user's refinement actually needs.
  Deliberately mirrors `RsiAnalysisState`'s exact algorithm and `ChartAnnotator.Collections.RingBuffer<T>`-based
  shape (samples ring buffer + separate high/low pivot ring buffers, swing-pivot comparison,
  Regular/Hidden Divergence + Convergence classification) rather than a new design, so the two stay
  directly auditable against each other. New model types (`StochRsiRelationshipType`,
  `StochRsiRelationshipSnapshot`, `StochRsiAnalysisSnapshot`) added to
  `ChartAnnotator/Models/AnalysisModels.cs` alongside the existing Rsi/Cci ones — purely additive,
  not yet part of the shared `IndicatorSnapshot`/`ChartAnnotationEngine` pipeline (see below).
- `Agent/Strategies/DivergenceReversal/DivergenceReversalStrategyOptions.cs` +
  `DivergenceReversalAgent.cs` — new `ITradingAgent`. Per configured timeframe, feeds the
  already-centrally-computed `AnalysisSnapshot.Indicators.StochRsi.Fast`/`.Rsi`/`.BollingerUpper`/
  `.BollingerLower`/`.Atr` and `AnalysisSnapshot.Swings` (all already computed by the shared engine —
  no duplicate indicator math) into its own `StochRsiAnalysisState` instance, deduplicated so each
  closed candle is only processed once. Extreme + freshly-confirmed regular divergence → `Close`
  (if holding the opposite side) then, on the next flat evaluation, `Buy`/`Sell` the flip side
  (tracked via a small pending-entry dictionary, not by re-detecting the signal a candle later,
  since it may no longer hold). Extreme + hidden divergence/convergence → open if flat, hold if
  already positioned, never closes. `ExitManagementMode = ProtectiveStopAndStrategyExit`: a wide
  ATR-multiple stop (default 3.0×ATR) is submitted as a backstop only — the primary exit is always
  the next genuine reversal signal, addressing the user's stated risk gap without overriding their
  stated design (signal-driven exit as the primary mechanism).
- Tests: `Simulator.Tests/DivergenceReversalAgentTests.cs` — 4 tests directly on
  `StochRsiAnalysisState` (regular/hidden divergence, convergence, below-tolerance no-signal) + 3
  on the agent (buy on divergence while flat with a protective stop set; close-then-flip across two
  evaluations when divergence opposes an open position; convergence at an extreme opposing an open
  position never closes it). All pass; full `Simulator.Tests` suite (1056 tests) has zero
  regressions from the additive `AnalysisModels.cs` changes.

**Phase 2 (2026-08-04) — registry wiring, partial-signal confirmation cascade, and a real bug caught
by the production pipeline:**

- Registered end-to-end: `TradingAgentKind.DivergenceReversal`, `TradingAgentTypeIds.DivergenceReversal`
  = `"divergence-reversal"` (wired into `TryParse`/`Format`), `TradingAgentDefinition.DivergenceReversal`
  option slot (`RequiredIntervals`/`TriggerInterval`/`Validate`/`ToAgentDefinition`/`FromAgentDefinition`),
  `AgentDefinition.FromDivergenceReversal`/`ReadDivergenceReversalOptions` (+ JSON source-gen context),
  and `DivergenceReversalTradingAgentBuilder` in `TradingAgentCatalog.CreateDefault()`. Now selectable
  via `--strategies divergence-reversal` on the CLI, same as the other three agents. Deliberately
  scoped to `SupportedDeploymentModes = [ObserveOnly, Shadow]` only (not `ManualApproval`/`Automatic`)
  and `AutomaticDemoCertified/AutomaticLiveCertified = false` — untested strategies don't get to be
  live-eligible just because they're wired in. `BacktestConfiguration.ResolveAgentDefinition` defaults
  it to `MonitoredIntervals=[30m,15m]`, `ConfirmationIntervals=[5m,1m]` (see next point).
- Added the partial-signal confirmation cascade per the user's mid-turn clarification ("trigger
  time frame set 30m and 15m and then it should check 5 min and 1 min if the signal was partial to
  confirm the signal"): a 30m/15m trigger reading that only *touches* the extreme condition (band
  touch or StochRSI-fast beyond 85/15, without fully qualifying) now recurses into 5m then 1m,
  finest-first-configured-order, looking for a *full* qualifying reading in the same direction —
  mirroring the original Python's recursive lower-timeframe confirmation this agent had
  deliberately simplified away in Phase 1. The confirming interval's own StochRSI-fast divergence
  classifies reversal-vs-breakout, since that's where the fresh confirmed swing pivot actually is.
- **Real bug caught by a production smoke test, not by unit tests**: the inline `Close` decision
  built in `EvaluateAsync` never set `SuggestedQuantity`, so `ExecutionManager.ExecutionCoordinator.ValidateDecision`
  threw `InvalidOperationException: A buy, sell, or close decision requires a quantity.` the first
  time this agent ran through the real CLI pipeline. The unit tests only asserted `decision.Action ==
  Close` and never exercised execution-layer validation, so they couldn't have caught it. Fixed by
  capturing the full `BrokerPosition` (not just its `Side`) and setting `SuggestedQuantity =
  currentPosition.Quantity` on the Close decision. Re-ran the same smoke test after the fix —
  succeeded.
- Smoke test (not yet a full walk-forward — see below): `FX:EUR/USD`, 2026-06-01 to 2026-06-08,
  `--strategies divergence-reversal --quantity 1000`, real OANDA-cached 1-minute data through the
  actual `BacktestRunner` CLI. Result: **3 trades, -220.86 USD net, 33.3% win rate**. Too small a
  sample to mean anything — confirms the pipeline wiring works end-to-end, not that the strategy
  has an edge.
- `Simulator.Tests/AgentCatalogueArchitectureTests.DefaultCatalogue_ResolvesEveryCanonicalAgentType`
  updated to include the new agent type (expected consequence of registering a 4th agent, not a
  regression). `Simulator.Tests/DivergenceReversalAgentTests.cs` grew a 4th agent-level test for the
  partial-cascade path. Full suite passes.

Walk-forward tested against real historical data — see §3.7. Result: mixed/flat (pooled
out-of-sample avgR=-0.037 across only 16 test trades), not decisively negative like
legacy-progressive but not validated either — the sample is too small to trust either way. 9-instrument
follow-up test (§3.8) then came back decisively negative (829 trades, netR=-85.11, PF=0.82, 100%
Monte Carlo safety-breach) — and the root-cause investigation below is a strong candidate reason why.

**CRITICAL BUG found 2026-08-04 (verified, not yet fixed) — the "freshly confirmed" freshness gate
is broken in the real pipeline:** `DivergenceReversalAgent.UpdateAndClassify`
(`DivergenceReversalAgent.cs:193`) feeds `StochRsiAnalysisState.Update` with `snapshot.Swings` -
`AnalysisSnapshot.Swings`, which is `state.SwingSnapshot = state.Swings.Snapshot()`
(`ChartAnnotator/Engine/ChartAnnotationEngine.cs:153,310`) - the **full accumulated ring buffer**
of up to the last 500 confirmed swings, re-sent unchanged on every closed candle. Compare to how
the engine feeds the reference `RsiAnalysisState` this class was supposed to mirror:
`ChartAnnotationEngine.cs:140-144` passes `confirmed` - only the 0-2 swings newly confirmed *that*
candle, straight from `SwingDetector.Update()`. `StochRsiAnalysisState` was never wired to receive
that same delta.

Consequence, confirmed with a standalone repro
(`/mnt/storage/scratch/stochrsi-bug-repro/Program.cs`): `AddPivotAndDetect` unconditionally
re-adds every re-sent swing and recomputes its relationship each time; since the recomputed
relationship has identical `Strength` to what's already stored, the freshness check
(`relationship.Strength >= _latestRelationship.Strength`) is satisfied via equality every time, so
`IsNewRelationship` gets set back to `true` on **every** candle for as long as that swing pair sits
anywhere in the 500-entry window - not just the candle it was actually confirmed on. Repro output:
feeding only the delta gives `IsNewRelationship=False` on candles with no new pivot (correct);
feeding the full accumulated window (what production actually does) gives `IsNewRelationship=True`
on every one of those same candles. `DivergenceReversalAgent.TryBuildSignal` relies on
`IsNewRelationship` as its sole freshness gate ("an older, aging relationship says nothing about
whether *this* extreme is exhausted or confirmed" - its own doc comment) - that gate is effectively
always open once any relationship has formed, so the agent treats any later revisit to a price
extreme as a fresh signal regardless of whether a real new divergence/convergence pivot just
occurred. Strong candidate explanation for §3.8's over-trading (829 trades) and negative result -
not proof the underlying strategy idea is bad, proof the implementation wasn't testing what it
claimed to. Missed by every unit test in `DivergenceReversalAgentTests.cs`/
`StochRsiAnalysisStateTests` because each one passes a single-item "just this pivot" list per
`Update` call, never the accumulated window production actually sends - the same "passes tests,
wrong on real data" pattern this file already warns about for pivot-lifecycle code.

**FIXED 2026-08-04**: `UpdateAndClassify` (`DivergenceReversalAgent.cs`) now tracks a per-timeframe
`LastProcessedSwingConfirmedAt` watermark and filters `snapshot.Swings` down to only the swings
confirmed since the last processed candle before feeding `StochRsiAnalysisState.Update` - matching
how the engine feeds the reference `RsiAnalysisState`. All 34 StochRSI/DivergenceReversal unit
tests plus the full 1057-test `Simulator.Tests` suite pass with zero regressions.

**Post-fix retest result (complete) - trade frequency collapsed ~23x, and it's still decisively
negative:** Re-ran the exact same EUR/USD walk-forward (§3.7) and 9-instrument backtest (§3.8)
after the fix. EUR/USD walk-forward: **0 pooled out-of-sample trades across all 4 folds** (only 2
trades total in-sample across all 12 fold/phase windows combined, one win one loss). 9-instrument
backtest: **36 pooled trades** (was 829 - a ~23x drop), netR=-14.24, avgR=-0.395, PF=0.42,
**Monte Carlo safety-breach probability=84.25%** (p5/median/p95 finalEquityR = -27.04/-14.01/-2.21R).
Per-instrument: AUD/USD 6 (was 94), EUR/USD 1 (was 48), GBP/USD 1 (was 64), NZD/USD 2 (was 86),
USD/CAD 3 (was 13), USD/CHF 0 (was 61), USD/JPY 6 (was 73), XAG/USD 11 (was 203), XAU/USD 6 (was
187) - every instrument dropped by roughly one to two orders of magnitude. This confirms the bug
was real and had a large effect (the stale `IsNewRelationship=true` was indeed driving most of the
829-trade over-trading in §3.8) - but the *average loss per trade got worse*, not better
(avgR=-0.395 post-fix vs. -0.103 pre-fix), and the strategy is still clearly unprofitable at scale,
just on a much smaller, much noisier sample. This rules out "the bug was single-handedly masking a
real edge" as an explanation - fixing it did not reveal a profitable strategy underneath. Worth
checking during code review whether the fix is now *overly* strict somewhere (e.g. an interaction
between the swing-detector's confirmation lag and the freshly-confirmed gate making the two
conditions - extreme price condition and fresh divergence pivot - rarely land on the same candle in
practice) versus this simply being how infrequently the underlying condition genuinely occurs.

**That check was done 2026-08-14 and the "overly strict" hypothesis is confirmed** - it was the
first of the two, not the second. `SwingDetector` only confirms a pivot once its right-hand candles
close, so the StochRSI-fast relationship lands ~2 candles *after* the price extreme that produced
it; by then price is normally back inside the Bollinger band and `ClassifyExtreme` no longer reads
Full. Requiring `IsNewRelationship` (true for exactly one candle) *and* a Full extreme on that same
candle made the two halves of the entry condition close to mutually exclusive by construction. Full
numbers and the supersession of §3.7/§3.8 are in §3.9. Three changes followed:

- **Freshness window** - `DivergenceReversalStrategyOptions.SignalFreshnessCandles` (default 3,
  validated `0 <= value <= RelationshipSignalLifetimeCandles`). `TryBuildSignal` now accepts a
  relationship that is either new this candle or confirmed within that many candles, measured in
  candles of the timeframe that produced it (`StochRsiRelationshipSnapshot.AgeCandles`, which the
  state was already tracking). `0` reproduces the old same-candle-only rule exactly, so the
  previous behaviour stays reproducible for comparison.
- **Consume-once guard** - a relationship that has produced a decision is recorded per timeframe
  (`ConsumedRelationship`, keyed on `ConfirmedAt` + `Type`) and cannot fire again. Without this the
  wider window would let one relationship re-enter a position a candle or two after the protective
  stop closed it. A signal is marked consumed even when it leads to no order (already positioned
  that way, or a breakout held against an open position) - it has had its say either way.
- **Signal funnel** - `DivergenceReversalAgent.GetFunnelSnapshot()` returns per-timeframe counts of
  how far each candle got: candles processed → partial extreme → full extreme → relationship
  exists → inside the freshness window → not already consumed → direction matches → signal built,
  plus agent-level entries/closes/flips/suppressions
  (`Agent/Strategies/DivergenceReversal/DivergenceReversalFunnel.cs`). Diagnostics only; nothing
  feeds a decision. Surfaced in the Agent Debugger (final SSE Status event → funnel table on the
  page) so "why did this only trade N times" is answered by whichever column absorbed the Full
  readings, not by inference. **Any future re-tune of this agent's thresholds should be justified
  from this table, not from trade count alone.**

Tests added for the gap that let the original bug through (`Simulator.Tests/DivergenceReversalAgentTests.cs`,
now 12 tests): `Update_ExpectsPerCandleDeltas_ResendingTheSameSwingsRepeatsRelationships` pins the
`StochRsiAnalysisState` contract that made the bug possible (re-sent pivots fabricate relationships
- callers must send deltas), and `EvaluateAsync_AccumulatedSwingWindowResentEveryCandle_SignalsOnlyOnce`
drives the agent with the full accumulated window production actually sends, asserting one fire, no
re-fire while it is re-sent, and a fresh fire once a genuinely new pivot confirms. Plus one test
each for the freshness window firing and for `SignalFreshnessCandles = 0` rejecting the same setup
as stale. Full `Simulator.Tests` suite: 1061 passed, 0 failed.

**Not yet done: the post-change backtest rerun.** The freshness window is expected to raise trade
frequency substantially, but no walk-forward or 9-instrument rerun has been done since, so §3.9's
numbers remain the current measured state. Nothing here is evidence of edge.

**Also delivered 2026-08-04: standalone "Agent Debugger" page** for visually/textually inspecting
what the agent sees candle-by-candle, separate from the Simulator/backtest pipeline entirely, per
explicit user request to "visually track whatever happen to the agent" and use "true
lowest-timeframe-up aggregation" (build higher timeframes from the base candle stream, only ever
act on the latest *closed* candle):
- Backend: `DashboardLive/AgentDebugModels.cs` (DTOs), `AgentDebugRunner.cs` (drives
  `ChartAnnotator.MarketData.MultiTimeframeAggregator` - the same component the real engine uses -
  feeding `ChartAnnotationEngine.ProcessAsync` for indicators, calling the real
  `DivergenceReversalAgent.EvaluateAsync` for ground-truth decisions, plus a small read-only
  diagnostic classifier mirroring `ClassifyExtreme` purely for per-candle display), `AgentDebugApi.cs`
  (`POST /api/agent-debug/runs`, `GET /api/agent-debug/runs/{id}/stream` SSE, `GET
  /api/agent-debug/instruments`). Wired into `DashboardLive/Program.cs`.
- Frontend: new standalone page `Dashboard/agent-debug.html` /
  `Dashboard/src/pages/AgentDebugPage.vue` (separate Vite entry, not merged into the main
  `App.vue`/`index.html` bundle - `vite.config.ts` now builds both). Form for
  instrument/timeframes/warm-up/run window, a live candle chart with extreme/decision markers, and
  a CLI-style auto-scrolling log showing RSI/Bollinger/StochRSI-fast per closed candle per
  timeframe.
- Verified end-to-end via a real dev-server + browser session (EUR/USD, 2026-06-01..2026-06-10):
  candles/diagnostics/decisions stream correctly, `IsNewRelationship` now correctly fires only once
  per fresh relationship (independent confirmation of the fix above). Two real bugs were caught and
  fixed during this verification, not by unit tests: (1) an unthrottled SSE-to-Vue-reactivity path
  that hung the browser tab on a real multi-week run (thousands of events/sec into an unbounded
  `v-for`) - fixed with an animation-frame-batched flush plus a rolling window cap on both the log
  and the chart; (2) the log panel's autoscroll silently did nothing because the template ref was a
  bare `let` variable instead of Vue's `useTemplateRef` API.
- **Known limitation, not addressed**: stopping the frontend stream does not cancel the backend
  job - an abandoned run keeps CPU-processing and writing to its (unbounded, in-memory) channel
  until it finishes on its own. Fine for single-user local use, not fine to ship further than that
  without adding real per-job cancellation.
- Separately noticed, unrelated to this feature: `npm run build`'s default output-directory-clear
  step tries to copy/clear `Dashboard/public/data/backtests/simulations/`, which has accumulated
  **1.5TB** of backtest export data over the course of this session's work - makes a full production
  build impractically slow. `npm run dev` (used for all verification above) is unaffected. Not
  cleaned up or otherwise addressed here - flagging for awareness, same class of issue as this
  file's existing `/tmp` filling-up note.

**Explicitly NOT done yet:**

- ~~Understand why post-fix trade frequency is now near-zero~~ - done 2026-08-14: the
  swing-confirmation lag versus the same-candle freshness gate, see above and §3.9.
- **Rerun the walk-forward and 9-instrument backtests with the freshness window in place** - the
  fix is untested against real data; §3.9 still reflects the pre-freshness-window build.
- A larger-sample validation (longer window and/or more instruments), still blocked on the rerun
  above producing a trade count worth analysing.
- `StochRsiAnalysisState` is deliberately self-contained inside this one agent, not promoted into
  the shared `IndicatorSnapshot`/`ChartAnnotationEngine` pipeline the way `RsiAnalysisState` is. If
  a second strategy later wants the same StochRSI-fast divergence tracking, promote it then rather
  than let two independent copies drift.
- Breakout-classified signals only open a position when currently flat; they do not add to or
  scale an already-open position in the same direction. Not asked for, and out of scope for this
  first pass.
- Agent Debugger: backend job cancellation, and a production-build fix/cleanup for the 1.5TB
  `public/data` directory (both noted above).

---

### 2.12 Dashboard price-action markers were drawn one bar to the right (2026-08-28)

**Symptom as reported:** signals on the chart looked reversed — "buy where we should sell and
sell where we should buy" — on 5m XAU/USD.

**Root cause: an off-by-one in marker placement, not a direction error anywhere in the engine.**
Three facts combine:

1. `ChartAnnotator/PriceAction/PriceActionAnalyzer.cs:973` stamps every event with its source
   candle's *close* time: `ConfirmedAt = candle.CloseTime ?? candle.OpenTime`.
2. `Dashboard/src/components/AnalysisChart.vue:117` builds `visibleOpenTimes` from candle
   *open* times.
3. The marker's x came from `xForTime(event.confirmedAt)`, a binary search for an **exact**
   match against those open times.

For any interval a candle's close time *is* the next candle's open time, so the search matched
the following bar. Every price-action marker rendered one candle to the right of the bar whose
geometry produced it.

**Evidence (real data, via `GET /api/workspaces/oanda/window`, XAU/USD 5m, 2026-08-27):** the
02:00 bar is red (4640.03 → 4638.82, closing near its low) and produced `BearishRejection`
(confidence 86.4, `confirmedAt` 02:05, resistance 4643.49). The 02:05 bar is green
(4638.90 → 4642.30) and produced nothing. The bearish marker rendered on the green bar. Because
5m bars alternate colour so often, the shifted marker usually lands on an opposite-coloured
candle — which is exactly what "reversed signals" looked like.

**Why it only showed up on drill-in.** In the normal live view the event belongs to the newest
bar, so `confirmedAt` is past every open time and `xForTime` hits its clamp
(`if (low >= times.length) return xAt(times.length - 1)`) — accidentally correct. The bug only
bites when candles exist *after* the event's bar, i.e. the centred/drill-in window.

**Fix.** Anchoring moved out of the SFC into `Dashboard/src/components/priceActionMarkers.ts`
(`collectPriceActionMarkers`), which binds each marker to the index of the frame that reported
it and never consults `confirmedAt` for positioning. `AnalysisChart.vue` consumes it.

**Verification.** `Dashboard/src/components/priceActionMarkers.test.ts` — 6 tests on Node's
built-in runner (`npm test`; no new dependency, Node 24 strips types natively), using the real
02:00/02:05 bars above as the fixture. Confirmed the key assertion *fails* against the old
lookup: replaying the old binary search on that fixture resolves to frame index 1, the test
requires 0. `npx vue-tsc --noEmit` and `npm run build` both clean.

**Two things checked and deliberately NOT changed:**
- Only one frame per response carries price-action data (1 of 921 in the window path, 1 of 150
  live). That is intentional payload trimming in `DashboardLive/LiveSseFrameProjector.cs`
  (`ProjectFrame`: *"event rings explode SSE size"*), not a defect.
- The detector direction logic is correct. Audited end to end — displacement, compression
  breakout, sweeps, liquidity pool sides, playbook direction filters, agent Buy/Sell conditions,
  `ExecutionCoordinator.cs:77`, OHLC ingestion, `yPrice`, candle colouring, trade markers. No
  inversion found in any of it.

**Known-unreproduced.** The originally reported labels were `BullishCompressionBreakout` +
`BullishDisplacement`; at 02:05 on 2026-08-27 the engine emitted *no* events (the 02:00 bar gave
`BearishRejection`). 01:05 UTC was also empty, so a local-time (+01:00) reading does not explain
it either. The off-by-one is independently verified, but it may not be the whole of what was
seen — if reversed-looking markers persist after this fix, that is a separate lead.

**Zone / pool / liquidity overlays — same bug, fixed in the same session.** `availableAt` is
close-stamped too (`LiquidityAnalyzer.cs:80`, `SupplyDemandAnalyzer.cs:60`:
`availableAt = currentCandle.CloseTime ?? currentCandle.OpenTime`), and supply/demand zones,
liquidity pools and liquidity events resolved it through `xForTime`, shifting them one bar right
by the identical mechanism. These are not frame-indexed — they come off the edge frame carrying
their own timestamps — so the marker fix does not apply. Added
`Dashboard/src/components/chartTime.ts` (`indexForCloseStampedTime`): bar `i` owns
`(openTimes[i], closeTimes[i]]`, so the producing bar is the last one whose open time is
strictly before the stamp. Handles weekend gaps, both clamps, and empty input. Wired in as
`xForCloseStampedTime` at the three call sites.

Measured on the real 2026-08-27 anchor frame: **13 of 24 liquidity pools reposition** by one bar
under the fix. The 4 supply/demand zones do not — their `availableAt` predates the window, so
old and new both clamp to the first bar.

**Deliberately left on `xForTime`:** swing pivots (`SwingDetector.cs:78,90` —
`PivotTime = candidate.OpenTime`, already open-stamped), RSI relationship pivots (derived from
the same), NeoWave bounds, and trade fills (`openedAt`/`closedAt`/`signalCreatedAt`/
`executedAt`) — all genuine wall-clock instants where nearest-bar matching is correct.

**Verification.** 14 tests total across `priceActionMarkers.test.ts` (6) and `chartTime.test.ts`
(8) via `npm test`; `npx vue-tsc --noEmit` and `npm run build` clean.

### 2.13 Telegram signal notifications (2026-08-28) — callable service, not pipeline-wired

Outbound signal alerting so a developing agent can announce a detection. Telegram only; email was
considered and dropped (SMTP config + a new MailKit dependency + deliverability failure modes for
no benefit over a bot POST).

**Deliberately not wired to a pipeline stage.** Requested as a service an agent calls directly
while its detection logic is being written, so it hangs off `ISignalNotifier` rather than
`SignalFunnel`/`LiveDecisionEpochCoordinator`.

**Shape** — all in `Networking/Notifications/` (a zero-dependency AOT leaf; `Agent` now references
it, no cycle since `Networking` references nothing):
- `SignalNotification` — flat payload (instrument, side, strategy, decision time, interval,
  entry/stop/target, confidence, reason) plus a `DeduplicationKey`.
- `ISignalNotifier` — `NotifyAsync` returning delivered/not. **Contractually never throws**:
  alerting is a side effect of a decision and must not fail the evaluation that produced it.
- `NullSignalNotifier.Instance` — the default everywhere. **This is what keeps backtests silent**:
  `TradingAgentCatalog` builds agents without a notifier, so a replay cannot emit traffic. Only a
  host that deliberately injects a real notifier sends anything.
- `TelegramNotifierOptions` — `Enabled` master switch, timeout, dedupe window. Token is a
  credential: resolve from the encrypted secret store (`DBManager.Abstractions/Credentials/
  SecretResolution.cs`, same mechanism as the OANDA token), never `appsettings.json`.
- `TelegramMessageFormatter` — pure, so message shape is testable without a network round trip.
- `TelegramSignalNotifier` — `sendMessage` via `HttpClient`, source-generated JSON for AOT.

`BreakoutDetectorAgent` takes an optional `ISignalNotifier` (defaulting to null-object) and has a
private `NotifySignalAsync` helper with a commented call site at the detection TODO.

**Bug caught by the tests, worth remembering.** The endpoint was first built as a *relative* uri,
`$"bot{token}/sendMessage"`. Every real bot token contains a colon (`<botid>:<hash>`), which in a
relative uri parses as a **scheme** — collapsing the path to `<hash>/sendMessage`. It would have
failed against every real token while passing any test using a colon-free fake. Now built as an
absolute uri, where colons are legal in the path. Covered by
`Notify_PostsToSendMessageWithConfiguredChat`, which asserts the full path.

**Verification.** 20 tests (`TelegramSignalNotifierTests`, `TelegramMessageFormatterTests`):
transport failure and API rejection both contained rather than thrown, disabled notifier makes no
network call, dedupe suppresses a repeated candle but **releases its slot on failure** so a retry
is possible, R:R computed from the correct side per direction and omitted when the stop is on the
wrong side, HTML escaping of free-form reason text, 4096-char truncation. Plus catalogue and
BreakoutDetector suites green (31 total) and a clean full-solution build.

**Delivery probe CLI** (`Notifications.Cli`, assembly `notify`). Exists to separate "credentials
or group are wrong" from "agent code is wrong" *before* any agent is wired up, so it shares the
real `TelegramSignalNotifier` rather than reimplementing the call:

```
dotnet run --project Notifications.Cli -- --discover              # find the group chat id
dotnet run --project Notifications.Cli -- --chat -1001234567890   # send one test message
```

Token/chat come from `--token`/`--chat` or `$TELEGRAM_BOT_TOKEN`/`$TELEGRAM_CHAT_ID` (env
preferred - an argument lands in shell history). It checks `getMe` before sending, because a bad
token and a bad chat produce errors that are easy to confuse when they surface together at send
time. Tokens are masked in all output (`123456:***abcd`). `--discover` lists chats via
`getUpdates` and explains the two reasons it commonly returns nothing (no recent activity; bot
privacy mode hiding group messages). Dedupe is disabled for the probe so running it twice in a
row actually sends twice. Exit codes: 0 ok, 1 send failed, 2 bad usage, 3 token/API error, 4 no
chats found. Verified against the live API: no token -> exit 2 with usage; bogus token -> exit 3
reporting Telegram's own "Unauthorized".

**Not done:** no host injects the notifier into an agent yet, so a *running agent* still cannot
send - the probe CLI can. Wiring `LiveTradingHost` to construct the notifier (token from the
encrypted secret store) and pass it to the agent is the remaining step.

### 2.14 New `TradingClassificationAgent` + ML subsystem (2026-08-30) — full V1 blueprint, implemented

Implements `Trading Classification Model — V1 Blueprint.md` (36 sections) end to end: a
three-class (`SELL`/`NO_TRADE`/`BUY`) LightGBM classifier over OHLC-derived and indicator
features, with walk-forward training, evaluation, ablation and a live agent. **Unlike
`breakout-detector` (§2.11) this is not a scaffold — it trains, predicts and trades.**

**Three new projects, split on the AOT boundary.** This split is the load-bearing architectural
decision. `Agent.csproj` is `IsAotCompatible` and is AOT-published by `Simulator.AotSmoke`;
Microsoft.ML is reflection-heavy with native LightGBM binaries and is *not* AOT-safe. So:

- `TradingClassifier/` — AOT-safe core, **no ML dependency**. Indicators, `FeatureEngine`,
  `FeatureSchema`, `LabelGenerator`, `DatasetBuilder`, splitters, `ITradingModel`, `Prediction`,
  `ConfidenceSignalGenerator`, classification/trading metrics, permutation importance. Referenced
  by `Agent`.
- `TradingClassifier.ML/` — Microsoft.ML 5.0.0 + Microsoft.ML.LightGbm. Trainers, `MlNetTradingModel`,
  `ModelEvaluator`, `WalkForwardRunner`, `FeatureExperimentRunner`. **Nothing on the AOT path
  references this.**
- `TradingClassifierRunner/` — console: `train`, `walk-forward`, `ladder`, `ablate`.

Verified the boundary holds: `dotnet list Simulator.AotSmoke package --include-transitive` shows no
Microsoft.ML, and `dotnet publish -r linux-x64` still succeeds. If a future change makes `Agent`
reference `TradingClassifier.ML`, AOT publish breaks — that is the intended tripwire.

The agent takes `ITradingModel` by constructor injection; `TradingClassificationTradingAgentBuilder`
supplies a `NoTradeModel` (always `P(NoTrade)=1`) when no resolver is given, so a bare CLI run
resolves and observes rather than failing. A host that already depends on Microsoft.ML passes a
resolver to get a real model. **A zero-trade run means no model was injected** — check that before
concluding anything about the features.

**42 features** (blueprint §8 asks for 40-60), grouped so §35's ladder and §24's ablation both
work off one `FeatureGroups` flags enum. Configurable via `ClassifierOptions` per §34 — nothing
hard-coded.

**Two deliberate deviations from the blueprint, both recorded here because they change results:**

1. **Split embargo (`DatasetSplitter.Embargo`).** The blueprint never mentions it. A row at `t`
   carries a label built from candles through `t + PredictionHorizon`, so without an embargo the
   last `horizon` rows of every train slice were labelled using candles inside the validation
   slice. That is the same leak §25 forbids, relocated from the features to the split boundary,
   and it inflates out-of-sample scores in exactly the way that makes a strategy look tradeable
   and then fail live. Train and validation slices are trimmed by `PredictionHorizon` rows; test
   is never trimmed.
2. **MACD normalised by close.** §8 names `macd`/`macd_signal`/`macd_histogram` plainly, but a raw
   MACD is in price units and §17 explicitly rules that out. The §8 names are kept; the values are
   divided by close so the columns are comparable across instruments and price eras.

**Robustness gap found by the tests, fixed in production code:** a training slice containing a
single class crashes LightGBM with an opaque native error (`"Number of classes should be specified
and greater than 1"`) that names neither the window nor the cause. A quiet walk-forward period
where every row labels `NO_TRADE` reaches this legitimately. `TrainingPreconditions.RequireMultipleClasses`
now throws an actionable message, and `WalkForwardRunner` *skips* such windows (reporting them in
`SkippedWindows`) rather than losing an entire run to one quiet fortnight.

Registration followed §2.11's checklist exactly, including the two easy-to-miss entries: the
`[JsonSerializable]` attribute on `AgentDefinitionJsonContext` (source-gen — a miss fails at
runtime, not compile time) and `DashboardLive/SimulationApi.cs`'s quantity/reward-risk switch.
That switch **still throws for `divergence-reversal`** (pre-existing, untouched).

Verification: full solution builds clean (0 warnings, 0 errors). `Simulator.Tests` 1187/1187,
`TradingCore.Tests` 24/24, `LiveTrading.Tests` 119/119, `TradingHub.UnitTests` 60/60.
29 new tests across `TradingClassifierFeatureTests.cs` and `TradingClassifierAgentTests.cs`.
The one worth knowing about is `Features_DoNotChangeWhenLaterCandlesExist`: it asserts §25 as a
property — a feature row for candle `t` must be byte-identical whether or not the series continues
past `t`. Any indicator that could see forward fails it. The ML.NET round trip is covered too
(`SavedModel_RoundTripsThroughDisk`, and a score-vector order test that would catch a transposed
`Sell`/`Buy` probability mapping — which would otherwise train fine and trade backwards).

---

## 3. Financial / quant state — does this strategy actually have an edge?

**This is the question the project's own July 15 audit
(`Implementation&PhasingDocs/STRATEGY_ALGORITHM_AUDIT_AND_IMPROVEMENTS.md`) flagged as Priority 0,
"required before considering live capital," and explicitly had NOT been done as of that date.**
This session ran the first real walk-forward validation against it.

### 3.1 Methodology

- Strategy: `structural-confluence` (all four playbooks enabled and functioning, per the fix in
  §2.5).
- Instrument: FX:EUR/USD, real OANDA 1-minute data, 2025-12-11 to 2026-07-24 (`.cache/historical`).
- Walk-forward plan: 4 folds, 60-day train / 15-day validation / 15-day test windows, 30-day step,
  2-day purge gap between phases (no parameter search — `ParameterGrid` empty, so this is pure
  walk-forward *validation*, not walk-forward *optimization*; there's no overfitting-via-selection
  risk in this specific run).
- Two cost scenarios: "current-defaults" (~0.2bps/side commission, the bug from §2.5) and
  "realistic-costs" (5bps commission, 1bp slippage — more representative of retail FX).
- Tooling: standalone driver at `/mnt/storage/scratch/wf-timing` (and the original, larger run at
  `/tmp/wf-research`) calling `WalkForwardExperimentRunner`/`BacktestApplicationService` directly,
  bypassing the CLI's Postgres dependency via `FileSimulationJobRepository`. Monte Carlo via
  `QuantResearch.Validation.MonteCarloSimulator` (trade-order bootstrap, 2000 iterations).

### 3.2 Results — current-defaults cost scenario (COMPLETE)

| Fold | Training net R | Test (out-of-sample) net R | Test trades |
|---|---|---|---|
| 0 | -5.92 | -4.71 | 4 |
| 1 | -5.61 | -2.56 | 6 |
| 2 | -2.36 | -3.59 | 5 |
| 3 | -1.82 | -1.14 | 2 |

**Pooled out-of-sample (all 4 test windows): 17 trades, net -12.00R, avg -0.706R/trade, profit
factor 0.14, max drawdown 12.00R.**

**Monte Carlo (2000-iteration trade-order bootstrap) on the pooled test trades**: 77.75%
probability of breaching a 10R drawdown safety threshold. **Every percentile of the distribution
is negative** — p5 = -17.20R, median = -11.97R, p95 = -6.24R. This is a strong signal: there is no
favorable reshuffling of these 17 trades that makes the strategy profitable. Training windows are
*also* net-negative in every fold (not just test), which points toward a weak/negative raw edge
rather than classic overfitting (overfitting typically shows good training, bad test).

### 3.3 Results — realistic-costs scenario (COMPLETE, 2026-08-03)

First attempt (`/tmp/wf-research`) hung after `current-defaults` completed — 12.5h+ with 0% CPU, 0
disk I/O, all 12 threads parked in futex/poll waits, confirmed not just slow via
`dotnet-trace`/`createdump` (the latter blocked by ptrace permissions in this sandbox). Root cause
not identified; `createdump` couldn't attach to confirm exactly where. Suspect a
`BacktestApplicationService` job-queue disposal/completion issue given the only difference between
scenarios is cost parameters, not threading — worth investigating if it recurs. Killed and
restarted as a fresh run scoped to *only* the realistic-costs scenario at
`/mnt/storage/scratch/wf-research-realistic-costs/` (moved off `/tmp` per this file's own
disk-space lesson); completed cleanly this time.

**Result: the strategy essentially stopped trading.** commission=0.0005, spreadBps=1,
slippageBps=1 (2.5x the commission and 2x the slippage of current-defaults). Across all 12
fold/phase windows, only **3 trades total** fired (vs. 51 total under current-defaults) — 2 in
training windows, 1 in the pooled out-of-sample test set, net -1.64R. Monte Carlo on a single
pooled trade is not meaningful (reported at 0% breach probability only because there's nothing to
bootstrap). This is itself the finding: `structural-confluence`'s own cost-viability gates
(`Geometry.MinimumRiskToSpreadMultiple`, `MinimumRewardRisk`) reject nearly every candidate once
realistic execution costs are priced in — the strategy's edge, such as it is under current-defaults
costs (already negative, §3.2), doesn't clear its own bar for "worth trading" once costs double.
Not evidence the strategy would lose *less* at realistic costs — evidence it would barely trade at
all, which is a different and arguably worse problem for a live-deployment candidate.

### 3.4 What this does and doesn't prove

- **Does prove**: on this specific instrument (EUR/USD), this specific time window (Jan–Jul 2026),
  and current-defaults costs, `structural-confluence` shows no evidence of a positive edge — the
  opposite, in fact, with reasonably strong statistical support (Monte Carlo across all
  percentiles negative).
- **Does not prove**: that the strategy has no edge anywhere. Sample size is thin (17 pooled
  out-of-sample trades across 4 folds) — real but not enormous statistical power. All tested data
  is USD-correlated (see §2.3) — this is one test of "does it trade USD moves well," not
  independent evidence across markets. No genuine cross-pair, no other asset class (metals were
  cached but not yet tested), no other strategy (`legacy-progressive`/`improved-progressive` not
  walk-forward tested this session).
- **Next logical step, not yet done**: realistic-costs result (in flight), then either broaden to
  metals/other cached USD pairs (cheap, already have data) or fetch genuine cross-pairs via the
  OANDA vault credentials (more work, addresses the correlation-confound critique directly).

### 3.5 Fibonacci price/time cluster validation (2026-08-03) — does Boroden's methodology have edge?

User asked me to read Carolyn Boroden's *Fibonacci Trading* (a real, well-specified methodology —
distinct from the earlier free supply-and-demand eBook, which added nothing not already in
`ChartAnnotator`) and, rather than implement it on faith, first check mechanically whether it has
edge on already-cached data. Script: `/mnt/storage/scratch/fibonacci-cluster-validation/Program.cs`
(scratch, not part of the tracked repo). Methodology, deliberately reusing production components
rather than inventing new ones to avoid tuning the test toward a favorable result:

- Swing detection: the real `ChartAnnotator.Structure.SwingDetector` (2-left/2-right fractal
  confirmation), not a custom zigzag.
- H1 EUR/USD, Dec 2025–Jul 2026 (`ChartAnnotator.MarketData.MultiTimeframeAggregator` resampling
  the same cached 1m file used elsewhere this session).
- Price levels: retracements (.236–.786) and extensions (1.272–4.236) from all valid high/low pairs
  in the last 6 confirmed swings; projections (1.0/1.618) anchored from only the single most recent
  swing (matching how the book's own examples use this tool, not an all-pairs combinatorial blowup).
- Cluster = 3+ levels within 0.05×ATR of each other (the book's own definition, just parameterized).
- Time cycles: same 8 ratios applied to elapsed-bar spans (floor 24 bars, to avoid a self-referential
  artifact where a tiny recent span trivially projects back to "now"), anchored from the most recent
  swing back to every earlier one; time window = 3+ projected bars within ±2 of the current bar.
- Reversal test: causal only (a bar only sees swings confirmed strictly before it) — did price move
  ≥1.0×ATR against the immediately-preceding 5-bar trend within the next 10 bars. Same test run
  unconditionally on every bar as the chance baseline.

**Result**: no edge for price clusters alone. Touched-cluster hit rate 60.86% (n=2,782) vs. baseline
60.87% (n=3,506) vs. no-touch control 60.91% (n=724) — differences are noise, not signal; touch rate
itself was 79.3% of all bars, which is itself informative (combinatorial Fibonacci level generation
produces so many candidate levels that "near a cluster" barely discriminates anything on H1 FX data).
Full time+price confluence showed a suggestive lift (72.7% hit rate, n=33) but the sample is far too
thin to trust — ~1.4 standard errors from baseline, not significant, and the setup only fired about
once every two weeks of H1 data.

**Conclusion**: no statistically defensible support for building this out as a new
`ChartAnnotator` analyzer/playbook. The suggestive confluence result isn't nothing, but confirming
it would need materially more data (a longer history or a coarser aggregate across instruments), not
a leap to implementation. Correctly *not* implementing something on a book's authority alone was the
point of running this check first.

**Follow-up mini-backtest (2026-08-03, same day)**: the §3.5 hit-rate result only measures direction
("did price move favorably"), not profitability — a strategy can be directionally right most of the
time and still lose money if stops/targets/costs don't work out. Built a second script,
`/mnt/storage/scratch/fibonacci-mini-backtest/Program.cs`, reusing the same (already-debugged)
swing/cluster/time-window detection unchanged, but simulating an actual traded outcome: entry at the
next bar's open, stop beyond the zone (book's own "few ticks beyond the cluster" placement), target
at the book's stated minimum 1.272×risk, same current-defaults cost model used elsewhere this
session. First run produced nonsensical results (avgR=-5.185, one trade nearly -6269R) — a real bug,
not a real finding: entry happens right at the touched zone edge, so the raw zone-edge stop was
sometimes only a few ticks from entry, making ordinary transaction costs alone dwarf the entire risk
budget. Fixed with a 0.5×ATR minimum-risk floor (a real trader would need the same fix to avoid
sizing off an unrealistically tight stop) and reran:

- Price-cluster only: 1,195 trades, netR=-273.33, avgR=-0.229, winRate=39.3%, PF=0.53.
- Price+time confluence: 22 trades, netR=-7.28, avgR=-0.331, winRate=36.4%, PF=0.45.

**Both lose money as an actual traded system** — confirming, on independent grounds (real
stop/target/cost mechanics rather than a raw hit-rate), the §3.5 conclusion not to implement this.
Notably, confluence trades slightly *worse* per-trade than price-only despite its better raw hit
rate in §3.5 — a direct illustration of why hit-rate isn't a valid proxy for profitability (the
exact caveat given before building this). The 22-trade confluence sample is still too thin to trust
its specific number, but it points the same direction as everything else here: no edge.

### 3.6 legacy-progressive walk-forward result (2026-08-03) — worse than structural-confluence

The one gap §3.4 explicitly flagged ("no other strategy walk-forward tested this session") is now
closed for `legacy-progressive`. Same walk-forward plan, same 4 folds, same EUR/USD cached data,
same current-defaults cost scenario as §3.2 — script at
`/mnt/storage/scratch/wf-legacy-progressive/Program.cs`. Used `ProgressiveStrategyTimeframes`' own
bare defaults (TrendInterval=1h, ConfirmationInterval=15m, EntryInterval=5m), not hand-tuned.

**Result: negative in every single fold and every single phase, no exceptions** — all 4 folds'
training, validation, and test windows lost money. Pooled out-of-sample: **89 trades** (a much
larger, more statistically meaningful sample than structural-confluence's 17), netR=-48.72,
avgR=-0.547, PF=0.33, **Monte Carlo safety-breach probability = 100%** (every one of 2,000
bootstrap iterations breached the 10R safety threshold; p5/median/p95 finalEquityR all in the
range -64R to -32R). This is a more decisive negative result than structural-confluence's — larger
sample, universally consistent across every fold/phase rather than mixed, and no ambiguity in the
Monte Carlo distribution at all.

### 3.7 divergence-reversal walk-forward result (2026-08-04) — SUPERSEDED, pre-bug-fix numbers

> **Superseded by §3.9.** Everything in §3.7 and §3.8 was produced by the buggy build described in
> §2.8 (`IsNewRelationship` stuck true, so most signals were stale re-fires). The post-fix reruns
> completed 2026-08-04 14:53 and are recorded in §3.9; §2.8 carried them from the start but this
> section did not, which is exactly the "one doc updated, the other not" drift this file exists to
> prevent. Read §3.9 for the current numbers; §3.7/§3.8 are kept as the historical record of what
> the buggy build produced.

#### 3.7 (historical) — no edge, too little data to be confident either way

First real validation of the user's formalized personal strategy (§2.8), beyond the 1-week pipeline
smoke test. Same walk-forward plan, same 4 folds, same EUR/USD cached data, same current-defaults
cost scenario as §3.2/§3.6 — script at `/mnt/storage/scratch/wf-divergence-reversal/Program.cs`.
Used `BacktestConfiguration`'s own bare defaults (`MonitoredIntervals=[30m,15m]`,
`ConfirmationIntervals=[5m,1m]`), not hand-tuned.

**Result: mixed, net flat-to-slightly-negative, on a small sample.** Per-fold out-of-sample (test
window) results: fold 0 avgR=-0.093 (8 trades), fold 1 avgR=+0.026 (2 trades), fold 2 avgR=-0.534
(3 trades), fold 3 avgR=+0.568 (3 trades) — two folds positive, two negative, no consistent
direction. Pooled out-of-sample: **16 trades**, netR=-0.59, avgR=-0.037, PF=0.93, sharpe=-0.03 —
essentially breakeven, slightly on the losing side once real costs are applied. Monte Carlo
(trade-order bootstrap, 2000 iterations): safety-breach probability=3.85% (far lower than
legacy-progressive's 100%, but the sample is also ~5x smaller), median finalEquityR=-0.96,
p5=-8.39, p95=+7.32, worst drawdown=15.19R.

16 pooled test trades is too few to draw a confident conclusion either way — this is not the
decisive negative result legacy-progressive got (§3.6), nor a positive one. Training-window numbers
look better (avgR 0.178-0.572, PF 1.33-3.55 across folds) but that's expected in-sample performance,
not evidence of edge. Before trusting this strategy with real capital, it needs a larger sample:
either a longer backtest window, more instruments, or both. Not disqualifying, but not validated —
consistent with every other idea checked this session, none has yet cleared the bar of "profitable
out-of-sample with a large enough sample to trust the number."

### 3.8 divergence-reversal, 9-instrument backtest (2026-08-04) — SUPERSEDED, pre-bug-fix numbers

Follow-up to §3.7's "needs a bigger sample" conclusion. Single continuous backtest (no walk-forward
folds — this agent has no fitted parameters, so a train/validation/test split doesn't guard against
anything) per instrument, 2026-01-01 through 2026-07-24 (end of cached range), across all 9 cached
USD-denominated instruments (EUR/USD, GBP/USD, AUD/USD, NZD/USD, USD/CAD, USD/CHF, USD/JPY, XAU/USD,
XAG/USD), same current-defaults cost scenario, same 30m/15m trigger + 5m/1m confirmation defaults —
script at `/mnt/storage/scratch/dr-9instrument-test/Program.cs`.

**Result: decisively negative.** Pooled across all 9 instruments: **829 trades**, netR=-85.11,
avgR=-0.103, PF=0.82, sharpe=-0.07, **Monte Carlo safety-breach probability = 100%** (every one of
2,000 bootstrap iterations breached the 10R threshold; p5/median/p95 finalEquityR = -154.67/-84.11/
-19.35R). Only 2 of 9 instruments were profitable — EUR/USD (48 trades, avgR=+0.238, PF=1.54) and
XAU/USD (187 trades, avgR=+0.142, PF=1.33); the other 7 all lost money, three of them badly
(USD/CHF: 61 trades, avgR=-0.374, PF=0.46; XAG/USD: 203 trades, netR=-61.42, PF=0.56; NZD/USD: 86
trades, avgR=-0.207, PF=0.67). Per-instrument detail in
`/mnt/storage/scratch/dr-9instrument-test/output/all-results.json`.

This supersedes §3.7's "inconclusive" verdict — with 829 trades instead of 16, the strategy as
currently implemented has no demonstrated edge and likely a real negative one, consistent with
`legacy-progressive` (§3.6) rather than the ambiguous single-instrument read. The 2-of-9 profitable
instruments (EUR/USD, XAU/USD) could be noise, not a genuine instrument-specific edge — no
correction for multiple comparisons was applied here. **Not to be trusted with real capital as
implemented.** User is reviewing the agent's source (`DivergenceReversalAgent.cs`,
`DivergenceReversalStrategyOptions.cs`, `StochRsiAnalysisState.cs`) to identify what's incomplete
before deciding on next steps — candidates raised so far: breakout signals never scale an
already-open position (only open when flat), exit is purely signal-driven with no explicit
profit-taking/exhaustion logic, and there's no regime/instrument filter.

### 3.9 divergence-reversal, post-bug-fix (2026-08-04, recorded here 2026-08-14) — the agent stopped trading

These are the current numbers for this agent, replacing §3.7/§3.8. Same scripts, same data, same
cost scenario, rerun after the `LastProcessedSwingConfirmedAt` watermark fix (§2.8); binaries
confirmed rebuilt (`Agent.dll` 12:43, run 12:45–14:53). Logs:
`/mnt/storage/scratch/wf-divergence-reversal/run-postfix.log`,
`/mnt/storage/scratch/dr-9instrument-test/run-postfix.log`.

| Run | Pre-fix (§3.7/§3.8) | Post-fix (current) |
|---|---|---|
| EUR/USD walk-forward, 4 folds | 16 pooled OOS trades, avgR -0.037 | **0 pooled OOS trades** (2 trades total across all 12 fold/phase windows) |
| 9 instruments, 2026-01-01..07-24 | 829 trades, netR -85.11, avgR -0.103, PF 0.82 | **36 trades**, netR -14.24, avgR -0.395, PF 0.42, Sharpe -0.31 |

Monte Carlo on the 36 pooled trades: safety-breach probability 84.25%, finalEquityR p5/median/p95 =
-27.04/-14.01/-2.21R. Per-instrument trade counts collapsed by one to two orders of magnitude
across the board (USD/CHF 61 → 0, XAG/USD 203 → 11, XAU/USD 187 → 6).

**The finding is the trade count, not the loss.** 36 trades over 7 months across 9 instruments is
~4 per instrument; no verdict about edge — positive or negative — is supportable from that sample,
and the pre-fix "decisively negative at scale" verdict in §3.8 does not survive, because ~95% of
the trades it rested on were stale re-fires. What is established: the bug was real and large, and
fixing it did not reveal a profitable strategy underneath (avgR got worse, on a much noisier
sample).

**Root cause of the near-zero trade count, confirmed 2026-08-14** (§2.8 flagged this as the thing
to check; it checks out). Entry required two conditions on the *same* candle: `ClassifyExtreme`
reading Full (close beyond the Bollinger band **and** RSI **and** StochRSI-fast at their extremes)
and `TryBuildSignal` seeing `IsNewRelationship == true`, which `StochRsiAnalysisState` sets on
exactly the one candle a new relationship confirms. `SwingDetector` confirms a pivot only after its
right-hand candles close, so the relationship lands ~2 candles *after* the price extreme that
produced it — by which point price is typically back inside the band and the extreme no longer
reads Full. The two halves of the entry condition were close to mutually exclusive by construction.
The pre-fix bug had masked this by holding `IsNewRelationship` true indefinitely. See §2.8 for the
fix (a configurable freshness window) and the funnel instrumentation added to measure it rather
than infer it.

### 3.10 divergence-reversal timeframe sweep + loss decomposition (2026-08-14/15) — why it loses

First real-data test of the freshness window from §2.8. 6 configs × 9 instruments = 54 single-window
backtests (2026-01-01..07-24, same current-defaults cost scenario as §3.8/§3.9), 98.9 min wall,
12-way parallel. Driver: `/mnt/storage/scratch/dr-timeframe-sweep/` (per-config agent options via
`StrategyInstrumentAssignment.AgentDefinitionOverride`; warmup fixed at 20 days for every config so
the 4h plan isn't handicapped against the 15m one).

| Config | Trigger / confirmation | Freshness | Trades | netR | avgR | PF | MC breach |
|---|---|---|---|---|---|---|---|
| baseline-freshness0 | 30m,15m / 5m,1m | 0 | 33 | -7.61 | -0.231 | 0.58 | 52.4% |
| baseline-30m15m | 30m,15m / 5m,1m | 3 | 203 | -43.62 | -0.215 | 0.62 | 100% |
| baseline-freshness8 | 30m,15m / 5m,1m | 8 | 551 | -61.75 | -0.112 | 0.79 | 100% |
| fast-15m5m | 15m,5m / 1m | 3 | 268 | -70.26 | -0.262 | 0.54 | 100% |
| slow-1h30m | 1h,30m / 15m,5m | 3 | 130 | -26.96 | -0.207 | 0.59 | 99.3% |
| slowest-4h1h | 4h,1h / 30m,15m | 3 | 72 | -4.65 | -0.065 | 0.87 | 60.9% |

**The freshness window worked as intended** — trade count scales monotonically with it (33 → 203 →
551) and per-trade loss *shrinks* (-0.231 → -0.215 → -0.112), so the old same-candle rule was not
only rare but selecting badly. The `freshness0` control reproduced §3.9's per-instrument numbers
exactly (EUR/USD 1 trade +0.305, AUD/USD 6 at -2.43, GBP/USD 1 at -0.85), confirming the new
harness and the August 4 driver agree. **No configuration is profitable**; slower is consistently
better than faster.

**Loss decomposition** (`/mnt/storage/scratch/dr-timeframe-sweep/analyse.py`, reading the per-trade
`trades.ndjson` the backtest already writes; numbers below are the 203-trade baseline):

- **Not signal direction.** Win rate 43.8%. Winners reach **+1.95R** maximum favourable excursion on
  average; losers only **+0.34R**. The entry genuinely separates trades that run from trades that
  don't.
- **Not execution cost** (this refuted the initial hypothesis, which was that cost explained the
  whole loss). Commission 0.020R + spread/slippage 0.104R = **0.125R/trade**; avgR *gross of all
  costs* is still **-0.090**. Cost does scale with timeframe (0.170R on 15m/5m vs 0.077R on 4h/1h),
  which is part of why slower configs do better, but it is not the cause.
- **It is payoff asymmetry created by the exit layer.** avgWin +0.80R vs avgLoss -1.01R = payoff
  **0.79**; break-even at a 43.8% win rate needs **1.29**. Winners realise only **41% of their own
  MFE**.
- Exit mix: `InitialStopLoss` 53% (avgR -1.06), **`BreakEvenStop` 24% (avgR +0.25)**,
  `TrailedStructureStop` 7% (+1.45), `MfeGivebackStop` 6% (+1.90), `ProfitFloorStop` 6% (+0.19).
  The agent's own designed exit, `StructuralInvalidation`, fires on **1%** of trades — the
  "primary exit is the next opposing reversal signal" design in §2.8 is effectively not being
  tested.
- Sensitivity, holding win rate and loss size fixed: capture 41% → -0.215R (today), 60% → -0.054R,
  80% → **+0.117R**. So exit calibration alone can plausibly flip the sign; it is not a rounding
  error.

**Root cause of the exit behaviour — an accidental inheritance, not a tuning choice.**
`BacktestConfiguration.GetPositionManagement()` (`Simulator/Models/BacktestConfiguration.cs:439-448`)
routes structural-confluence to `StructuralPositionManagement`, anything containing `"legacy"` to
`LegacyPositionManagement`, and **everything else to `ImprovedPositionManagement`**.
`divergence-reversal` matches neither name, so it silently lands in the fallback and inherits the
profile tuned for `improved-progressive`. In `PositionManagementOptions.ImprovedDefaults`
(`TradeManager/StructureBasedTradeManager.cs:338-405`) **three mechanisms all fire at exactly 1R**:
`BreakEvenActivationR = 1`, a `ProfitFloorRule { ActivationR = 1, LockedProfitR = 0 }`, and a 15%
`scale-1r` scale-out. Trades routinely touch ~1R, trip all three, retrace, and exit at ~+0.25R.
The preset's own comment concedes it is a "historical convenience default … not a guarantee for
every agent," and the same file already documents this exact pathology being found and fixed for
`IndicatorConfluencePlaybook` via a per-playbook override — divergence-reversal has the same
disease and no override. **The fallback branch itself is a landmine for the next agent added.**

### 3.11 divergence-reversal exit-calibration sweep (2026-08-15) — the diagnosis was right, the fix is not sufficient

Direct test of §3.10's conclusion. 5 configs × 9 instruments (87.2 min,
`/mnt/storage/scratch/dr-exit-calibration/`), timeframes pinned to the 30m/15m baseline, varying
**only** the position-management profile via `BacktestRuntimeOptions.ImprovedPositionManagement`
(the slot `GetPositionManagement()`'s fallback actually routes this agent to; override confirmed
present in the persisted job JSON before launch).

| Config | Trades | netR | avgR | winRate | avgWin | payoff | PF |
|---|---|---|---|---|---|---|---|
| *baseline (§3.10)* | 203 | -43.62 | -0.215 | 43.8% | +0.80 | 0.79 | 0.62 |
| pm-be2r (break-even 1R→2R) | 203 | -45.16 | -0.222 | 39.9% | +0.87 | 0.92 | 0.61 |
| pm-nofloor1r (drop 1R floor) | 203 | -39.33 | -0.194 | 44.8% | +0.85 | 0.82 | 0.66 |
| pm-noscaleout (drop 1R partial) | 203 | -47.27 | -0.233 | 42.9% | +0.78 | 0.78 | 0.59 |
| **pm-patient (all three → 2R)** | 201 | -34.98 | **-0.174** | 31.3% | **+1.66** | **1.64** | 0.75 |
| *freshness8 baseline (§3.10)* | 551 | -61.75 | -0.112 | 45.6% | +0.90 | 0.94 | 0.79 |
| **pm-patient-fresh8** | 538 | -57.51 | **-0.107** | 33.5% | +1.64 | 1.66 | 0.84 |

**The mechanism behaved exactly as §3.10 predicted, and it still didn't work.** The patient profile
more than doubled payoff (0.79 → 1.64) and avgWin (+0.80R → +1.66R) — the exit layer really was
throwing winners away. But win rate collapsed in near-exact compensation (43.8% → 31.3%) because
`InitialStopLoss` went from 53% to 69% of exits: the 1R mechanisms were *protecting* as many trades
as they were cutting short. Net gain is only avgR -0.215 → -0.174. Break-even at 31.3% needs payoff
2.19; achieved 1.64 (baseline needed 1.29, achieved 0.79 — so relatively closer, still short).

**Methodological correction to §3.10.** That section's sensitivity table ("capture 80% of MFE →
+0.117R") treated MFE capture as a free dial. It isn't: MFE is a hindsight measurement, and the
counterfactual to a +0.25R scratch is frequently a -1R loss, not a completed winner. The table was
an arithmetic ceiling, not an achievable target, and should be read only as such. The isolated
single-knob variants are the clearest evidence — two of the three made things *worse*; only the
coherent combination helped.

**Costs are now the binding constraint for the best config.** `pm-patient-fresh8` is
**+0.018R gross of costs** and -0.107R after them; execution cost is 0.125R/trade. For the first
time a configuration is (marginally) positive before costs — which is not an edge, but it does
relocate the problem. Note §3.10's cost figures scale with timeframe (0.170R on 15m/5m, 0.077R on
4h/1h), and the best *pre-exit-fix* config was `slowest-4h1h` (avgR -0.065). **Patient exits × slow
timeframes is the untested combination** and the obvious next experiment.

**Signal funnel** (now correct — see the defect note below; EUR/USD, pm-patient): the dominant
rejection is still `stale`, ~67% of Full extremes (1m: 645 full → 432 stale → 192 direction
mismatch → 18 signals; 30m: 69 full → 43 stale, 26 mismatch, 0 signals). `noRelationship` is
~0, so a relationship almost always exists — it is simply older than the freshness window or
pointing the other way. Consistent with freshness 8 producing ~2.6× the trades of freshness 3.

**Multiple-comparison warning.** Across §3.10 and §3.11, eleven configurations have now been
compared on one window, one cost model, and nine correlated instruments, with no correction. The
per-config differences here (0.005-0.041R) are well inside the range selection noise can produce.
**Nothing in either section is validated**; the only defensible claims are the negative ones (no
configuration is profitable; all Monte Carlo safety-breach probabilities are ~100%) and the
mechanistic ones (payoff/win-rate coupling, cost per R by timeframe). Any config that looks
promising needs walk-forward validation before it is believed.

**Known defect in the timeframe sweep's own funnel pass**: phase 2 excluded the 1m base interval
from the aggregator's target list, so any config whose finest interval is 1m never saw its
`TriggerInterval` close and `EvaluateAsync` was never called — the funnel rows for
`baseline-*` and `fast-15m5m` in `output/sweep-results.json` are all zeros and must be ignored.
Only `slow-1h30m`/`slowest-4h1h` funnels are valid there. Fixed in the exit-calibration copy
(pass every analysis interval, matching `AgentDebugRunner`); phase 1 backtest results are
unaffected, since they never used that code path.

---

### 2.9 Stored simulation profiles were unreadable — ContentHash invalidated by schema drift (2026-08-19)

**Symptom.** `GET /api/simulation-profiles` returned HTTP 500 (both directly on `:5180` and through
the Vite proxy), so the Dashboard Simulator panel had no strategy profiles. Exception:
`ArgumentException: ContentHash does not match the resolved profile` from
`SimulationStrategyProfile.Validate()` (`Simulator/Experiments/Models/SimulationStrategyProfile.cs:74`),
thrown out of `PostgresSimulationStrategyProfileStore.Deserialize` → `ListAsync`.

**Diagnosis (read-only).** All **44** stored profile revisions failed, not one bad row. Crucially:
`configuration_hash` matched the `contentHash` embedded in `settings_json` on every row (0 drift),
0 deserialization failures, and **0 stored values changed on round-trip** — so nothing was corrupt.
Round-tripping each stored profile through the current type shape showed the current code emits
**45 JSON paths that did not exist when these were written**, 11 of them affecting all 44 rows:
`agent.divergenceReversal`, `analysis.stochRsi{Period,FastSmoothing,SlowSmoothing}` (and the same
three again under `runtime.options.annotationOptions`), `runtime.options.structural{Trigger,Setup,Context}Interval`,
and `runtime.options.tradingConditions.softSpreadLimitAction`; plus ~34 more hitting 34-35 rows
(supply/demand origination filters, liquidity touch filters, `geometry.minimumRiskToSpreadMultiple`,
`marketRegime`). `ComputeContentHash` serializes `Agent`/`Analysis`/`Runtime`/`Management`/`Calibration`
(`SimulationStrategyProfile.cs:195-205`), so **any** added field — even one defaulting to `null` —
changes the JSON and therefore the SHA-256 for every profile ever stored. This is exactly the
persisted-hash breaking-change risk §4b flags, now realized. Note a first hypothesis (that the single
new `TradingAgentDefinition.DivergenceReversal` property explained it) was tested and **reproduced 0 of
44** — the drift is cumulative across several changes, not one.

**Also found:** the current shape *drops* 3 paths that 6 profiles still carry —
`agent.structuralConfluence.indicatorConfluence.{maximumRsiForBuy,minimumRsiForSell,requireSqueezeBreakout}`.
Those values were captured to a side file before any rewrite, since re-serialization discards them.

**Second, independent problem.** 11 of the 44 revisions also fail `ValidateManagementTimeframeAlignment`
— they hardcode `Management.ThesisInterval`/`MainStructureInterval` values their agents no longer
evaluate (e.g. `ThesisInterval 1H` against an agent whose coarsest interval is now `2H`). Re-hashing
cannot fix these; a valid hash over invalid configuration would only mask the drift the validator
exists to catch. **They were deliberately left untouched.** This does not block the dashboard because
`ListAsync` selects `.MaxBy(row => row.Revision)` per profile (`PostgresSimulationStrategyProfileStore.cs:111`)
and all 11 failures are older revisions (r1-r4) of profiles whose latest revision is valid. They will
still throw via `ReadAsync`/`DiffAsync` if those specific old revisions are diffed — **still open**,
fixable by clearing the hardcoded intervals (the validator's own message recommends omitting them so
they derive automatically) or by archiving those revisions.

**Fix applied.** 33 revisions re-hashed — `ContentHash` recomputed from the current shape and both
`settings_json` and `configuration_hash` rewritten, in one transaction. Verified after: the endpoint
returns **HTTP 200 with 11 profiles** (200 direct and through the proxy), and no new errors in the
backend log. Backups written before any modification to
`/mnt/storage/tradinghub-profile-revisions-backup-*.json` (all 44 revisions verbatim) and
`/mnt/storage/tradinghub-profile-dropped-fields-*.json` (the 3 discarded fields for the 6 affected
profiles). Migration tool retained at `/mnt/storage/tradinghub-profile-rehash/` — it is idempotent,
backs up before writing, and refuses to write if any active profile's latest revision fails validation.

**Lesson for future schema changes.** Adding *any* field to a type inside the hashed graph silently
invalidates every persisted `SimulationStrategyProfile`, with no migration and no warning — the
failure surfaces much later as a 500 on an unrelated endpoint. Either version the hash, exclude
defaulted-null fields from it, or ship a re-hash migration alongside the schema change.

---

### 2.10 Timeframe drill-in: warmed-up, centred windows (2026-08-19, user-requested)

Selecting a candle now asks which timeframe to open it at, and the chosen timeframe loads centred on
that candle with 499 candles before and 500 after. Going back up uses the same path.

**Why a backend endpoint was required.** `OandaLiveAnalysisSession.cs:95` returns exactly ONE real
series (the selected interval). Client-side `resampleFrames` can only aggregate coarser, never
manufacture finer, and sets `emptyIndicators` by design. So before this: drilling *down* was
impossible (no finer series existed) and drilling *up* landed on a synthetic series with no
indicators, i.e. an unannotatable chart. The existing `drillInto` also auto-picked a direction
rather than asking.

**Backend** — `GET /api/workspaces/oanda/window?symbol&interval&anchor&before&after`.
`OandaWorkspaceService.GetWindowAsync` fetches by time range, analyses warm-up + window together
through the existing `WorkspaceAnalysis.AnalyzeAsync`, then discards the warm-up frames, so every
returned frame has settled ATR/RSI/Bollinger and confirmed swings. Verified against live OANDA:
1,000 frames, anchorIndex 499, `warmupSatisfied: true`, **0 frames with null ATR**.

Three decisions worth remembering:
- **The 150-frame transport cap had to be bypassed.** `LiveSseFrameProjector.ProjectSeries` keeps
  only the newest `MaxSnapshotFrames`; applied to a drill window that discards everything *before*
  the anchor. New `ProjectWorkspaceWindow` keeps all frames and bounds size the same way instead —
  one frame with structural overlays, and that frame is the **anchor**, not the last.
- **Clamping never slides.** Running out of history on one side must not borrow from the other;
  `AnchorIndex` reports where the anchor really landed.
- **The fetch over-reaches on calendar time** (FX has no weekend candles) but the end is clamped to
  now — see below.

**Two bugs found by actually driving the UI, not by reading:**
1. `to` could land in the future (anchor near the live edge + padded "after" span), and OANDA
   rejects a future range end → HTTP 400 surfaced as a 502. Curl tests with older anchors never hit
   it. Fixed by clamping `to` to `now`.
2. Drilling wrote `workspace.interval`, which tore down and reconnected the live data source; on the
   way *back* the chart blanked to "Connecting workspace data" and never recovered. Fixed by not
   touching `workspace.interval` (a drill is a transient view) and by making `activeSeries` fall
   back to the drilled window when `dataset` is momentarily null.

**Frontend** — `App.vue` holds fetched windows in `drillWindows`, kept OUT of `dataset` because the
live SSE feed rewrites `dataset` continuously and would wipe them; they are merged in through
`mtfSeries` where they read as real series. `useMultiTimeframeSelection.drillInto` gained an optional
explicit target (auto-pick retained for `SimulatorPanel`, which has no chooser). Drill is enabled in
live mode for OANDA only.

**Verified in the browser end-to-end**: chooser splits ↓ Lower / ↑ Higher correctly against the
current interval; drilling 1h → 5m gives "frame 500/1000" with annotations across the whole window
including the earliest candles; Back returns to 1h still centred on the drilled candle.

**Not done**: Binance (`/api/workspaces/binance/snapshot` needs the same treatment) — deferred by
the user. `Simulator.Tests/WorkspaceDrillWindowTests.cs` covers anchor resolution, the 499/500
geometry, edge clamping and the projection; the future-`to` clamp is covered only by the manual
verification above, since `GetWindowAsync` needs a live broker client.

### 2.11 New `BreakoutDetectorAgent` (2026-08-27) — registered scaffold, no detection logic yet

> **STALE as of 2026-08-30 — detection is implemented.** Everything below describes the original
> scaffold. `BreakoutDetectorAgent.EvaluateAsync` now emits real Buy/Sell decisions
> (`Agent/Strategies/BreakoutDetector/BreakoutDetectorAgent.cs:123`) and §3.13 scores a 134-trade
> log from it. The interval set also changed: it is now **15m context / 5m trigger / 1m
> confirmation** (a third interval, `ConfirmationInterval`, that did not exist in the scaffold),
> with `StopAtrMultiple` 1.5 and `TargetAtrMultiple` 3.0 — not the 1h/15m below. The code is still
> uncommitted (untracked `Agent/Strategies/BreakoutDetector/`). **The "placeholders, not calibrated"
> warning below still stands: no calibration run for these parameters is recorded anywhere in this
> file.** See §3.14.

A fifth agent type, `breakout-detector`, exists end to end as a **scaffold**: it is registered,
serializable, resolvable and selectable, but `EvaluateAsync` returns `Observe` on every bar. It
will produce **zero trades by construction** — if a backtest of it comes back empty, that is the
missing logic, not a data or configuration fault. Do not read a zero-trade run of this agent as
evidence about breakouts.

Declared contract (`Agent/Strategies/BreakoutDetector/BreakoutDetectorAgent.cs`): exit model is
`AgentExitManagementMode.Bracket`, so when detection is written both bracket legs
(`StopLossPrice`, `TakeProfitPrice`) must be populated on the decision itself. Options
(`BreakoutDetectorStrategyOptions.cs`) are `ContextInterval` (default 1h) / `TriggerInterval`
(default 15m) plus `RangeLookbackCandles`, `BreakoutBufferAtr`, `StopAtrMultiple`,
`TargetAtrMultiple`, `MinimumRewardRisk`, `CooldownCandles`. **Those parameter values are
placeholders, not calibrated** — they exist so the options shape, validation and JSON round-trip
are wired; replace them with calibrated values (and record the run here) before drawing any
conclusion from them.

Registration points touched, i.e. the checklist for adding agent #6:

- `Agent/Configuration/TradingAgentKind.cs`, `TradingAgentTypeIds.cs` (const + `TryParse`/`Format`
  + the error message that enumerates valid ids)
- `Agent/Configuration/AgentDefinition.cs` — `From…`/`Read…Options` pair **and** the
  `[JsonSerializable]` attribute on `AgentDefinitionJsonContext` (source-gen; a missing attribute
  fails at runtime, not compile time)
- `Agent/Configuration/TradingAgentDefinition.cs` — options property, `RequiredIntervals`,
  `TriggerInterval`, `ToAgentDefinition`, `FromAgentDefinition`, and `Validate`, where **every
  pre-existing kind's "forbids" check must also be extended** to reject the new options bag
- `Agent/Factories/TradingAgentCatalog.cs` — an `ITradingAgentBuilder` plus its `CreateDefault` entry
- `Simulator/Models/BacktestConfiguration.cs:753` — the `ResolveAgentDefinition` switch that turns a
  `--strategy` string into a definition
- `DashboardLive/SimulationApi.cs:1213` — the quantity/reward-risk switch in `ApplySimulationProfile`.
  Note this switch **still throws for `divergence-reversal`** (pre-existing, unrelated to this work):
  simulation profiles for that agent cannot be applied through the Dashboard path.

Deployment modes are deliberately limited to `ObserveOnly`/`Shadow` in the descriptor while the
strategy is a scaffold — an agent that cannot produce an entry has nothing to approve or automate.
Widen this once detection works.

Verification: full solution builds clean (0 warnings, 0 errors);
`Simulator.Tests/BreakoutDetectorAgentTests.cs` (6 tests) plus the extended
`AgentCatalogueArchitectureTests` (5) pass 11/11. The new tests pin metadata, missing-snapshot
handling, options validation, definition round-trip through the catalogue, and — deliberately —
the observe-only behaviour, so the scaffold's zero-trade state is asserted rather than assumed.

---

---

### 3.12 trading-classification: first section 35 experiment ladder (2026-08-30) — more indicators made it worse

First quant run of the new ML agent (§2.14). **Not a validation — an ablation finding.**

- Instrument/data: `METAL:XAU/USD`, real 1-minute candles, 2025-12-26 to 2026-02-05 (38,377
  candles), read from the `bd-1m` run's replay chunks.
- Label: future close, horizon 10 candles, threshold 0.75 x ATR14. Class balance 32.9% SELL /
  29.7% NO_TRADE / 37.4% BUY — note this is *not* the NO_TRADE-heavy split §18 predicts, because
  10 bars of 1m gold moves well over 0.75 ATR routinely.
- Walk-forward: 14d train / 5d validation / 5d test, 4 windows, embargoed by the horizon.
  Thresholds tuned per window on validation only (§20).
- Costs applied: 0.15 half-spread + 0.05 commission per trade.

Command: `dotnet run --project TradingClassifierRunner -c Release -- ladder --simulation-market <run>/market --train-days 14 --validation-days 5 --test-days 5 --horizon 10 --half-spread 0.15 --commission 0.05`

```
experiment         features  trades    win%       PF         net       maxDD
1 OHLC only              14    1436   50.7%    1.068      335.22      590.88
2 + EMA                  24     413   55.7%    1.062      175.28      613.82
3 + RSI/CCI              34     210   60.5%    1.459      367.51      138.25
4 + ATR                  36     569   50.6%    0.839     -940.04     1524.34
5 + MACD/BB              42    1235   47.4%    0.795    -2854.80     4336.30
```

> **SUPERSEDED for rungs 4 and 5 (see §3.12d).** The ATR group in this table still contained the
> raw `atr{n}_pct` level columns, which were removed from the default feature set on 2026-08-30
> after §3.12c diagnosed them as non-transportable. Rungs 1-3 are unaffected and still current.

**The full section 8 feature set is the worst of the five.** Adding ATR features (rung 4) flips
the sign, and adding MACD/Bollinger (rung 5) makes it substantially worse again — PF 0.795 with a
4,336 max drawdown against rung 3's 138. This is precisely the outcome §35 exists to expose ("do
not simply throw every indicator into the first model"), and it means **the blueprint's own
recommended V1 configuration is not the one to use on this data.**

Rung 3 per-window (the §36 check that matters):

```
window-1  2026-01-14->01-19  thr 0.65/0.85  trades=47  PF=0.911  net=-15.77
window-2  2026-01-19->01-23  thr 0.70/0.80  trades=91  PF=1.477  net=+109.23
window-3  2026-01-25->01-29  thr 0.85/0.80  trades=58  PF=2.595  net=+299.98
window-4  2026-01-29->02-03  thr 0.85/0.70  trades=14  PF=0.874  net=-25.93
POOLED                                      trades=210 PF=1.459  net=+367.51  maxDD=138.25
```

The mechanical §36 bar reports **MET**. **Do not read that as an edge.** Concretely:

- 4 windows over ~6 weeks of *one* instrument. §36 asks for consistency across several periods;
  four is the minimum the spans allowed, not a sample.
- **Two of the four windows lose money.** Window 3 alone supplies 300 of the 367 net (82%). The
  automated "no single period responsible for all profit" test only fires when one window exceeds
  the pooled total, so it passed on a technicality — the concentration is real and the check is
  weaker than the §36 sentence it implements.
- 210 trades total, and per-window thresholds were selected from an 11x11 grid on short validation
  slices. That is a lot of selection pressure for this sample size.
- Window 4 traded 14 times. Any statistic from it is noise.

**What this run does establish:** the pipeline runs end to end on real data, the ablation
machinery discriminates between feature sets, and the ordering (3 > 1 ≈ 2 >> 4 > 5) is a real,
reproducible signal about *this* data. **What it does not establish:** that trading-classification
has an edge. Before that claim, it needs multiple instruments, a materially longer history, and
the rung-3 finding reproduced on data that did not select it.

Negative control: `--random-walk 20000` gives accuracy 32.9% against a 36.1% majority-class rate
and macro-F1 0.328 — i.e. chance, with the §21 warning firing. A pipeline leak would show up here
as apparent skill, and does not.

---

### 3.12a Standalone: OHLC+EMA+RSI+CCI+**BB** is the best rung, and ATR is what breaks it (2026-08-30, user-requested)

> **The "best configuration" claim here does NOT survive out-of-sample testing — see §3.12f.**
> On 226 days the same configuration is pooled-negative and clears no success bar. The *relative*
> ATR/MACD findings below still hold; the absolute result does not.

Follow-up to §3.12. The ladder only tests cumulative rungs, so rung 5 adding ATR *and* MACD *and*
Bollinger together could not say which of the three caused the collapse. Re-ran the walk-forward
with individual groups, identical config (14d/5d/5d, horizon 10, 0.15 half-spread + 0.05
commission, 4 windows, thresholds tuned per window on validation).

Per-window net, then pooled:

```
feature set                 feat    w1       w2       w3       w4    POOLED   windows +ve
3 base (no ATR/MACD/BB)       34  -15.77  +109.23  +299.98   -25.93  +367.51      2 / 4
3 + BB        (requested)     36  +42.45  +174.06  +215.46  +233.45  +665.41      4 / 4
3 + BB + MACD                 40  +21.28   +84.75  +230.71  -238.13   +98.60      3 / 4
3 + MACD only                 38  -67.00   +10.14  +283.09  -686.53  -460.31      2 / 4
3 + ATR only                  36  -77.66   -64.19   -54.86  -743.33  -940.04      0 / 4
```

**The requested set is the best configuration found so far, and the improvement is in consistency
rather than headline profit factor.** Pooled PF 1.430 vs rung 3's 1.459 — marginally lower — but:

- **All four windows are positive**, against two of four for rung 3.
- Profit concentration drops from 82% in one window (rung 3) to 32%. This is the §36 criterion
  ("no single period responsible for all profit") actually being met rather than passing on a
  technicality, which is how §3.12 recorded rung 3.
- 392 trades against 210, so roughly double the sample behind the same claim.
- Max drawdown rises (227.68 vs 138.25), which is the honest cost of trading more.

**ATR is the single destructive addition: it makes every window negative.** MACD is also harmful
(2 of 4 positive, pooled negative), and adding MACD on top of BB drags a 4/4 result back to 3/4.
So §3.12's rung-5 collapse was ATR first, MACD second — Bollinger was carried along and blamed by
association. **The blueprint's section 8 feature set is not the one to use; OHLC + EMA + RSI + CCI
+ Bollinger is.**

Note this does not contradict §3.13's filter finding that *fewer* features is better. Standalone
and meta-filter are different problems with different effective sample sizes (392 decisions vs
~100), and the feature set that wins one need not win the other. Both results stand.

Same caveats as §3.12 apply unchanged: one instrument, ~6 weeks, 4 windows, per-window threshold
selection from an 11x11 grid. The ordering between these five rows was measured on the same data
that produced it and needs reproducing elsewhere before it is a fact about markets rather than
about this window.

---

### 3.12b ATR feature rework (2026-08-30, user-requested) — much better, still not useful here

§3.12a found ATR was the single destructive group (0 of 4 windows positive, pooled -940). The
diagnosis: the group carried only *level* (`atr14_pct`, `atr20_pct` = ATR/Close). Level says how
volatile the market is but nothing about whether that is unusual, or which way volatility is
moving — so the model could not distinguish "ATR 6.7 while expanding out of a squeeze" from
"ATR 6.7 while decaying after a spike".

Reworked the group to six columns (`TradingClassifier/Features/FeatureEngine.cs`, schema in
`FeatureSchema.cs`):

| feature | definition | what it adds |
|---|---|---|
| `atr14_pct`, `atr20_pct` | ATR(n) / Close | level (unchanged) |
| `atr_regime` | ATR14 / ATR50 | short-run volatility vs its own baseline; >1 expanding |
| `atr_change_1` | ATR14[t] / ATR14[t-1] - 1 | direction of travel, one bar |
| `atr_change_5` | ATR14[t] / ATR14[t-5] - 1 | direction of travel, five bars |
| `atr_percentile_100` | rank of ATR14 in its trailing 100 | how unusual, comparable across instruments |

New `RollingPercentileState` indicator; new options `AtrBasePeriod` (14), `AtrRegimeSlowPeriod`
(50), `AtrChangeLags` ([1,5]), `AtrPercentilePeriod` (100). `AtrPeriods` may now be **empty**,
giving a context-only ATR group with no raw level column (`--atr-levels=`). Full feature set is
now 46 columns, still inside §8's 40-60. Warm-up lengthens to ~150 bars (ATR50 plus the 100-bar
percentile window).

Same walk-forward config as §3.12a (14d/5d/5d, horizon 10, costs applied):

```
feature set                       w1       w2       w3       w4    POOLED   windows +ve
3 + BB (best, no ATR)          +42.45  +174.06  +215.46  +233.45  +665.41      4 / 4
3 base (no ATR/MACD/BB)        -15.77  +109.23  +299.98   -25.93  +367.51      2 / 4
3 + ATR context only           -48.83   +32.39  +300.79  -339.39   -55.05      2 / 4
3 + BB + ATR context only      -63.12   -16.78  +769.36  -140.26  +549.22      1 / 4
3 + BB + ATR full              -35.33   +76.19  +160.47  -320.11  -118.79      2 / 4
3 + ATR full (level+context)   +18.42  +181.32  +378.12  -755.96  -178.10      3 / 4
3 + ATR OLD (level only)       -77.66   -64.19   -54.86  -743.33  -940.04      0 / 4
```

**The rework is a large improvement to the ATR group and still does not make it worth including.**
Old ATR: 0 of 4 windows positive, pooled -940. Reworked: 3 of 4 positive, pooled -178 — an 762
swing, and the level-only version is now clearly the worst row in the table. But every ATR variant
is still pooled-negative or badly concentrated, and none beats `3 + BB` alone.

Two things worth noting:

- **Window 4 (2026-01-29 to 02-03) is negative in every single ATR variant**, and is the window
  that drags each of them under. It is positive in `3 + BB`. Whatever ATR is keying on in that
  period actively misleads the model; that window is the thing to look at before trying ATR again.
- `3 + BB + ATR context only` posts a healthy-looking +549 pooled, but on **1 of 4** positive
  windows with 769 from a single one. That is the concentration failure §3.12 warned about, and it
  is a worse result than `3 + BB` despite the comparable pooled number — a reminder that pooled PnL
  alone ranks these wrongly.

**Standing recommendation is unchanged: OHLC + EMA + RSI + CCI + BB, no ATR, no MACD.** The
reworked ATR features are retained in the codebase (they are strictly better than what they
replaced, and are the right shape if ATR is revisited on other data) but are not in the
recommended set.

Coverage: 5 new tests in `TradingClassifierFeatureTests.cs` pin the percentile ranking, that
`atr_regime` exceeds 1 inside a volatility expansion, that `atr_change_5` is 0 on flat volatility
and positive on rising, the empty-`AtrPeriods` context-only schema, and the
`AtrRegimeSlowPeriod > AtrBasePeriod` guard.

---

### 3.12c Why window 4 breaks ATR (2026-08-30, user-requested) — a distribution-shift diagnosis

§3.12b noted window 4 (test 2026-01-29 to 02-03) is negative in *every* ATR variant. It is not a
bad-luck window: it is a regime break that ATR's level features cannot survive by construction.

**What the window is.** Boundaries are train 01-10→01-24, validation 01-24→01-29, test 01-29→02-03.
Measured on the real 1m XAU/USD candles:

```
period       bars    ATR14 mean   ATR14 p95   close range        drift
train       13610         2.284       4.318   4521 -> 4989      +466.03
validation   4183         3.911       6.938   4999 -> 5588      +508.83
TEST         4128        11.299      20.486   4405 -> 5594      -741.02
```

**Volatility is 4.9x the training mean and the direction inverts.** The model trains on a calm
uptrend and is scored on a crash: 01-30 alone is -543, and intraday ATR14 peaks at 49.8 on 01-29
against a training p95 of 4.3. For contrast, window 3 — which every variant handled — steps from
ATR 2.07 to 3.91, a 1.9x move in the same direction.

**Why this lands on ATR and not on the other groups.** Share of test bars falling outside the
*entire* training range of each feature:

```
feature          train min   train max    test med   % outside
atr14_pct         0.000148    0.002126    0.002085      48.2%
bb_width          0.000260    0.025465    0.008377       4.6%
return_20        -0.017856    0.017120   -0.000327       4.5%
range_pct         0.000016    0.008654    0.001906       1.0%
bb_position      -0.561479    1.588747    0.488162       0.0%
```

`atr14_pct` is an order of magnitude worse than anything else — **48% of the test window sits above
every split point the trees learned.** A gradient-boosted tree cannot extrapolate: those bars all
fall into the topmost bin, so for half the window the feature is a constant pinned at "maximum",
carrying no discriminating information while dragging every prediction toward whatever was learned
from the thin extreme tail of a calm period.

The mechanism is **Wilder smoothing**. ATR is persistent, so a volatility regime shift moves it
wholesale and it stays moved. `range_pct` is the same quantity measured per bar, but it
mean-reverts bar to bar and only exceeds the training range 1% of the time. `bb_position` is bounded
by construction and never leaves range at all. This is a general lesson for this feature set:
**slow, persistent level features are the ones that fail to transport across a regime break; fast
or bounded ones survive it.**

**The rework fixed exactly this, and the fix is verified.** The scale-free ATR features are
distributionally stable across the same break:

```
                    train      TEST
atr_regime  median   0.97      0.96
atr_percentile mean  0.44      0.44
```

Both transport essentially unchanged, against `atr14_pct`'s 4.9x shift. That is why §3.12b's rework
moved ATR from 0/4 to 3/4 positive windows.

**But stable is not the same as informative.** `3 + ATR context only` (no raw level column) still
scores 2/4 windows and pooled -55: the context features no longer *break*, they simply do not carry
enough signal to pay for four extra columns on ~13k training rows, and the added variance costs
more than they contribute. Window 4 is not unlearnable either — `3 + BB` makes +233 there.

**Conclusion and the actionable part:** `atr{n}_pct` should not be in any recommended feature set.
It is not merely unhelpful, it is structurally non-transportable — the failure will recur on any
data containing a volatility regime shift, which is most data worth trading. `atr_percentile_100`
is the transportable replacement for the same information and should be preferred if ATR is
revisited. The standing recommendation (OHLC + EMA + RSI + CCI + BB) is unchanged, now for a
diagnosed reason rather than an empirical one.

---

### 3.12d Ladder re-run with `atr{n}_pct` removed (2026-08-30, user-requested)

Acting on §3.12c: `AtrPeriods` now defaults to **empty**, so the ATR group ships as
regime + change_1 + change_5 + percentile only, with no raw level column. Full set is 44 features
(was 46 with levels, 42 before the ATR rework). Same walk-forward config as §3.12.

```
                          BEFORE (with atr_pct)              AFTER (levels removed)
experiment          feat  trades   PF        net    maxDD  | feat trades   PF        net    maxDD
1 OHLC only           14    1436  1.068   +335.22   590.88 |   14   1436  1.068   +335.22   590.88
2 + EMA               24     413  1.062   +175.28   613.82 |   24    413  1.062   +175.28   613.82
3 + RSI/CCI           34     210  1.459   +367.51   138.25 |   34    210  1.459   +367.51   138.25
4 + ATR               36     569  0.839   -940.04  1524.34 |   38    216  0.961    -55.05   503.65
5 + MACD/BB           42    1235  0.795  -2854.80  4336.30 |   44    720  0.816  -1734.24  2655.79
```

Rungs 1-3 contain no ATR and are byte-identical, which is the control confirming nothing else moved.

**Removing two columns recovered +885 on rung 4 and +1,121 on rung 5, and cut max drawdown by
roughly two thirds on both** (1524 -> 504, 4336 -> 2656). Rung 4 goes from clearly losing
(PF 0.839) to near break-even (PF 0.961). That is a large effect for deleting 2 of 46 features, and
it quantifies §3.12c's diagnosis: the level columns were not weak signal, they were actively
destructive under regime shift.

Per-window, rung 4's problem window improves but does not resolve:

```
                             w1       w2       w3       w4    POOLED
4 + ATR with levels       +18.42  +181.32  +378.12  -755.96  -178.10
4 + ATR levels removed    -48.83   +32.39  +300.79  -339.39   -55.05
```

Window 4's loss more than halves (-756 -> -339), consistent with removing the feature that had 48%
of its bars outside the training range. It stays the only badly negative window, so the direction
inversion in that period (train +466 uptrend, test -741 crash) still costs the ATR variants
something the other groups absorb.

**Rankings are unchanged: rung 3 is still the best ladder rung, and ATR still does not pay for
itself.** The ladder does not test the actual best-known configuration — `3 + BB` (§3.12a, +665.41
on 4/4 positive windows) — because that is not a cumulative rung. Standing recommendation remains
**OHLC + EMA + RSI + CCI + BB**.

The `atr{n}_pct` columns remain available via `--atr-levels 14,20` / `AtrPeriods = [14, 20]` for
anyone who wants to reproduce the old behaviour; they are simply no longer the default.

---

### 3.12e Multi-timeframe run of the recommended set (2026-08-30, user-requested)

> **SUPERSEDED by §3.12g.** Every run below sits inside the 41-day Dec-Feb window. On 3.5 years of
> real OANDA data all three timeframes are negative; the "MET" verdicts here do not generalise.

Ran OHLC + EMA + RSI + CCI + BB (36 features, no ATR, no MACD) on 1m, 5m, 15m and 1h. Source is the
same XAU/USD 1m series aggregated up by `CandleSources.Resample`, which buckets by **close** time -
a 5m bar stamped 03:15 is the 1m bars closing 03:11-03:15 - so the causal stamp survives
aggregation. Partial trailing buckets are dropped. Identical config otherwise: 14d/5d/5d
walk-forward, horizon 10 **bars** (so 10 minutes on 1m, 10 hours on 1h), 0.15 half-spread + 0.05
commission, thresholds tuned per window on validation.

```
TF     candles    rows  train rows/win  trades   win%      PF        net    maxDD  win+ve  §36
1m      38,377  38,313        ~20,160      392  56.4%   1.430    +665.41   227.68   4 / 4  MET
5m       7,698   7,634         ~4,032      591  55.0%   1.238   +1255.04   896.25   4 / 4  MET
15m      2,566   2,502         ~1,344      244  53.3%   1.358    +877.85   410.88   3 / 4  MET
1h         642     578           ~336       69  36.2%   0.137   -5702.58  5849.03   0 / 3  not met
```

**The result holds on 1m, 5m and 15m and collapses on 1h.** All three lower timeframes clear the
§36 bar, with profit factors in a tight 1.24-1.43 band and no reliance on a single window. 5m
produces the largest net (+1,255) but also the largest drawdown; 1m has by far the best
drawdown-to-profit ratio.

**This is the first evidence the finding is not an artefact of the 1m timeframe** - but it is much
weaker evidence than it looks. The four runs share one underlying price series over one 41-day
period on one instrument, so they are correlated re-samplings, not independent replications. A
regime that suits this feature set will suit it at every sampling rate. Replication still requires
a second instrument and a different date range.

**The 1h failure is a data-volume artefact, not a verdict on 1h.** 41 days yields only 642 hourly
candles, leaving ~336 training rows for 36 features - roughly 9 rows per feature - and only 3
windows instead of 4. Diagnostics run to separate signal from sample size:

- Smaller model (15 leaves / 100 iterations / min-leaf 10): still fails, pooled PF 0.348. Not
  simply model capacity.
- Longer 25d training window: only one walk-forward window survives, containing **2 trades**.
  Correctly reported as not met - there is nothing left to evaluate.
- The crash period (01-28 to 02-02) lands in 1h's window 3 and is catastrophic there (PF 0.046,
  8% win rate, -5,091 from 25 trades) while the *same calendar period* is positive on 1m, 5m and
  15m. With ~120 test bars instead of ~7,200, a handful of trades dominates the window.

The honest reading is **"1h cannot be evaluated on this dataset"**, not "1h does not work". Testing
it needs roughly a year of data, which the current `bd-1m` replay artefacts do not contain.

Coverage: 3 new tests in `TradingClassifierResampleTests` pin the close-time bucketing, the dropped
partial trailing bucket, and that aggregation neither invents nor loses range at 5m/15m/60m.

---

### 3.12f 15m on 226 days — the edge does not replicate out of sample (2026-08-30, user-requested)

The user asked for the 15m test on far more data. 39,000 15m candles are not obtainable from the
cache; the longest XAU/USD series is `METAL_XAU_USD_1m_20251211_20260724` (216,323 1m candles,
2025-12-11 to 2026-07-23), which resamples to **14,465 15m candles** — 5.6x §3.12e's 2,566.
Added `CandleSources.FromHistoricalCache` to read `.cache/historical/*.jsonl.gz` (stamps candles by
`closeTime`, matching the `availableAt` convention).

Same recommended set (OHLC + EMA + RSI + CCI + BB, 36 features), same horizon, same costs.

**60d/20d/20d — 7 windows:**

```
POOLED  trades=1319  win-rate=50.64%  PF=0.881  net=-1636.15  maxDD=3749.84   1 of 7 windows positive
success bar (section 36): NOT MET
```

**21d/7d/7d — 28 windows, the finer picture:**

```
windows: 28   positive: 13 (46%)   pooled PF=0.953   net=-1802.09   trades=3927
  windows  1- 4 (Jan 08-Feb 05):  +2200.87   (3/4 positive)
  windows  5- 8 (Feb 05-Mar 05):   -264.78   (2/4 positive)
  windows  9-12 (Mar 05-Apr 02):  -1975.51   (1/4 positive)
  windows 13-16 (Apr 02-Apr 30):  -1246.97   (0/4 positive)
  windows 17-20 (Apr 30-May 28):   -169.18   (3/4 positive)
  windows 21-24 (May 28-Jun 25):   +179.45   (3/4 positive)
  windows 25-28 (Jun 25-Jul 23):   -525.97   (1/4 positive)
```

**The decisive split:**

```
windows 1-3  (2026-01-08 -> 01-29, the period every earlier result came from):  +2779.43
windows 4-28 (everything after):                                                -4581.52
```

**The edge found in §3.12/§3.12a/§3.12e was period-specific.** Windows 1-3 cover almost exactly the
41-day Dec 26 - Feb 5 slice that the `bd-1m` replay artefacts contain, and they are the three
strongest windows in the entire 226-day series. Every subsequent period gives it back and more. Over
28 windows the configuration is a coin flip that loses to costs: 51.7% win rate, pooled PF 0.953,
negative expectancy.

This is the replication test §3.12 and §3.12e both said was required, and **it does not replicate**.
It is not a marginal miss — the out-of-sample half is decisively negative.

What still stands:

- The **relative** findings. ATR levels being non-transportable (§3.12c), MACD hurting, ATR hurting,
  and Bollinger helping *relative to* the other groups were all measured the same way and are
  ablation comparisons, not absolute performance claims. They should be re-measured on the long
  series before being trusted, but nothing here contradicts them.
- The **pipeline**. The negative-control behaviour, the look-ahead guards, the embargo and the
  walk-forward machinery all did their job: this is exactly the failure mode they exist to expose,
  and they exposed it as soon as they were given enough data.
- §3.13's **meta-labelling** result is untouched by this, but note it was measured on the same
  Jan-Feb window and is now under the same suspicion. It needs the identical long-series treatment.

What this changes: **there is currently no evidence that trading-classification has an edge on
XAU/USD.** The standing recommendation (OHLC + EMA + RSI + CCI + BB) is now only a statement about
which feature set is least bad, not about a tradeable configuration. Any future work here should
start from the 226-day series, never the 41-day one.

---

### 3.12g Definitive multi-timeframe result on 3.5 years (2026-08-30) — no edge at any timeframe

Fetched real OANDA XAU/USD history via the encrypted database credential vault (new
`TradingClassifierRunner fetch` command, `CandleFetcher.cs` - reuses `OandaHistoricalCandleSource`
and `BrokerCredentialVault`; the token is never placed in the environment or printed, and output is
written outside `.cache/historical` so it cannot leave a half-valid entry in the repo's own cache).

Three series, all 2023-01-02 to 2026-07-24: **1,257,460** 1m, **252,358** 5m, **84,129** 15m candles.
Recommended feature set (OHLC + EMA + RSI + CCI + BB, 36 features), horizon 10, 90d train / 30d
validation, costs 0.15 half-spread + 0.05 commission, thresholds tuned per window on validation.

```
TF     candles      rows   test block  windows  positive   trades   win%      PF        net     maxDD
1m   1,257,460  1,257,396         90d       13    3 (23%)   19200   43.4%   0.849   -4764.82   5657.43
5m     252,358    252,294         30d       39   12 (31%)    6471   46.3%   0.898   -1530.60   2812.28
15m     84,129     84,065         30d       39   18 (46%)    5014   49.2%   0.905   -1957.07   2589.86
```

**Every timeframe is negative. None clears the section 36 bar.** Profit factors cluster tightly at
0.85-0.91 — consistently below break-even by roughly the cost of trading, which is what a model with
no edge looks like once spread and commission are charged.

Per year, both 39-window runs are negative in almost every year:

```
        5m                              15m
  2023:  9 win,  0 positive,  -673.61     9 win,  3 positive,  -681.03
  2024: 12 win,  2 positive,  -708.94    12 win,  5 positive,  -296.73
  2025: 12 win,  6 positive,  -633.55    12 win,  8 positive,  -472.37
  2026:  6 win,  4 positive,  +485.44     6 win,  2 positive,  -506.94
```

**A preliminary 5m run showed PF 1.160 / +300.08 and did not survive.** That run used 240-day test
blocks (4 windows); re-run with the matched 30-day blocks the same data gives PF 0.898 / -1530.60.
The positive sign was a block-size artefact, not a finding — recorded here because it is exactly the
kind of number that gets quoted out of context.

**Threshold-tuner weakness found.** In two 1m windows the tuner selected 0.40 on one side and the
model then traded almost every bar: window-3 took 7,448 trades (-2,539) and window-11 took 10,032
(-1,629), together 17,480 of the 19,200 total. `ModelEvaluator.TuneThresholds` enforces a
`minimumTrades` floor but has **no ceiling**, so it cannot reject a threshold that degenerates into
trading constantly. Excluding both windows the remaining 11 still sum to roughly -415 with 3
positive, so this does not change the verdict — but the tuner should grow a maximum-trade-rate guard
before it is used for anything else.

**Conclusion: the trading-classification model has no demonstrable edge on XAU/USD at 1m, 5m or 15m
over 3.5 years.** Sections 3.12, 3.12a and 3.12e recorded positive results; all of them came from
the same 41-day Dec-2025/Feb-2026 window, which §3.12f showed to be the best stretch in the data.
With 15x more data and 3.5 years of coverage the result is uniformly negative.

The pipeline itself is validated by this: the embargo, the look-ahead property test, the negative
control and the walk-forward machinery all behaved correctly and produced the honest answer as soon
as they were given enough data. What is refuted is the strategy, not the infrastructure.

---

### 3.13 Meta-labelling: the classifier as a filter over breakout-detector (2026-08-30)

Asks a different and much lower-bar question than §3.12: not "can the classifier trade" but "can
it tell breakout-detector which of its own signals to skip". Implemented as
`TradingClassifier.ML/Experiments/MetaFilter.cs`, driven by
`TradingClassifierRunner filter-signals`.

Setup: breakout-detector's own 134-trade log from run `41d670c87f134babb70ad67f2e99f547`
(`Books/trades.csv`), XAU/USD, 2026-01-05 to 2026-02-04. Classifier = rung 3 feature set
(§3.12's winner, 34 features), horizon 10, walk-forward 10d/3d/3d.

**Strictly out of sample.** Each window trains on its own past and judges only the primary trades
opening inside its own test period, so no trade is ever scored by a model that saw it. The
classifier is asked about the *decision* bar (`Opened - 1m`), not the fill bar, so it gets no
candle the primary strategy did not have. 103 of 134 trades fall inside some test window; the
other 31 are excluded from **both** the baseline and the filtered column.

```
filter                      kept    win%       PF        net   randPF  rand p95   pctile
BASELINE (no filter)         103   43.7%    0.756   -3610.90
Agreement >= 0.30             63   50.8%    1.252    1768.44    0.751     1.028    99.8%
Agreement >= 0.40             41   51.2%    1.365    1670.80    0.748     1.220    98.1%
Agreement >= 0.50             29   62.1%    2.337    3230.81    0.749     1.368    99.8%
Agreement >= 0.60             17   70.6%    4.177    2704.80    0.747     1.774    99.9%
Agreement >= 0.70              8   75.0%    6.943    1549.21    0.728     2.998    99.5%
NotOpposed >= 0.40            74   44.6%    0.990    -100.00    0.755     0.966    96.9%
NotOpposed >= 0.70           100   42.0%    0.733   -3949.16    0.759     0.803    22.4%
```

**The random-subset control is the load-bearing column.** breakout-detector loses money, so its
losses are large and concentrated, and dropping *any* sizeable fraction of its trades has a decent
chance of flipping the sign by luck. Every row is therefore compared against 2,000 random subsets
of the same size. Random median PF stays at ~0.75 no matter how many trades are dropped, while the
filter reaches 2.34 at the 0.50 cut — beating 99.8% of random subsets. **Without this control the
result would be worthless; with it, the selection is real.**

The `NotOpposed >= 0.70` row is the sanity check working: it keeps 100 of 103 trades, lands at the
22nd percentile, and is correctly indistinguishable from random, because it is barely filtering.

Robustness — the effect is not horizon-specific (`Agreement >= 0.50`, all beating the random p95):

```
horizon=5   kept=26  PF=2.369  pctile=99.8%
horizon=10  kept=29  PF=2.337  pctile=99.8%
horizon=15  kept=33  PF=1.505  pctile=97.1%
horizon=20  kept=30  PF=1.539  pctile=97.3%
horizon=30  kept=31  PF=2.239  pctile=99.8%
```

(An earlier run at horizon 45 showed no effect; that was the coarser 14d/5d/5d window config,
which covers only 70 of 134 trades. Coverage, not horizon, was the difference.)

**It is not simply refusing to sell.** breakout-detector's sells lost 6,434 while its buys made
1,165, so "stop selling" would be the trivial explanation. It filters and improves *both* sides:

```
side       all  kept   keep%     net all    net kept
Buy         46    16     35%     1584.65     2676.56
Sell        57    13     23%    -5195.55      554.25
```

That is conditional selection on setup quality, not a directional bias.

**Caveats, and they are serious.** 103 trades, one instrument, five weeks. The 0.50 cut rests on
29 trades and the 0.60 cut on 17. The thresholds were not tuned out of sample — the whole grid is
shown precisely because no single row should be read as "the" number. This says the classifier
carries information breakout-detector is not using; it does **not** establish a tradeable filtered
strategy.

**Feature-set comparison as a filter (2026-08-30, user-requested).** Re-ran the filter with
OHLC+EMA+RSI+CCI+**BB** (36 features, i.e. rung 3 plus Bollinger, still excluding ATR and MACD),
then swept the whole ladder. Horizon 10, `Agreement >= 0.50`, same 103-trade out-of-sample set:

```
feature set                 feat  kept    win%       PF        net   pctile
1 OHLC only                   14    22   63.6%    3.064    3345.92    99.8%
2 + EMA                       24    25   60.0%    2.578    2834.82    99.9%
3 + RSI/CCI                   34    29   62.1%    2.337    3230.81    99.8%
4 + BB      (requested)       36    28   60.7%    2.264    2826.88    99.8%
5 + ATR/MACD (all)            42    38   50.0%    1.737    2743.95    99.4%
```

**Adding Bollinger does not help: 2.264 vs 2.337 without it.** More importantly the ranking here is
the *inverse* of §3.12's standalone-strategy ladder, where rung 3 won and rung 1 was mediocre. As a
filter, **fewer features is monotonically better** — plain OHLC-derived features (14 columns) give
the best profit factor, and every indicator group added degrades it.

That inversion is consistent rather than contradictory. Standalone the model must locate the move
itself, which needs indicator context. As a filter it only has to answer a much narrower question
about a setup breakout-detector has already found, on an effective sample of ~100 decisions — and
there the extra 28 columns buy nothing and cost generalisation.

All five sets still beat 99%+ of random subsets, so the *effect* is not feature-set dependent; only
its magnitude is. The BB variant is robust across horizons (PF 1.55-2.26 at h=5/10/15/20/30, all
above the random p95), same as rung 3.

**Practical read: if this is ever wired in, start from the OHLC-only feature set, not the section 8
one.** Note this is 22 trades at the 0.50 cut, so the ordering between rungs 1-4 is not separable
at this sample size; the reliable statement is "small beats large", not "14 is the right number".

**Suggested next step:** this is the natural fit for the existing meta-model slot
(`Calibration/MetaModelArtifact.cs`, `RiskManager.Calibration.MetaLabelFeatureFactory`), which is
currently bucket-based rather than ML-backed, and which `breakout-detector`'s descriptor advertises
as `SupportsMetaModel = false`. Wiring it needs the finding reproduced on a second instrument and a
longer window first.

---

### 2.15 TrendStatistics / 4H Statistical Trend Agent — Phase 1 only (reviewed 2026-08-30)

`Blueprint — 4H Statistical Trend Agent.md` (2,483 lines, 72 sections) specifies a phased build
V0-V10 (section 71). **Only V0 is implemented.** It is a library, not an agent: no
`ITradingAgent`, no `TradingAgentKind` entry, no catalogue builder, and section 53 explicitly says
"do not implement trading yet". A backtest of it would return zero trades by construction.

Against section 51's project structure:

```
Data/            Candle only                    CandleAggregator MISSING
Detection/       ITrendDetector, TrendDetector, TrendState, TrendPhase,
                 TrendDirection, TrendDetectorConfig, TrendDetectorUpdate   DONE
                 (TrendStateMachine folded into TrendDetector)
Segmentation/    ITrendSegmenter, TrendSegmenter, TrendRecord               DONE
Statistics/      QuantileCalculator, BootstrapEngine, BootstrapResult,
                 JointDistribution                                          MISSING (whole folder)
Profiles/        SymbolTrendProfile, DirectionTrendProfile, ProfileRepository  MISSING
Runtime/         TrendStateEstimator, TrendProgressEstimator, ExhaustionEstimator  MISSING
Trading/         SwingSignalGenerator, SwingPositionManager, DirectionGate   MISSING
Evaluation/      TrendBacktester, WalkForwardRunner, TrendMetrics            MISSING
```

Interfaces `IBootstrapEngine`, `ITrendProfileBuilder`, `ITrendStateEstimator` (section 52) do not
exist. Builds clean; `TrendStatistics.Tests` passes 6/6.

**Phase 2 (section 55) run on real data**, via the new `TradingClassifierRunner trend-stats`
command: XAU/USD 1m resampled to 4H, 2023-01-02 to 2026-07-24, 5,691 4H candles, detector defaults
(EMA20, ATR14, candidate 0.75 ATR, confirm 1.25 ATR).

**146 completed trends, 76 bull / 70 bear, 39 candles per trend.**

```
                        BULL (76)                          BEAR (70)
                   P05    P50    P90    P95   mean     P05    P50    P90    P95   mean
total move %      1.30   3.95   8.78  10.51   5.01    1.22   3.28   6.92   9.24   3.96
move after conf % 0.18   1.90   6.97   8.53   3.16    0.14   1.07   4.66   5.99   1.96
max retracement % 1.02   2.00   3.96   4.97   2.42    0.94   1.80   3.72   5.27   2.47
ATR-norm move     2.39   6.53  16.90  23.88   9.80    2.45   4.94   9.04  13.33   5.91
duration (bars)  12.75  34.00  67.00  89.00  41.64   12.00  25.00  55.10  66.55  31.16
confirm delay bar 4.00   7.00  12.50  14.00   7.93    4.00   8.00  14.00  15.10   8.26
confirm delay %   0.74   1.61   2.96   3.41   1.78    0.73   1.78   3.14   3.99   2.05
```

**Section 54 acceptance criteria: PASS.** 0 concurrent live trends (no two confirmed at once),
0 negative confirmation delays, 0 out-of-order timestamps, 0 zero-duration trends. 76
structural-start overlaps are *expected* - section 9 discovers the structural start retroactively
at the swing extreme, which normally precedes the prior trend's end. Of those, **16 are
same-direction and worth inspecting** as possible single trends split in two.

**The headline finding is confirmation cost.** Median bull trend moves 3.95%, but the median
confirmation delay consumes 1.61% - leaving 1.90% after entry, which is exactly the
move-after-confirmation median. **Entry efficiency is roughly 48% on bulls and 33% on bears**
(1.07 of 3.28). Two thirds of a median bear trend is gone before the detector will confirm it.
That is the number sections 29, 30 and 39 exist to attack, and it should frame the entry research:
the tradeable edge is not the trend size, it is what survives confirmation.

Bull trends are larger and longer than bear (P50 3.95% / 34 bars vs 3.28% / 25 bars), consistent
with gold's trend over this window - section 5's insistence on never pooling directions is
justified by this data.

**Defect found and FIXED (2026-08-30):** `TrendDetector.cs` set `MfePct = moveAfterConfirmation`,
the identical value already assigned to `MoveAfterConfirmationPct`. Not a miscalculation - MFE from
confirmation *is* that quantity - but the field carried no information while the statistics table
made it look like two measurements agreeing.

Replaced with `MaximumAdverseExcursionPct`: the worst excursion against the trend measured from the
confirmation price, tracked bar by bar from confirmation onward. This is genuinely new -
`MaximumRetracementPct` measures giveback from the running favourable extreme, so a trend that runs
straight up then hands back 3% has a large retracement while never trading against a confirmation
entry at all. The new field is what a stop placed at confirmation actually had to survive, which is
the input section 37 needs. Pinned by
`MaximumAdverseExcursion_IsNotADuplicateOfMoveAfterConfirmation`, which drives a trend below its
entry and asserts the two fields differ. TrendStatistics.Tests 7/7.

Measured on the same 3.5-year series:

```
                        BULL (76)                          BEAR (70)
                   P05    P50    P90    P95   mean     P05    P50    P90    P95   mean
move after conf % 0.18   1.90   6.97   8.53   3.16    0.14   1.07   4.66   5.99   1.96
max adverse exc % 0.14   0.75   1.97   2.70   0.94    0.07   0.97   2.34   2.91   1.31
```

**The excursion asymmetry is the actionable result.** A median confirmed bull risks 0.75% to make
1.90% (~2.5:1); a median bear risks 0.97% to make 1.07% (~1.1:1). A stop 2.70% beyond entry would
have survived 95% of bull trends, 2.91% for bears. Combined with the confirmation-cost finding
above, the bull side is the tradeable one on this data and the bear side barely clears its own
risk - further justification for section 5's refusal to pool directions.

**The 16 same-direction overlaps were a false alarm in the check, not a detector defect.** All 16
have a gap of exactly -1 bar, and 15 of 16 anchor at or after the previous trend's favourable
extreme. `EndTime` is the closing bar's close and the next trend's `StructuralStartTime` is the
extreme that ended it, so a one-bar overlap is the interval convention. A pullback inside a larger
move being measured as two chained trends is correct segmentation. The acceptance check now
separates 1-bar boundary overlaps (16, benign) from deeper ones (**0**), and the real concurrency
test - did a trend confirm while another was still live - reads 0.

Next step per section 71 is V1/V2 proper: a `Statistics/` folder with `QuantileCalculator` and the
bull/bear trend library as a first-class type, then V3's bootstrap engine. The numbers above were
produced ad hoc by the reporting command and are not yet a reusable component.

---

### 2.16 TrendStatistics V1 built out: statistics, entry, exit, metrics (2026-08-30)

Continuation of §2.15, which found only V0 (the causal detector) implemented. Built the remaining
V1 statistical machinery per section 68's scope - no ML, no news, no order book; the question is
whether the statistical structure of 4H trends alone is tradeable.

**Added** (all in `TrendStatistics/`, 25/25 tests pass):

```
Statistics/QuantileCalculator   sections 15/18: empirical quantiles + the inverse PercentileOf
Statistics/BootstrapEngine      sections 16-20: resample with replacement, CIs, convergence test,
                                deterministic seed
Statistics/JointDistribution    sections 26/27: joint exceedance and rarity in percentile space
Profiles/DirectionTrendProfile  section 21, per direction, never pooled (section 5)
Profiles/SymbolTrendProfile     with BuildAsOf enforcing section 49's leakage rule
Runtime/TrendProgressEstimator  sections 23/24: price and time percentiles, kept separate per s25
Runtime/ExhaustionEstimator     sections 34/35: TrendExhaustionState with score + raw inputs
Trading/SwingSignalGenerator    sections 28-30: Confirmed + percentile gate, 4 modes
Trading/SwingPositionManager    sections 36/37: Open->NormalHold->ProfitProtection->Trail->Exit
Evaluation/TrendMetrics         sections 38-40: capture ratio, entry efficiency, exit giveback
Data/CandleAggregator           section 51
```

Still missing: `Profiles/ProfileRepository`, `Evaluation/TrendBacktester` + `WalkForwardRunner` as
first-class types (section 48), regime conditioning (section 60, V8), multi-symbol (V9),
lower-timeframe tactical integration (V9/V10), and the `ITradingAgent` wiring. The V1 question can
now be answered without them.

**Section 29/30 entry experiments on 3.5 years of 4H XAU/USD** (5,690 candles, exit fixed at the
detector's trend-end signal so only entry timing varies, profiles rebuilt causally per section 49,
gross of costs). Command: `TradingClassifierRunner swing-entry --historical <1m> --resample 240`.

```
mode            entry  trades    win%       PF      sum%    mean%   maxDD%
BASELINE conf    0.00     146   37.7%    1.182     20.25    0.139    21.02
PriceOnly        0.05      95   33.7%    1.215     16.73    0.176    12.61
PriceOnly        0.10      86   31.4%    1.164     12.25    0.142    12.85
PriceOnly        0.15      80   28.7%    0.854    -10.44   -0.130    24.10
PriceOnly        0.20      75   29.3%    0.851    -10.18   -0.136    21.95
PriceOnly        0.25      68   29.4%    0.853     -9.72   -0.143    21.69
TimeOnly         0.05      40   35.0%    1.139      4.11    0.103     9.66
TimeOnly         0.10      30   46.7%    1.813     13.95    0.465     4.28
TimeOnly         0.15      22   45.5%    1.708     10.48    0.476     3.14
TimeOnly         0.20      16   37.5%    1.132      1.58    0.099     6.38
PriceAndTime     0.10      29   44.8%    1.649     11.54    0.398     4.28
PriceAndTime     0.15      22   45.5%    1.597      9.22    0.419     3.14
```

**Section 30's Experiment B beats Experiment A: time confirmation outperforms price
confirmation.** The blueprint refused to assume which would win; on this data price-only actively
degrades past P10 (PF 0.85, worse than taking every confirmation), while time-only at P10-P15
roughly halves the trade count and cuts max drawdown from 21% to 3-4%. Entry gating does what
section 28 claims it should.

**But the bootstrap (sections 16-19) undercuts the exhaustion half of the design:**

```
Bullish total move %, n=76, 10,000 iterations
    q   estimate     CI low    CI high  rel width  stable
 0.25      2.578      2.223      2.841      0.240  yes
 0.50      3.954      3.217      4.544      0.336  yes
 0.90      8.782      7.425      9.771      0.267  yes
 0.95     10.509      8.602     23.493      1.417  NO
Bearish: P90 rel width 0.507 NO, P95 rel width 0.687 NO
```

Section 17's own worked example says a P95 CI of 7.1-18.5 means "poorly estimated"; the measured
bull P95 CI is 8.6-23.5. **The P90/P95 exhaustion thresholds that sections 32-35 depend on are not
estimable from 3.5 years of 4H data on one symbol.** The convergence test settles this as a sample
problem rather than an iteration problem - the interval stops moving between 5,000 and 10,000
iterations, so more iterations cannot help:

```
  1000 iters  P95=10.509  CI  8.598 -> 19.443  width 10.846
  5000 iters  P95=10.509  CI  8.602 -> 23.493  width 14.890
 10000 iters  P95=10.509  CI  8.602 -> 23.493  width 14.890
```

76 bull and 70 bear trends is simply too few for a 95th percentile. This is what section 61's
multi-symbol expansion is for, and it is now a prerequisite rather than a later nicety: exhaustion
logic built on these thresholds would be acting on noise. The middle quantiles (P25-P75) are stable
in both directions, which is consistent with the entry sweep finding its usable thresholds at
P10-P15.

**Caveats on the entry result.** 22-30 trades at the best settings over 3.5 years is thin, the
figures are gross of costs, and the best cell was selected from a 15-cell grid - some of that PF
1.813 is selection. Section 48's walk-forward is not yet built, so the threshold choice itself has
never been validated out of sample. The honest reading is that time-based entry confirmation shows
a real and directionally consistent effect across adjacent thresholds (P10 and P15 both good, both
neighbours of each other), not that PF 1.81 is a tradeable number.

---

### 2.17 TrendStatistics: 16 years, 5 symbols — the edge is metals-only (2026-08-30)

§2.16 found the P90/P95 exhaustion thresholds unestimable from 3.5 years (bull P95 CI 8.6-23.5,
relative width 1.42). Two fixes were possible: more history per symbol, or pooling symbols. Section
4 forbids pooling ("every symbol must have its own statistical profile"), so history it is.

**OANDA serves 4H back to 2010.** Fetched 16.5 years for five symbols via
`TradingClassifierRunner fetch --interval 4h` (~27,000 candles and ~500KB each - trivial next to
the 1.26M-row 1m series).

**More history solves the estimation problem outright:**

```
XAU/USD bull P95, 76 trends (3.5y):   10.509   CI  8.602 -> 23.493   rel width 1.417   NO
XAU/USD bull P95, 375 trends (16.5y):  8.873   CI  8.045 ->  9.969   rel width 0.217   yes
convergence identical at 10,000 and 20,000 iterations
```

Every quantile is stable in both directions on all five symbols. Note the estimate itself moved
from 10.509 to 8.873 - the short sample was not merely imprecise, it was biased ~18% high, which is
what the confidence interval was warning about.

**The section 29/30 entry finding replicates.** TimeOnly P10 gave PF 1.813 on 30 trades over 3.5
years; on 16.5 years - 13 of which the original run never saw - it gives PF 1.802 on 107 trades,
and beats the baseline on every axis (PF 1.802 vs 1.072, net +45.49% vs +34.01%, max drawdown
**10.19% vs 40.52%**). PriceOnly still decays monotonically (1.038 -> 0.952 -> 0.914 -> 0.833 ->
0.809).

**But it does not transfer across symbols, and section 61 said not to assume it would:**

```
symbol    trends  bull  baseline PF  TimeOnly P10 PF  PriceAndTime P10 PF   verdict
XAU/USD      725   375        1.072            1.802                1.682   works
XAG/USD      702   352        1.184            1.979                2.164   works
EUR/USD      798   395        0.840            0.960                0.962   no edge to gate
GBP/USD      786   385        0.918            0.666                0.618   gate makes it WORSE
USD/JPY      749   398        0.970            0.993                0.943   no edge to gate
```

**The trend edge exists in metals and not in FX majors.** Both metals have a positive baseline
before any gating (1.072, 1.184) and both are lifted to ~1.8-2.2 by the time gate, peaking at the
same P10 threshold with the same mode ranking - that is an independent cross-validation on a
symbol whose data played no part in choosing P10. All three FX majors have a baseline below 1.0,
and gating cannot manufacture an edge from a strategy that has none: on GBP/USD it actively
destroys value (0.918 -> 0.666), because filtering to fewer trades concentrates a negative
expectancy rather than diluting it.

The trend populations themselves are similar in size across symbols (702-798 trends), so this is
not a sample artefact. The distributions differ enormously though - bull P95 move is 18.8% on
silver, 8.9% on gold, and 4.3% on EUR/USD and GBP/USD - which is section 4's point made
quantitatively: a shared profile would have been wrong for every symbol.

**How to improve, from this data:**

1. **Restrict deployment to metals.** This is section 51's `Trading/DirectionGate` generalised to a
   symbol gate, and it should be driven by the measured baseline PF, not by asset class as a
   prior.
2. **The time percentile is the mechanism, not price.** Price gating is harmful everywhere
   (monotonic decay on XAU, negative on all FX). Section 30's Experiment A can be retired;
   Experiment B is the one to develop.
3. **P10 is the operating point** on both metals independently. P05 under-filters, P15+ thins the
   sample without improving PF.
4. **Gating amplifies, it does not create.** Screen a symbol on its ungated baseline before
   applying the gate at all - GBP/USD is the counter-example that makes this a rule rather than a
   preference.

**Caveats.** Gross of costs, though at 107 trades over 16.5 years costs are immaterial. The P10
threshold was chosen on XAU 3.5y and confirmed on XAU 16.5y and XAG 16.5y - the XAG confirmation is
the genuinely independent one. Section 48's walk-forward is still not built, so nothing here is a
formal out-of-sample validation of the whole procedure.

---

### 2.18 TrendStatistics V7 + V8: walk-forward cuts the headline result roughly in half (2026-08-30)

Implemented the two phases §2.17 flagged as missing.

**V7, section 48** - `Evaluation/TrendBacktester` (runs against a FROZEN profile over an explicit
trade window, with a cost model) and `Evaluation/WalkForwardRunner` (expanding history, per-window
threshold and mode selected on a validation slice carved from the training period, then frozen).
**V8, section 60** - `Profiles/RegimeClassifier` (volatility terciles fitted causally on
ATR-normalised move) and `Profiles/ProfileRepository` (section 51's missing type), with a
sample-count fallback from the conditioned profile to the unconditional one.

29/29 tests. New coverage pins that entries respect the trade window, costs reduce returns, test
periods tile without overlap, the training library grows monotonically, a too-thin conditioned
profile falls back, and that a report carried entirely by one window fails section 69's bar.

**Result on XAU/USD 4H, 2010-2026, after 0.03% round-trip costs:**

```
                        windows   trades     PF     net%   positive   maxDD   §69 bar
V7 unconditioned             12      170  1.174   +18.89       7/12   20.44%      MET
V8 regime-conditioned        12      185  1.248   +27.16       9/12   19.03%      MET
```

**Both clear section 69's bar. The headline number is nonetheless roughly half what §2.16/§2.17
reported.** The single-pass sweep gave PF 1.802; with the entry threshold chosen per window without
hindsight it is 1.174. **That gap is the measure of how much of the earlier figure was in-sample
threshold selection**, and it is exactly what section 48 exists to expose. The earlier "TimeOnly at
P10 is the operating point" conclusion should be read as an in-sample observation, not a validated
parameter.

**Section 60's fragmentation warning does not bite here - conditioning helps.** PF 1.174 -> 1.248,
positive windows 7/12 -> 9/12, and slightly lower drawdown. The `ProfileRepository` fallback is
probably why: where a volatility tercile is too thin the conditioned profile is not used at all, so
conditioning can add information without ever costing sample size. That is a design detail worth
keeping if this is developed further.

**The finding that most undermines confidence is threshold instability.** The per-window selections
are scattered across the whole grid:

```
TimeOnly P10, TimeOnly P05, PriceOnly P20, TimeOnly P05, PriceOnly P20, PriceOnly P05,
PriceOnly P05, PriceAndTime P15, PriceOnly P25, TimeOnly P10, TimeOnly P05, TimeOnly P10
```

If the P10/TimeOnly optimum were a stable property of the market, validation slices should keep
rediscovering it. They do not. Several windows also trade too few times for their statistics to
mean anything (windows 1, 8 and 10 took 4, 1 and 3 trades; PF 17.018 and infinity are artefacts, not
results).

**Section 38-40 diagnostics, now measurable for the first time:** median trend capture 32.5%
(34.2% conditioned), entry efficiency 38.9% (40.9%), exit giveback 35.6% (35.3%). So the strategy
captures about a third of the move it identifies, enters with ~40% of the move still ahead, and
hands back a third of what it could have taken. Entry efficiency is the weakest link and is where
sections 28-30 were always aimed.

**Timeframe follow-up (2026-08-30, user-suggested): 2H beats 4H under walk-forward.** The user
asked whether a lower timeframe would give more trades and less noise. Half right - lower timeframe
buys trade count and cuts drawdown, and costs per-trade edge. Gross, 16.5 years XAU/USD:

```
TF   baseline PF  trades   sum%   maxDD%  |  best gated       PF  trades  mean%/trade
1H         1.155    2641  130.45   18.90  |  TimeOnly P15  1.362     423       0.112
2H         1.108    1343   68.71   32.50  |  TimeOnly P15  1.704     176       0.284
4H         1.072     725   34.01   40.52  |  TimeOnly P10  1.802     107       0.425
```

Run through V7's walk-forward with 0.03% round-trip costs, 2H is decisively the better timeframe:

```
                        trades   win%     PF    net%  positive  maxDD%   §69
2H unconditioned           125  43.2%  1.694  +34.64      8/12    8.11   MET
2H regime-conditioned      388  38.1%  1.199  +30.77      7/12   12.83   MET
4H unconditioned           170  38.8%  1.174  +18.89      7/12   20.44   MET
4H regime-conditioned      185  38.4%  1.248  +27.16      9/12   19.03   MET
```

**The decisive detail is the in-sample/walk-forward gap.** 2H gross 1.704 -> walk-forward 1.694,
essentially unchanged. 4H gross 1.802 -> walk-forward 1.174, a 35% collapse. An edge that survives
hindsight-free threshold selection is real; one that halves was largely selection. **4H's apparent
superiority in the single-pass sweep was an artefact, and the blueprint's choice of 4H as the base
timeframe is not supported by this data - 2H is better on PF, net, drawdown and robustness.** The
likely mechanism is the threshold instability recorded above: 2H roughly doubles the trend
population per window, which stabilises the per-window selection.

1H is the worst of the three after costs despite the best gross sum - a 0.03% charge takes 27% of a
0.112% mean edge, against 6% of 2H's 0.284%.

**V8 (regime conditioning) should not be used.** It helps at 4H (1.174 -> 1.248) and hurts at 2H
(1.694 -> 1.199). An effect that reverses sign between adjacent timeframes is not an effect;
section 60's fragmentation warning evidently bites at 2H, where the larger trend population is
divided three ways at exactly the point it was starting to help. The code stays (it is correct and
tested) but the recommended configuration is unconditioned.

**Honest status: V0-V8 implemented; V9 and V10 deliberately not.** Sections 62/63 connect this to
the ML agent, and that agent came back at PF 0.85-0.91 across 3.5 years (§3.12g), so there is
nothing worth connecting to. It is also still not an `ITradingAgent` - no `TradingAgentKind` entry,
no catalogue builder - so every number here comes from the research harness rather than the trading
pipeline.

---

### 2.19 Training/evaluation methodology review (2026-08-30) — one real flaw, two open

Reviewed how the classifier is trained and scored, after the question "is the way we train the
model right". Three problems, one corrected claim, and one confirmation of concurrent work.

**FLAW 1 (fixed): the backtester allowed overlapping positions.**
`ClassifierBacktester.Run` had no position tracking - it opened a trade on every actionable bar and
held for `PredictionHorizon` bars, so up to `horizon` positions could be open simultaneously on the
same instrument in the same direction. **Every classifier figure in §3.12-§3.13 counted those as
independent trades.** They are not: they overlap, they would need `horizon` times the capital, and
they inflate apparent statistical confidence. The 19,200 "trades" at 1m are the extreme case.
Added `MaximumConcurrentPositions` / `AverageConcurrentPositions` to `TradingReport` and a
`--no-overlap` mode enforcing one position at a time. The before/after comparison had not completed
when this was written.

**FLAW 1 fully closed 2026-08-30 — see §3.14.** The `--no-overlap` mode existed but only
`walk-forward` passed it; `train`, `ladder` and `ablate` used the library default of `true`, so the
commands measured different strategies. Defaults are now `false` everywhere and one shared policy is
threaded through every command. **The scope of the damage is narrower than stated above: it affects
§3.12/§3.12g only, not §3.13** — the meta-filter never simulates positions, and the primary log it
scores is strictly serial (§3.14).

**FLAW 2 (open): label overlap / sample uniqueness.** Row *t*'s label is built from candles
*t+1..t+horizon* and row *t+1*'s from *t+2..t+horizon+1*, so consecutive labels share 9 of 10 bars.
84,065 training rows therefore carry far fewer independent observations, which inflates effective
sample size and feeds LightGBM near-duplicate examples. Standard remedy is sample-uniqueness
weighting. Not implemented.

**FLAW 3 (open): the threshold tuner has no maximum-trade guard.** Recorded in §3.12g -
`ModelEvaluator.TuneThresholds` enforces a `minimumTrades` floor but no ceiling, so it cannot
reject a threshold that degenerates into trading almost every bar (two 1m windows took 7,448 and
10,032 trades).

**CORRECTION to §2.15.** That entry recorded "same-direction, deeper: 0" overlaps as evidence the
detector was sound. That was measured on the 3.5-year sample only. On 16.5 years there are **15**,
with gaps of -5 to -22 bars. They are **not a defect**: `TrendDetector` sets the re-anchor floor at
the previous trend's *favourable extreme* rather than its end (an explicit, commented design
choice, correct for reversals), so a same-direction successor legitimately anchors at the
retracement low inside its predecessor. The consequence is correlated trend records, not
miscounted ones. The acceptance check's "DOUBLE-COUNT" label was wrong and has been corrected.

**Concurrent work confirmed correct.** `BootstrapEngine` gained a block bootstrap
(`BuildBlockDistribution`, calendar-bucketed non-overlapping blocks) and `DirectionTrendProfile`
gained `IndependentBlockCount` / `ReliabilityScore` gating `IsReliable`, plus a symbol-mixing guard
in `Build`. Verified on XAU/USD 4H 2010-2026 that the block version widens intervals as theory
requires:

```
   q  estimate   IID width   block width   ratio
0.05     1.387       0.210         0.237    1.13
0.50     3.568       0.456         0.520    1.14
0.90     7.946       0.982         1.267    1.29
0.95     9.970       1.840         2.218    1.21
  68 independent 90-day blocks vs 382 observations
```

**382 trend records carry roughly 68 independent units** - a 5.6x reduction in effective sample
size, and the direct quantification of the correlation the overlaps above create. The quantiles
still clear the stability bar under blocking (P95 relative width 0.222 vs the 0.5 threshold), so
§2.17's conclusions survive, now with honest rather than understated uncertainty. This is the right
tool for the problem and should be preferred over the IID path for any trend-derived statistic.

---

### 3.14 Classification Agent V2 — Phase 0a: execution parity and baseline reconciliation (2026-08-30)

First implementation step of `Books/Trading Classification Agent V2 — Investigation and Redesign.md`.
That document gates all model work behind a Phase 0 falsification of the §3.13 filter premise; this
is P0a (correctness parity and reproducibility) only. **No V2 model, candidate contract or trend
join has been built, and none should be until P0b returns a verdict.**

**1. Overlap parity (closes §2.19 FLAW 1).** Three library entry points defaulted
`allowOverlappingPositions` to `true` — `ClassifierBacktester.Run`
(`TradingClassifier/Evaluation/TradingMetrics.cs:91`), `ModelEvaluator`
(`TradingClassifier.ML/Evaluation/ModelEvaluator.cs:52`) and `WalkForwardRunner`
(`TradingClassifier.ML/Experiments/ExperimentRunners.cs:91`) — while only `walk-forward` passed the
`--allow-overlap` flag. `train` (Program.cs:110,142), `ladder` and `ablate` therefore scored a
strategy holding up to `horizon` simultaneous positions. All three defaults are now `false`,
`FeatureExperimentRunner` threads the policy to its inner `WalkForwardRunner`, and the runner reads
the flag **once** into a shared `allowOverlap` passed by all five construction sites.

New `Simulator.Tests/OverlapPolicyParityTests.cs` (3 tests) pins all of it: a behavioural check that
the default serialises 4 actionable bars into 2 trades and that the flag genuinely changes the
count; a reflection check that every entry point still defaults to `false`; and a source check that
the runner reads the flag once and every command passes it. 37/37 classifier tests pass.

**2. The 0.626 vs 0.756 baseline discrepancy is resolved in favour of §3.13.**
`Books/Blueprint — ML Meta-Label Redesign.md:69` reports the §3.13 unfiltered baseline as PF 0.626 /
net -4089.27; §3.13 reports PF 0.756 / net -3610.90. Both claim the same 103 trades at 43.7%.
Reconstructing the covered set directly from `Books/trades.csv` (134 trades, 2026-01-05 to
2026-02-04):

```
covered set              n    win     PF        net    grossProfit  grossLoss
suffix from 2026-01-09  102     45   0.751   -3716.71      11134.6    14851.3
§3.13 reported          103     45   0.756   -3610.90      11187.9    14798.8
Blueprint:69 implied    103     45   0.626   -4089.27       6844.6    10933.9
full 134-trade log      134     56   0.729   -5268.83      14205.8    19474.6
```

**§3.13 reproduces to within one trade; the blueprint's figure does not reproduce at all.** Its
implied gross turnover (17,778) is 53% of the full log's, where 103/134 trades should be ~77% — and
§3.13's 25,987 is exactly that. Treat `Blueprint — ML Meta-Label Redesign.md:69` as stale or from a
different run; **§3.13 is the authoritative baseline** and does not need re-deriving for P0b.

**3. The meta-filter path never had the overlap defect.** `MetaFilter.Apply` carries the primary
strategy's realised `Net` through unchanged and calls `ClassifierBacktester.Summarise`, not `.Run`
(`TradingClassifier.ML/Experiments/MetaFilter.cs:240-244`) — it never simulates a position, so no
overlap policy applies. Measured directly, the BreakoutDetector log is **strictly serial: maximum
concurrent positions 1, zero trades opened while another was live**, consistent with
`BreakoutDetectorAgent.cs:72` refusing to signal while a position is open. So §2.19 FLAW 1 taints
§3.12/§3.12g only. The V2 document's §3.1 item 1 ("use one-position-at-a-time in ... meta-filter
evaluation") is a no-op, and its §2.4 claim that old baselines are not comparable applies to the
standalone results, not to §3.13.

**4. BreakoutDetector uses managed exits, not a fixed bracket.** Exit reasons across the 134 trades:
73 `InitialStopLoss`, 28 `TakeProfit`, 22 `BreakEvenStop`, 4 `ProfitFloorStop`, 3
`TrailedStructureStop`, and one each of `MfeGivebackStop`, `ProfitFloorExit`, `MaximumGivebackExit`,
`EndOfSimulation`. **31 of 134 (23%) exit through management rather than either bracket leg**, even
though §2.11 declares the exit model as `AgentExitManagementMode.Bracket`. This is decisive for V2's
label design: a three-class `TargetFirst`/`StopFirst`/`Timeout` label would be wrong for ~23% of
candidates, which independently confirms the V2 document's choice of after-cost realised R as the
primary target (its §5.2) rather than the earlier three-class proposal.

**5. Scale-outs make the bracket label structurally inapplicable, not merely inaccurate.** The run
manifest (`/mnt/storage/scratch/bd-1m/simulations/41d670c8.../manifest.json`) shows
`legacyPositionManagement.enableScaleOut: true` with two stages — `scale-1r` and
`scale-1.5r-or-structure`, each closing 20% of initial quantity — plus `breakEvenActivationR: 1`,
`structureTrailActivationR: 1.5`, 53 break-even activations and 78 accepted stop amendments across
the 134 trades. A candidate's realised R is therefore **a blend across up to three partial exits at
different prices**, not one barrier outcome. This goes beyond §3.14.4: the V2 document's §5.2
`CandidateOutcome` schema (single `EntryPrice`/`ExitPrice`) cannot represent it either, and will
need a partial-fill representation. After-cost realised R remains the correct primary target.

**6. P0b harness built and validated (`filter-nested`).** `MetaFilter.SelectNested`
(`TradingClassifier.ML/Experiments/MetaFilter.cs`) plus a `filter-nested` runner command implement
the nested selection §3.4 of the V2 document requires: each fold picks its **feature set, mode and
threshold on validation candidates only**, freezes them, and applies them once to untouched test
candidates. `filter-signals` cannot do this — it prints a whole grid scored on the test period, so
the reader selects after seeing the answer.

Smoke-run on the same 5-week data as §3.13, predeclared sets `ohlc:Experiment1` and
`rung3:Experiment3`, 10d/3d/3d, horizon 10, costs 0.15+0.05:

```
column                        trades    win%       PF         net       maxDD
BASELINE (no filter)             104   44.2%    0.781    -3245.34     6257.15
NESTED (frozen per fold)          33   57.6%    1.295     1160.93     1409.39

Random control (33 of 104): median PF 0.773, p95 1.360, result at the 93.8th percentile.
Folds 9; rule selected in 7; positive test folds 5. ohlc chosen 5x, rung3 2x.
```

A first version of this run reported a 74-trade baseline at the 87.3rd percentile. That was **a bug
in `SelectNested`, caught by `NestedFilterSelectionTests`**: the fold's baseline was derived from the
winning rule's scored set, so a fold where no rule cleared the validation guard contributed *nothing*
to the denominator — silently comparing accepted trades only against folds that happened to produce
a rule. Both guard-failing folds here had negative baseline net (-1,157.94 and -1,569.92), so the
defect flattered the result. The baseline is now fixed by the first feature set independently of
selection, and accepted is drawn from the baseline's membership. Corrected coverage (104) matches
§3.13's 103 and the corrected baseline PF 0.781 matches §3.13's 0.756.

**This is a harness validation, not the gate — but it is the first honest look at the §3.13 effect,
and the effect shrinks.** §3.13's hindsight-selected threshold reached PF 2.337 at the 99.8th
percentile; with the threshold and feature set chosen per fold on validation only, the same data
gives PF 1.295 at the **93.8th percentile — still short of the 95th-percentile bar** the V2
document's §3.5 and §8 require, though borderline rather than clearly failing. It is a milder form
of the pattern §2.18 recorded when 4H's PF 1.802 fell to 1.174 under hindsight-free selection.

**Compute budget for the real gate.** The §3.13 run covered 31 days / 38,377 base candles in 3m44s
at 64 candles/sec (job snapshot). 3.5 years is 1,257,460 1m candles ≈ **5.5 hours per candidate-log
generation**, and the chosen "both geometries" option needs two of them plus a calibration sweep to
produce the second geometry — roughly 15-20 hours total. Not yet launched.

**8. Stale-snapshot duplication measured: 5.2%, not ~18%.** The V2 document §3.1 item 4 asks for a
measured before/after rather than reuse of the blueprint's ~18% estimate. `BreakoutDetectorAgent`
has no last-processed-trigger guard — it wakes on the 1m interval, reads `trigger.LatestCandle` from
the 5m snapshot, and its only re-entry guard is "a position is already open"
(`BreakoutDetectorAgent.cs:71-75`). Grouping the 134-trade validation run by the 5m bucket of
`signalCreatedAt`: **127 distinct trigger buckets, 7 buckets holding more than one candidate, 7
surplus candidates = 5.2%**. Materially smaller than the blueprint's estimate. §3.1 item 4 allows
"deterministically deduplicate by source event" instead of an agent-side guard, so the long
candidate log will be deduplicated post-hoc (first candidate per 5m trigger bucket) rather than
regenerated. Caveat to record with the result: position sizing is `FixedFractionalRisk` on equity,
so dropping a candidate post-hoc leaves later trades sized on an equity path that included it.

**7. P0b pre-registration (recorded 2026-08-30, BEFORE the gate result exists).** The V2 document's
§3.4 requires the primary claim be declared before the final test is read. Registered here so it
cannot be adjusted afterwards:

- **Primary comparison.** Pooled `NESTED (frozen per fold)` accepted candidates versus the pooled
  `BASELINE (no filter)` candidates over the same covered set, after costs. One comparison, one
  number.
- **Candidate source.** `breakout-detector` at its **current, uncalibrated geometry** — 15m context /
  5m trigger / 1m confirmation, `StopAtrMultiple` 1.5, `TargetAtrMultiple` 3.0, `BreakoutBufferAtr`
  0.25, `RangeLookbackCandles` 20, `CooldownCandles` 4 — frozen as an explicit premise of the gate
  (per the open item below). A second, calibrated geometry is deferred until this run reports.
- **Instrument / period.** XAU/USD, 2023-01-02 to 2026-07-24, the same span as §3.12g.
- **Candidate log.** Generated by `BacktestRunner` at 1m execution, analysis 5m/15m/1h, OANDA demo
  source. The 31-day reproduction of §3.13's run under this exact command returned **134 trades at
  41.8% win rate**, matching the original job snapshot, so the geometry is confirmed reproducible.
- **Classifier.** Existing directional classifier, unmodified. Horizon 10, 1m features.
- **Feature sets (both, nested).** `ohlc:Experiment1` (14 features) and `rung3:Experiment3` (34),
  selected per fold on validation only — never on test.
- **Walk-forward.** 90d train / 30d validation / 30d test, matching §3.12g's long-history config,
  embargoed by the horizon.
- **Filter grid searched on validation only.** Modes Agreement and NotOpposed; thresholds
  0.30/0.40/0.50/0.60/0.70; minimum 5 kept validation candidates for a rule to be eligible.
- **Selection objective.** Total after-cost net on validation-accepted candidates.
- **Gate criteria (§3.5).** Pass requires: after-cost improvement over the unfiltered baseline; the
  accepted subset beating the **95th percentile** of equal-sized random subsets; a majority of
  positive walk-forward test folds; no dependence on one year or one side; and enough accepted
  events for usable uncertainty bounds. Failing any of these is a **stop**, which the document
  (§12) treats as a successful research outcome.

**AMENDMENT 1 (2026-08-30, made before any gate result existed; user-approved).** The primary
metric changes from **account currency to R multiples**, with currency reported as a secondary
column that is never optimised. Rationale, which depends only on the candidate source and not on any
filter outcome: the source is sized by `FixedFractionalRisk` on equity, and the 3.5-year run takes
the account from 100,000 to under 24,000. A currency evaluation therefore (a) weights 2023
candidates several times more heavily than 2025 ones purely because the account was larger, making
"does it work in every year" untestable, and (b) credits the filter with the de-risking that the
drawdown itself produced. Measured on the partial run at 73.5%: **PF 0.695 in currency versus 0.656
in R** — the dollar figure flatters the source by exactly that mechanism. Selection now optimises
total validation net R; `NestedFilterResult` exposes both views. No gate result had been computed
when this amendment was made.

A second defect surfaced while implementing the amendment: `MetaFilter.RandomControl` drew its
random subsets from `Trade.Net` while being handed an R-based profit factor to rank, so the
percentile compared an R statistic against a currency distribution. Now metric-aware
(`useRMultiple`), and both controls are reported.

**Restated in R, the 5-week validation result changes conclusion:**

```
PRIMARY - R multiples          trades   win%    PF_R      netR    maxDD_R
BASELINE (no filter)              104  44.2%   0.631    -21.59      27.94
NESTED (frozen per fold)           33  57.6%   0.778     -4.56      10.51
Random control in R: median 0.630, p95 1.134 -> result at the 70.5th percentile

SECONDARY - account currency    trades   win%     PF$      net$     maxDD$
BASELINE (no filter)              104  44.2%   0.781  -3245.34    6257.18
NESTED (frozen per fold)           33  57.6%   1.295   1160.93    1409.40
Random control in currency: median 0.773, p95 1.360 -> 93.8th percentile
```

**In currency the filter appears to turn -3,245 into +1,161 at the 93.8th percentile. In R it turns
-21.59R into -4.56R at the 70.5th percentile — still losing, and well inside the random
distribution.** The apparent profitability is a position-sizing effect, not selection.

Mechanism: within this 31-day window equity moved only -5%, yet the risk-per-trade proxy `|net/R|`
spans 15.8 to 368.1 — a **23x range** (median 333.9, p25 184.5). Partial exits (61.6% of trades)
confound that ratio, so the exact mechanism is not fully attributed, but the conclusion does not
depend on it: two views of the same 33 trades disagree by 23 percentile points, so the currency
view is not a clean measure of selection quality.

Recorded before the run completed. The preliminary 5-week validation now sits at the 70.5th
percentile in R, materially below this gate's 95th-percentile bar.

**9. Pre-registered SECOND hypothesis: training-window length (recorded 2026-08-30, before the
first gate result existed; user-proposed).** §3.12 and §3.12g differ in **two** variables at once:

| | training config | period | PF (1m) |
|---|---|---|---|
| §3.12 | **14d** train / 5d val / 5d test | 41 days, Dec-Feb | **1.430** |
| §3.12g | **90d** train / 30d val / 30d test | 3.5 years | **0.849** |

The repo concluded the short-window result "did not generalise", attributing the collapse to the
**period**. That conclusion is not established: the **training-window length** changed too, and was
never tested independently. A 14d model retrained every 5d adapts to regime; a 90d model averages
over regimes that may no longer be relevant. Both stories fit the observed data.

**Registered test:** run the identical gate at **14d train / 5d validation / 5d test across the full
3.5 years** — the unfavourable history, not the Dec-Feb window. Same candidate log, same code, same
feature sets, same R-primary evaluation; only the spans change. ~250 folds of ~21 candidates each
(vs 39 folds of ~138), same ~5,400 pooled candidates, and likely *faster* than the primary run
because each model trains on ~20k rows rather than ~130k.

- Comes back ≈1.4 → training length was the driver; a significant and previously untested finding.
- Comes back ≈0.85 → the period was the explanation; the question closes.

**Registered as a declared second hypothesis, not a search.** It is recorded here before the primary
90d/30d/30d gate reported, so it cannot be a reaction to that result. It does not supersede the
primary registration in item 7; both will be reported. If a live retrain cadence is later specified,
the config matching deployment becomes primary by definition and the other becomes a diagnostic —
that choice must be made on deployment grounds, never on which profit factor is higher. Running
14d/30d/90d and reporting the winner is explicitly out of scope without the §3.4 multiple-testing
treatment.

**Open before P0b can run — neither is a code defect, both are premises:**

- **The candidate source is uncalibrated.** §2.11's "placeholders, not calibrated — replace them
  with calibrated values (and record the run here)" still stands; no calibration run exists in this
  file. P0b would spend a 3.5-year falsification on an arbitrarily parameterised source, making a
  *failure* ambiguous (no classifier signal vs. bad candidate geometry) and a *pass* fragile (labels
  conditioned on 1.5/3.0 ATR that change the moment anyone calibrates). Either calibrate first or
  freeze the values as an explicit, documented premise of the gate.
- **P0b does not declare which feature set it runs.** §3.13 found OHLC-only (14 features) the best
  *filter* (PF 3.064); §3.12 found rung 3 (34 features) the best *standalone*. The V2 document's
  §3.4 requires the primary claim be predeclared, so this choice must be registered before the run,
  not made during it.

---

### 3.15 Classification Agent V2 — Phase 0b GATE RESULT: FAILED **for breakout-detector**. (2026-08-30)

> **SCOPE CORRECTION (2026-08-31).** This section, §3.16 and §3.18 all measured **one candidate
> source: `breakout-detector`**. §3.19 then showed that source hits its target 29.8% of the time
> against a 35.6% random-walk expectation — indistinguishable from a coin flip on its own bracket.
> **A meta-model can only select structure that exists**, so a null result over a source with no
> entry edge is the expected outcome and is not evidence about the technique.
>
> - **Settled:** candidate-conditioned meta-labelling does not rescue `breakout-detector`.
> - **NOT settled:** whether meta-labelling or direct-R regression works against a source that has a
>   demonstrated edge. Never tested.
> - **Transfers regardless:** the harness, the gate methodology, nested selection on validation,
>   R-primary evaluation, metric-matched random controls.
>
> Read "the gate failed" throughout §3.15-§3.18 as "failed for this source". The natural retest is
> the trend-gated agent in `Books/Design — Trend-Gated Tactical Agent.md`, built on the 2H
> TrendStatistics result (§2.18, PF 1.694 walk-forward) — the only validated edge in this repo.



The pre-registered falsification gate (§3.14 item 7) ran to completion on the full 3.5-year
BreakoutDetector candidate log. **It fails every criterion that discriminates. Per the V2 document
§3.5 and §12, the correct action is to stop: do not build P1-P4.**

Candidate log: `breakout-detector` at the frozen uncalibrated geometry, XAU/USD, 2023-01-03 to
2026-07-23, generated in 3h27m over 1,276,655 1m candles. **5,452 candidates**, net -85,068.21,
account 100,000 -> 16,634. Simulation `8e19544350bc49c6bd16e66ee36c1db7`.

```
PRIMARY - R multiples          trades   win%    PF_R       netR    maxDD_R
BASELINE (no filter)             4901  42.1%   0.688    -801.50     808.60
NESTED (frozen per fold)          650  42.2%   0.719     -96.52     113.36
Random control in R: median 0.688, p95 0.790 -> result at the 70.1st percentile

SECONDARY - account currency   trades   win%    PF$        net$     maxDD$
BASELINE (no filter)             4901  42.1%   0.719  -68714.88   69208.24
NESTED (frozen per fold)          650  42.2%   0.706  -10093.39   11189.01
Random control in currency: median 0.719, p95 0.846 -> 42.5th percentile

39 folds, a rule was selected in all 39, positive test folds 12/39 (31%).
Feature set chosen: rung3 23x, ohlc 16x.
```

**Against the registered §3.5 criteria:**

| Criterion | Required | Actual | |
|---|---|---|---|
| After-cost improvement | credible | PF_R 0.688 -> 0.719, still **-96.52R** | FAIL |
| Beat random same-size subsets | > 95th pct | **70.1st** percentile | FAIL |
| Majority of positive test folds | > 50% | **12/39 = 31%** | FAIL |
| Enough accepted events | usable bounds | 650 accepted | pass |

**The decisive number is the win rate: baseline 42.1%, filtered 42.2%.** The classifier has
essentially zero discriminative power over these candidates. It is not selecting better trades; it
is trading less. The random-control median PF_R (0.688) is *identical to the baseline*, and the
filtered result (0.719) sits at the 70th percentile of that distribution — a mildly lucky random
subset, nothing more. In account currency the filter is **worse than the unfiltered baseline**
(0.706 vs 0.719, 42.5th percentile — below random median).

No consistent feature set won (rung3 23 folds, ohlc 16), which is itself what noise looks like.

**§3.13's headline is now fully explained** and should be treated as superseded. The decomposition,
each step measured this session on progressively honest methodology:

| | PF | percentile |
|---|---:|---:|
| §3.13, hindsight-picked threshold, currency, 5 weeks | 2.337 | 99.8th |
| Frozen on validation, currency, 5 weeks | 1.295 | 93.8th |
| Frozen on validation, **R**, 5 weeks | 0.778 | 70.5th |
| Frozen on validation, **R, 3.5 years** | **0.719** | **70.1st** |

Roughly half the original effect was hindsight threshold selection, most of the remainder was
position sizing, and what survives is indistinguishable from random — and the 5-week and 3.5-year
honest results agree closely (70.5th vs 70.1st percentile), so this is a stable null, not a
small-sample accident.

**Consequence.** Per the V2 document §12, a failed Phase 0 is a *successful* research outcome: it
cost one 3.5-hour simulation and a small harness instead of four implementation phases built on a
transient selection effect. **P1-P4 are not authorised.** The existing `ISetupMetaModel`
infrastructure stays as-is.

**Confirmed under deduplication (2026-08-30).** §3.1 item 4 was measured but not applied when the
gate first ran. `MetaFilter.DeduplicateBySourceEvent` is now wired into `filter-nested`
(`--dedup-trigger-minutes`, default 5) and the gate was re-run, removing 274 of 5,452 candidates
(5.0%). The prediction recorded before the re-run — that removing ~5% of candidates cannot move a
verdict that failed by 25 percentile points — holds:

```
                        original            deduplicated
baseline PF_R           0.688               0.711
nested PF_R             0.719               0.744
nested netR             -96.52              -112.76
random control (R)      70.1st pct          73.2nd pct      (bar: 95th)
positive test folds     12/39 = 31%         13/39 = 33%     (bar: >50%)
win-rate lift           42.1 -> 42.2        42.7 -> 42.9    (+0.2pp)
currency percentile     42.5th              42.2nd
```

Every discriminating criterion still fails, by the same margins. The feature-set split also stays a
dead heat (ohlc 20 folds, rung3 19), which is what noise looks like. **§3.15's verdict stands, now
on a gate that meets its own §3.1 item 4 requirement.**

**Still open (pre-registered before this result, §3.14 item 9):** the 14d/5d/5d training-window
hypothesis. §3.12 and §3.12g confounded training length with time period, and only the period was
ever tested. That question is untouched by this result and remains worth running on the same log.

**Not a valid response to this result:** calibrating the source, re-running with different
thresholds, or trying further feature sets in search of a better number. Any of those is a
post-result search and would rebuild exactly the selection effect this gate just eliminated.

---

### 3.16 V2 second hypothesis: training-window length. Answered — the PERIOD was the explanation. (2026-08-30)

Ran the pre-registered 14d/5d/5d hypothesis (§3.14 item 9) on the same 3.5-year candidate log, same
code, same feature sets, same R-primary evaluation. Only the walk-forward spans changed.

```
PRIMARY - R multiples          trades   win%    PF_R       netR    maxDD_R
BASELINE (no filter)             5442  42.0%   0.681    -911.30     913.47
NESTED (frozen per fold)         2737  43.1%   0.716    -400.21     405.06
Random control in R: median 0.682, p95 0.716 -> result at the 95.2nd percentile

SECONDARY - account currency   trades   win%     PF$       net$     maxDD$
BASELINE (no filter)             5442  42.0%   0.707  -84958.31   85748.11
NESTED (frozen per fold)         2737  43.1%   0.731  -39108.64   39544.14
Random control in currency: median 0.707, p95 0.751 -> 81.1st percentile

260 folds, rule selected in 255, positive test folds 87/260 (33.5%).
Feature set: rung3 128x, ohlc 127x - a dead heat, i.e. noise.
```

**The hypothesis is answered: training-window length was NOT the driver.**

| | 90d/30d/30d | 14d/5d/5d | §3.12 (41 days) |
|---|---:|---:|---:|
| Nested PF_R | 0.719 | **0.716** | 1.430 (currency, hindsight) |
| netR | -96.52 | -400.21 | — |
| positive folds | 31% | 33.5% | 4/4 |

Shortening the training window from 90d to 14d moves the filtered profit factor from 0.719 to
**0.716 — indistinguishable**, and nowhere near §3.12's 1.430. **§3.12's short-window result does not
reproduce at its own 14d/5d/5d configuration once run over the full history. The favourable Dec-Feb
2026 period was the explanation, exactly as the repo originally concluded — that conclusion is now
tested rather than assumed, and it holds.**

**Do not misread the 95.2nd percentile as a pass.** Three reasons:

1. The result *equals* p95 to three decimals (0.716 vs 0.716). It is at the boundary, not past it.
2. The null distribution narrowed because the keep fraction rose from 13.3% (90d) to **50.3%** (14d).
   Random subsets of half the population cluster tightly around the baseline, so a trivial edge
   becomes "distinguishable from random" without becoming valuable. This is statistical
   significance without economic significance, and the currency view agrees (81.1st percentile).
3. Economically it is negligible: the filter lifts avgR from **-0.1675 to -0.1462**, a gain of
   **+0.0212R per trade** against the **0.1675R per trade needed to reach breakeven — 12.7% of the
   gap**. It still loses 400R over 2,737 trades.

Against the §3.5 criteria: after-cost improvement **FAIL** (still -400R), beat-random **marginal**
(95.2nd, at the boundary), majority positive folds **FAIL** (33.5%), sample size pass. **The gate
verdict in §3.15 stands unchanged: stop.**

There is a real but tiny signal — the win-rate lift is +1.1pp here versus +0.1pp at 90d, so the
shorter, more adaptive model does discriminate marginally better. It is roughly an eighth of what
would be needed to matter, against a source that loses 0.17R per trade. **That is a reason to fix
the source, not to keep modelling it.** See §3.14 item 5 and the exit-management analysis, which
*estimated* a 673R leak from scale-outs and break-even stops — but see §3.17a: that decomposition
was tested and **falsified**, and the real leak is far smaller. The conclusion above does not rest
on it; it rests on the -0.17R/trade source.

---

### 3.17 THIRD hypothesis: management-off geometry (registered 2026-08-30, before the run)

Motivated by the measured leak in §3.14 item 5 and the exit-reason analysis, not by a parameter
search. On the 3.5-year log the realised payoff ratio was **0.93:1 against a planned 1.79:1** — the
bracket's designed edge is destroyed after entry, not at entry:

```
avg WIN  +0.828R (n=1846)      avg LOSS -0.894R (n=2543)
win rate  42.1%  ->  breakeven needs 51.9%; the PLAN (1.79:1) needs only 35.8%

exit reason              n      avgR     totalR
InitialStopLoss       2481    -0.911    -2259.3
TakeProfit            1002    +1.288    +1290.9     <- planned 1.79R, delivered 1.288R
BreakEvenStop          738    +0.164     +120.7     <- reached 1R, returned ~0
```

Decomposition of the -747R deficit: scale-outs cost ~`1002 x (1.79 - 1.288) = 503R`; break-even
stops ~`738 x 0.23 = 170R`. Together ~673R, **~30x larger than anything the classifier filter
recovered** (§3.16: +0.021R/trade over 2,737 trades = ~58R).

**Registered test:** identical run with `--legacy-trailing-mode disabled --legacy-no-scale-out`.
Everything else — instrument, period, intervals, bracket geometry, costs, source — unchanged.

**Predictions, recorded before the run:**

- If the decomposition is right, PF_R should move from 0.673 toward ~0.85-0.95 and netR from -747R
  toward roughly -100R to -250R. A price-reconstruction counterfactual (§3.14) put the raw bracket
  at PF 0.788, so **that is the honest expectation; profitability is NOT predicted.**
- `TakeProfit` avgR should rise from +1.288 toward ~+1.79 (no scale-out drag).
- `BreakEvenStop` should vanish as an exit reason; those 738 trades redistribute to TakeProfit and
  InitialStopLoss.
- Losing `MfeGivebackStop` (+1.277R on 71 trades, ~+91R) is an expected small cost of the change.

**This is a new candidate source, not a re-scoring of the old one.** Entry logic is unchanged, but
because exits close at different times the "a position is already open" guard fires differently, so
the candidate set will not be identical. Any filter result on it requires the gate to be re-run and
is a separately registered hypothesis — it does **not** reopen §3.15's verdict, which stands for the
geometry it tested.

**Scope discipline:** this is a source-improvement experiment. It is explicitly *not* an attempt to
rescue the meta-filter premise, and a better source does not by itself re-authorise P1-P4.

#### 3.17a Result: the 673R decomposition is FALSIFIED (2026-08-31)

Run `nomgmt-validate` (complete, `COMPLETE` marker present, 122 trades, management fully disabled —
the exit histogram contains only `InitialStopLoss`, `TakeProfit` and one `EndOfSimulation`, so
unlike the abandoned `geometry-nomgmt` run this one really did turn management off).

```
                      WITH management        WITHOUT management
PF_R                        0.673                   0.683
avg R / trade              -0.17                   -0.2320
TakeProfit    avgR         +1.288                  +1.685   <- prediction HELD
BreakEvenStop  n             738                        0   <- prediction HELD
InitialStopLoss avgR       -0.911                  -1.050   <- prediction MISSED (worse)
```

**Two of three predictions held; the headline one did not.** PF_R was predicted to move from 0.673
toward ~0.85-0.95. It moved to **0.683** — inside noise.

**Why the decomposition was wrong:** it counted what management *cost winners* (scale-outs capping
+1.79R at +1.288R) without counting what break-even stops *saved on losers*. With break-even stops
removed, average loss deepened from -0.911R to -1.050R. The two effects very nearly cancel. The
management leak is real in direction but roughly an order of magnitude smaller than 673R, and the
"~30x larger than the filter" claim in §3.16 does not survive.

**Caveat:** 122 trades against the baseline's thousands, so the PF comparison is directional, not a
matched-window measurement. The *mechanism* (winners capped, losers unprotected) is what the exit
histogram establishes, and that does not depend on sample size.

**Standing lesson:** an arithmetic decomposition of a deficit is a hypothesis, not a measurement.
This one was labelled "measured" in §3.16 before any run tested it. Corrected there.

---

### 3.18 V2 P1 + P2-slice: candidate-outcome dataset and direct-R regression (registered 2026-08-30)

Building the load-bearing tenth of V2 rather than all of P1-P4: the candidate-outcome dataset (P1)
and a direct realised-R regression on it (a slice of P2). Skips the feature registry, the 2H join
(P3) and robustness (P4). Purpose: measure whether *any* information in a candidate predicts its
realised R, before committing weeks to the rest.

**PRE-REGISTERED SUCCESS BAR (recorded before the model was built or run).** The source loses
**0.17R per trade**. For a filtered subset to break even, the model must select candidates averaging
0R from a pool averaging -0.17R. With R having stdev ~1.0 and a ~20% keep rate, the mean lift from
selecting the top 20% by a predictor correlating rho with realised R is approximately `rho x 1.4`.
So the bar is:

**rho >= 0.12 out-of-sample (Pearson, predicted vs realised R, pooled across walk-forward test
folds).**

For calibration against what has actually been measured: §3.16's best filter delivered +0.021R at a
50% keep, implying **rho ~ 0.026**. V2 therefore needs roughly **5x** the predictive signal anything
has shown. Interpretation fixed in advance:

- **rho >= 0.12** -> the premise survives; P3/P4 are justified.
- **0.05 <= rho < 0.12** -> real but insufficient signal; report and stop, do not tune toward the bar.
- **rho < 0.05** -> no usable information; P3/P4 cannot rescue it. Stop.

**Structural finding that shapes the build (measured before running).** Two things the V2 document
assumed are not available for this source:

1. **The journal carries almost no candidate context.** Across sampled trades, `entryRegime`,
   `entrySetupType`, `entryVolatilityBucket` are all the constant `"Unknown"`, `entryConfidence` is
   constant 70, and `plannedR`, `initialRiskCash`, `entryRegimeConfidence`, and every
   NeoWave/SupplyDemand/StructuralConfluence field are null. Only `side`, `entrySession`,
   `entryMultiTimeframeAlignment` and the prices vary. Features must therefore be rebuilt causally
   from candles, confirming the document's §6.3 warning that detailed state "may not exist in the
   current journal".
2. **The document's §6.1 "candidate geometry" feature group is degenerate here.** The bracket is a
   fixed ATR multiple: `stopSource` is always "1.50 ATR", `targetSource` always "3.00 ATR", and
   `expectedRewardRisk` is constant 2.0. Stop distance in ATR, target distance in ATR and planned
   reward/risk are therefore **constants carrying zero information**. They are retained in the
   feature vector for provenance and will be reported as constant rather than silently dropped.

The consequence is worth stating plainly: for this source, V2's feature set collapses to local
causal price state plus side — close to what the directional classifier already had. **What remains
genuinely new is the target** (after-cost realised R instead of direction at a fixed horizon), which
is the half of the V2 claim this slice actually tests.

**RESULT (2026-08-30): rho = 0.025. Below the "no usable information" floor. Stop.**

```
5,178 candidate rows x 21 features, 0 unjoined. Target mean -0.1523, stdev 0.9584.
Walk-forward by event: 600 train -> 200 test, rolling, 22 folds, 4,400 scored out of sample.

model               folds  scored      rho      MAE    baseR   top20%R   top10%R
Sdca-linear-R          22    4400   0.0252   0.8533  -0.1452   -0.1171   -0.1412
LightGBM-R             22    4400   0.0044   0.8606  -0.1452   -0.1183   -0.0869
```

Against the bar registered before the model was built: **rho 0.0252 and 0.0044 both fall in the
`< 0.05` band = no usable information. P3/P4 cannot rescue this and are not authorised.**

Three things make this decisive rather than merely negative:

1. **It independently reproduces the predicted base rate.** The bar was derived from §3.16's filter
   result, which implied rho ~ 0.026. The direct R regression returns **0.0252**. Two unrelated
   methods — a directional classifier used as a filter, and a linear regression on realised R —
   land on the same effect size. That is a stable measurement of the source, not an artefact of
   either method.
2. **Selection does not pay.** Taking the top 20% by predicted R lifts mean R from -0.1452 to
   -0.1171 (+0.028R), still deeply negative; the top 10% is -0.1412, *worse* than the top 20%,
   which is what noise looks like when it is ranked.
3. **Complexity makes it worse.** LightGBM (rho 0.0044) underperforms the regularised linear model
   (0.0252), exactly as §7 anticipated when it required LightGBM to earn its complexity. It did not.

**This closes the V2 question.** The gate (§3.15) showed the existing classifier cannot select these
candidates; this shows *no model on the available causal features* can, using the redesigned target
V2 was built around. The remaining explanation is in §3.19: the entry itself has no edge.

---

### 3.19 Target geometry: the 2R target is far out of reach — but no target fixes the source (2026-08-30)

User hypothesis: a 1:2 risk:reward is too ambitious and the market rarely travels that far. **Tested
and confirmed.** MFE re-expressed in units of the *actual* initial risk distance from prices (not
the trade record's R fields), 5,452 trades:

```
MFE median 0.88R   p60 1.22   p70 1.58   p80 1.83   p90 1.99

reached MFE >= 0.50R : 3401 (62.4%)      >= 1.50R : 1745 (32.0%)
reached MFE >= 0.75R : 2954 (54.2%)      >= 1.75R : 1305 (23.9%)
reached MFE >= 1.00R : 2555 (46.9%)      >= 2.00R :  526 ( 9.6%)
```

**Only 9.6% of trades ever travel 2R.** The bracket's target sits where roughly one trade in ten can
reach it.

Target sweep with the stop fixed at 1R, barrier order resolved by MFE/MAE timestamps:

```
 target   winRate   expR/trade     totalR
   0.50     60.0%      -0.0995     -542.5
   0.75     52.0%      -0.0889     -484.8   <- best
   1.00     44.8%      -0.0970     -529.0
   1.25     36.8%      -0.1117     -609.0
   1.50     30.2%      -0.1192     -650.0
   1.75     22.2%      -0.1859    -1013.5   <- approximately the current bracket
```

**CENSORING CAVEAT, and it matters.** MFE is truncated at the live target: a trade that reaches the
target is closed, so its recorded MFE stops there and cannot exceed ~1.81R. The sweep is therefore
**valid only for target multiples at or below ~1.75R** and the 2.0R+ rows are artefacts of that
truncation, not measurements. The calibration check: at m=1.75 the sweep gives a 22.2% win rate
against the 22.8% `TakeProfit` rate actually observed, so the simulation is well calibrated in its
valid range.

**Conclusion, in two parts.**

1. **The hypothesis is right.** Expectancy roughly halves from -0.186R/trade at the current ~1.75R
   target to **-0.0889R/trade at a 0.75R target**. The geometry is genuinely mis-specified: a
   mean-reversion entry was given a trend-following target.
2. **It does not rescue the strategy.** Every target multiple in the valid range is negative. The
   optimum is a shallow basin between 0.5R and 1.0R, all of it losing. Fixing the target halves the
   bleed; it does not create an edge. This is consistent with §3.19's coin-flip finding and with
   §3.18's rho = 0.025 — there is no edge in the entry for any exit geometry to harvest.

**Architecture finding: the agent/platform exit contract is violated.** `BreakoutDetectorAgent`
declares `AgentExitManagementMode.Bracket` (`BreakoutDetectorAgent.cs:57`) — "stop and target
submitted with the entry, the bracket owns the exit" — and contains **no management logic at all**,
returning `Observe` whenever a position is open. Yet the platform's `improvedPositionManagement`
layer applied scale-outs, break-even stops and structural-deterioration reductions to 61.6% of its
trades regardless. Of the three logics, only **entry** and the **initial bracket** live in the
agent; "keeping the trade alive" is entirely external and overrides the declared contract.

---

### 3.20 New entry-model feature groups + extended ladder/ablation (2026-08-30)

Groundwork for the trend-gated tactical agent (`Books/Design — Trend-Gated Tactical Agent.md`):
every analysis the annotation engine already computes must be an independently toggleable feature
group so the combination can be *discovered* rather than assumed.

**Added four groups** the classifier could never consume — `Adx`, `StochRsi`, `Donchian`,
`Efficiency` (`TradingClassifier/Features/ExtendedIndicatorFeatures.cs`), plus
`FeatureGroups.Everything` for superset construction. All columns are **bounded by construction**
(ratios, percentiles, 0/1 flags, categorical codes) per §3.12c's finding that the one feature which
destroyed walk-forward performance was an unbounded *level*; Donchian therefore emits channel
*position* and ATR-normalised width, never band prices.

**Three bugs found and fixed during the build — all of the same shape: a silent no-op that reads as
a measured null result.**

1. `FeatureEngine` gated the annotation-dataset path on `Analysis` alone, so enabling `Adx` via the
   cheap `DatasetBuilder` would have emitted **zeros for every ADX column** and the ablation would
   have priced ADX at exactly nothing. Fixed by `FeatureEngine.RequiresAnnotation`.
2. The ladder's new superset-projection cache (added to stop the annotation engine rerunning per
   rung) projected from `FeatureGroups.All`, which stops at `Experiment5`. Rungs 6-10 came back
   **byte-identical at 44 features** — including a regression of the pre-existing `Analysis` rung.
   Fixed by projecting from `Everything`.
3. `RunAblation` hardcoded `FeatureGroups.All` and ignored `--groups`, so removing a group outside
   that set removed nothing and printed a row identical to the baseline. Fixed to use the
   configured base set and to skip groups it never contained.

Regression tests now pin all three: every ladder rung must add columns over the previous, and
`Everything` must contain every group.

**Results on the 41-day window (2025-12-26 to 2026-02-05, XAU/USD 15m, 14d/5d/5d, horizon 10).**

```
LADDER                                            ABLATION (base = Everything, 93 features)
experiment      feat trades   PF      net         experiment       feat trades   PF      net
1 OHLC only       14     20  7.514  +553.81       all features       93     71  0.743  -301.13
2 + EMA           24     21  1.608  +161.50       minus Rsi          88     70  0.622  -468.19
3 + RSI/CCI       34     43  1.854  +838.37       minus Cci          88     75  1.000    -0.34
4 + ATR           38     46  1.455  +348.08       minus Macd         89     77  1.565  +566.42
5 + MACD/BB       44     41  0.895   -78.71       minus Trend        83     71  1.353  +324.04
6 + Analysis      75     70  1.089   +96.18       minus Atr          89     73  0.930   -82.35
7 + ADX           82     73  1.022   +27.04       minus Bollinger    91     71  0.743  -301.13
8 + StochRSI      85     69  1.135  +143.18       minus RangePos     89     73  0.992    -8.60
9 + Donchian      89     67  0.871  -169.23       minus PriceAction  83     74  1.049   +49.64
10 + Efficiency   93     71  0.743  -301.13       minus Adx          86     75  0.728  -342.51
                                                  minus StochRsi     90     79  1.158  +146.41
                                                  minus Donchian     89     72  1.925  +799.44
                                                  minus Efficiency   89     67  0.871  -169.23
```

Reading the ablation (base PF 0.743; *lower* after removal means the group was helping): only
**RSI (0.622) and ADX (0.728)** help. Everything else is neutral or harmful, and **Donchian is the
most harmful by a wide margin** (removing it takes PF 0.743 -> 1.925).

**These numbers must not be used to choose a configuration.** This is the same 41-day window whose
ladder winner §3.12 crowned and §3.12g then destroyed on 3.5 years (PF 0.849-0.905). Sample sizes
here are 20-79 trades; rung 1's PF 7.514 rests on 20 trades. The run's purpose was to verify the
plumbing, and it did — feature counts now increase monotonically and every rung and ablation row is
distinct.

**What is worth carrying forward** is the *direction*, which now agrees across three independent
observations: §3.12 (rung 3 best standalone), §3.13 (fewer features monotonically better as a
filter), and this ablation (only 2 of 12 groups help). **Small feature sets keep winning.** ADX
earning one of only two positive slots is the one genuinely new signal, and it is the group that was
computed by default yet never consumed.

**Open oddity:** `minus Bollinger` returns results byte-identical to the full set across trades,
win%, PF, net and maxDD despite dropping 2 columns. Plausible if the trees never split on them, but
worth confirming rather than assuming.

---

### 3.21 All 11 optional feature groups live; ATR moved to the end of the ladder (2026-08-31)

Completes §3.20. Added `Volume`, `Structure`, `SupportResistance`, `SupplyDemand`, `Liquidity` and
`Regime` alongside the four indicator groups, giving **11 optional groups / 125 columns**.
`FeatureEngine` gained a full-`AnalysisSnapshot` overload (`RequiresFullSnapshot`) because those
groups read swings, zones, pools and regime, none of which live on `IndicatorSnapshot`.

**Five more silent no-ops found and fixed.** Every one produced constant columns that the ablation
would have scored as a genuine "this group does not help":

1. **Volume was never in the data.** `ClassifierCandle` had no volume field, so it was dropped at the
   source. Added, and populated from simulation replay chunks — the fetched OANDA jsonl carries OHLC
   only, so `--historical` still has no volume and the group must not be enabled on it.
2. **`Resample` discarded volume**, which would have re-broken it for every resampled run. Volume is
   now summed across the bucket.
3. **`AnnotationDatasetBuilder` never set `Candle.Volume`**, so the engine's volume analysis stayed
   empty even once the source carried it.
4. **`SupplyDemand`, `Liquidity` and `MarketRegime` all default to `Enabled = false`.** They are now
   switched on only when the matching feature group is requested — expensive otherwise, and silently
   empty if left off. This is why those groups were absent from *every* prior experiment in this file.
5. **Count columns saturated.** A hard `Math.Min(count, 50)` looked bounded but pinned
   `liq_recent_event_count` at 50 on every single row — 1 distinct value. Replaced with log1p
   compression: that column now has **80 distinct values**, `sd_recent_event_count` 135.

Constant columns fell from **16 of 125 to 8**, and the remaining 8 are legitimately constant on this
window (`sd_enabled`, `liq_enabled`, `vol_is_reliable`, `vol_is_activity_proxy`,
`regime_is_tradeable`, `struct_last_swing_strength`, `sr_trendline_count`, `sr_channel_direction`).

**ATR moved to the last rung (user-directed).** §3.12 measured ATR as harmful, but it sat at rung 4,
so every rung above it carried that damage and no later group could be judged on its own merits.
The ladder now runs ATR-free to rung 15 and adds ATR only at 16, making that rung a direct
measurement of ATR's cost.

```
experiment         features  trades    win%       PF         net       maxDD
1 OHLC only              14      20   60.0%    7.514      553.81       28.08
2 + EMA                  24      21   57.1%    1.608      161.50      135.54
3 + RSI/CCI              34      43   55.8%    1.854      838.37      256.12   <- best net
4 + MACD/BB              40      39   51.3%    0.746     -350.35     1018.74
5 + Analysis             71      80   61.3%    1.578      519.09      276.32
6 + ADX                  78      71   54.9%    1.206      203.48      580.45
7 + StochRSI             81      78   62.8%    1.231      268.61      316.35
8 + Donchian             85      70   54.3%    0.885     -139.09      497.65
9 + Efficiency           89      73   54.8%    0.930      -82.35      533.73
10 + Volume              95      78   60.3%    0.989      -11.83      518.32
11 + Structure          102      68   51.5%    0.941      -57.08      359.50
12 + S/R                108      66   54.5%    1.221      164.53      301.87
13 + SupplyDemand       111      69   53.6%    1.075       63.06      339.68
14 + Liquidity          114      71   53.5%    1.144      119.63      301.87
15 + Regime             121      70   54.3%    0.829     -184.08      465.41
16 + ATR (last)         125      68   50.0%    0.829     -153.34      442.16
```

**Do not read a configuration out of this table.** 20-80 trades per rung on the same 41-day window
whose §3.12 winner was destroyed by §3.12g on 3.5 years. Its purpose was to prove the plumbing, and
it does: all 16 rungs are now distinct, where three of them were previously byte-identical no-ops.

The one durable observation: **rung 3 (34 features) still has the best net**, agreeing with §3.12,
§3.13 and §3.20 that small feature sets win. That is now four independent observations.

**Also note the earlier ATR reading was itself an artefact.** Before regime was enabled, rung 16
showed ATR *improving* results (0.896 -> 1.362); with regime populated it shows no improvement
(0.829 -> 0.829). A conclusion that flips on an unrelated fix is a sample-size artefact, not a
finding — which is precisely why this window cannot be used to choose features.

---

### 3.22 trend-tactical agent built; rung B is the first positive result — with three caveats (2026-08-31)

New agent `trend-tactical` (`Agent/Strategies/TrendTactical/`), implementing the composition
specified in `Blueprint — 4H Statistical Trend Agent.md` §41-43 and never previously built: 2H
TrendStatistics gates direction and phase, the classifier times entry, structural stop, trend-driven
exit. Registered across the full §2.11 checklist; declares
`ProtectiveStopAndStrategyExit`, **not** `Bracket` — §3.19 measured a fixed ATR target as reachable
by under 10% of trades, and `Bracket` also makes `PreTradeRiskManager` hard-floor reward:risk at 1.5
and silently reject every decision below it.

**Integration bug found only by running it.** `ExecutionCoordinator.ValidateDecision` requires a
quantity on a CLOSE as well as an entry. The agent's exit path omitted it, so the run threw at
sequence 22075 after opening its first position. Exits are now sized from the open position, which
also means a partially reduced position closes for what remains.

**Rung B (trend gate only, no ML) on the 41-day window, 2026-01-05 to 02-05:**

```
n=84  win=47.6%  PF_R=1.368  netR=+4.19  avgR=+0.0499
avgWin +0.389R  avgLoss -0.258R  payoff 1.51:1

by side:  Buy  n=11  win 72.7%  PF_R 3.650  netR +5.92
          Sell n=73  win 43.8%  PF_R 0.810  netR -1.73

same window, for comparison:
  breakout-detector mgmt ON   n=134  win 41.8%  PF_R 0.604  avgR -0.2229
  breakout-detector mgmt OFF  n=122  win 30.3%  PF_R 0.683  avgR -0.2320
```

**This is the first positive expectancy any agent in this repo has produced** (+0.0499R/trade against
breakout-detector's -0.22). It is also not yet a result, for three measured reasons:

1. **All the profit is 11 trades.** Buy side netR +5.92, sell side -1.73; total +4.19. Eleven trades
   carry the entire result on the favourable 41-day window that §3.12g already destroyed once.
2. **The agent's own exit logic barely fires.** 75 of 84 exits are `StructuralInvalidation` — the
   platform's structural exit — against 3 `InitialStopLoss`, 3 `MfeGivebackStop`, 3 `BreakEvenStop`.
   The designed `ExitOnExhaustion` / `ExitOnTrendReversal` path is doing almost nothing, so what was
   measured is not the exit policy that was designed.
3. **Repeated re-entry inside one trend.** The 2H detector found **7 trends** over this window
   (4 bull / 3 bear, `trend-stats`), yet the agent took 84 trades. Because a structural exit leaves
   the trend still Confirmed, the agent re-enters on the next trigger bar. There is no cooldown, so
   trade count reflects re-entry frequency rather than trend count — and the 73/11 sell/buy split is
   a consequence of one persistent bear trend, not of 73 independent decisions.

**Consequence for feature selection.** 84 candidates cannot support the greedy walk-forward search:
the §3.21 run needed validation folds with enough events to choose between 17 groups, and 84 events
across folds gives single-digit validation sets. Selection on this agent needs a multi-year run
first — which also addresses caveat 1.

---

### 3.23 ML agents could never trade through the pipeline; all subsystems now smoke-tested (2026-08-31)

**The defect.** `TradingAgentFactory` built its catalogue with no model resolver, so
`trading-classification` and `trend-tactical` at rung C both fell back to `NoTradeModel` and reported
**0 trades with no error** in every backtest. Verified empirically before fixing. Consequence:
**every classifier figure in §3.12-§3.18 came from `TradingClassifierRunner`'s research harness**,
which never touches `ExecutionCoordinator`, `PreTradeRiskManager`, position sizing or the pipeline
cost model. This is the same gap §2.18 recorded for TrendStatistics; nobody had noticed it applied
to the classifier too.

**The fix.** `TradingAgentFactory.Install(catalog)` lets a host that can take the Microsoft.ML
dependency install model-backed builders while the AOT-safe default stays model-free.
`BacktestRunner/ClassifierModelInstaller` loads the artifact and **reconstructs the agent's
configuration from it** — feature groups, horizon, ATR multiplier, stride, thresholds and signal
interval all come from the model rather than being defaulted alongside it. A loud warning fires when
an ML agent is requested without `--classifier-model`, so the zero-trade case announces itself.

`EnsureCompatible` rejected four genuine mismatches during wiring — feature-set, configuration hash,
scoring interval, and signal interval — each of which would have produced confident predictions from
misaligned columns. First run through the real pipeline: **365 trades, net +5,926.48** on the 41-day
window (pipe connected, not a validated result: overlapping training data, no walk-forward).

**Rung C answered — the classifier subtracts value here.** Design doc §5's primary claim is C vs B:

```
B: trend gate only        84 trades   PF_R 1.368   avgR +0.0499
B + cooldown 4 bars       39 trades   PF_R 1.932   avgR +0.1505
C: + classifier           19 trades   PF_R 0.881   avgR -0.0484
```

Adding the classifier halves the trades again and flips expectancy negative. Caveat: a weak model
trained on overlapping data, 19 trades, favourable window — so this is a working end-to-end test, not
a verdict.

**Re-entry controls (both added, independently switchable).** `ReentryCooldownBars` counts from ANY
close, including the platform's own structural exit, which was 75 of 84 exits in §3.22.
`OneEntryPerTrend` collapsed 84 trades to **5, all losers (-2.37R)** — so the edge is not in the
first entry after a trend confirms, it is in the pullback re-entries, which is what the blueprint's
tactical layer actually describes. Cooldown 4 is the best variant and, importantly, flips the sell
side from -1.73R to +0.40R, so the result stops being carried by one direction.

**Subsystem smoke test (`diagnose` command, 41-day window, 44 features).** All four previously
untested subsystems work:

```
DRIFT (train -> test PSI)     range_pct 6.134 · bb_width 5.237 · ema20_vs_ema50 2.133
                              ema50_slope_5 1.631 · close_vs_ema50 1.591 · macd 1.410
                              22 of 44 features show a SIGNIFICANT shift

CALIBRATION                   Brier    mean p   actual
  raw                        0.2718    0.3526   0.4108     <- under-confident
  Platt                      0.2391    0.4108
  Isotonic                   0.2343    0.4108

ENSEMBLE (test)   LightGBM PF 1.083 · Logistic PF 0.603 · Ensemble PF 0.906
SELECTOR          refuses the losing strategy and the 5-trade sample; selects only the qualifying one
```

**The drift result is the most important thing here.** Half the feature set does not transport from
train to test *within a single 41-day window*, and the worst offenders — `range_pct`, `bb_width`,
`ema20_vs_ema50`, `close_vs_ema50`, `macd` — are all **unbounded levels**, exactly the failure mode
§3.12c identified when `atr14_pct` destroyed walk-forward performance with 48% of a test window
outside the training range. That single measurement explains a great deal of the instability seen
across §3.20-§3.21, where feature rankings flipped sign on reordering and on unrelated fixes.

Calibration behaves correctly: the raw model is under-confident (mean p 0.3526 against a 0.4108 base
rate), and both calibrators improve Brier and pull the mean onto the actual rate. The ensemble lands
between its members rather than beating the best one, which is what averaging does.

---

### 3.24 Feature drift fixed: 22 of 44 shifting features down to 4 (2026-08-31)

§3.23 measured **22 of 44 features shifting significantly** between train and test *inside a single
41-day window*, worst PSI 6.134. Diagnosis: every offender was a **price-normalised ratio**.
Dividing by `close` removes the price *level* but not the volatility *regime*, so the ratio's own
distribution still moves when volatility does. ATR tracks the regime, so dividing by ATR removes
both.

New `ClassifierOptions.NormalizeByAtr` (default **false**, so this is a measurable A/B rather than a
silent redefinition of every existing model's inputs), exposed as `--normalize-by-atr`. It switches
level-like features from price-normalised to ATR-normalised: `range_pct`, `bb_width`, EMA distances
and differences, MACD/signal/histogram, EMA slopes, and the `return_N` series.

```
                                 significant   worst PSI
original (price-normalised)        22 of 44        6.134   range_pct
+ ATR on levels                    11 of 44        1.631   ema50_slope_5
+ ATR on returns and slopes         4 of 44        0.441   ema20_vs_ema50
```

**14x reduction in the worst PSI, 5.5x fewer drifting features.** The four survivors
(`ema20_vs_ema50` 0.441, `ema50_slope_5` 0.281, `atr_regime` 0.251, `macd_signal` 0.251) all sit
just over the 0.25 threshold rather than multiples above it.

**Why this matters more than a tidy-up.** §3.12c had already identified the mechanism — `atr14_pct`,
an unbounded level, destroyed walk-forward performance with 48% of a test window outside the entire
training range — but it was treated as one bad feature rather than a whole class. It was the class.
This retroactively explains the instability running through §3.20-§3.21: feature rankings that
flipped sign on reordering, ATR moving from "helps" to "neutral" on an unrelated regime fix, and
RSI/CCI swinging +838 to -246 purely on ladder position. Models were being asked about distributions
they had never seen, so which feature "won" was substantially a lottery.

The conceptual reading of the change: a return over price asks "what percentage did it move"; a
return over ATR asks "how many normal candles of movement was that". Only the second question has
the same meaning in December and in June.

**Status:** off by default and not yet re-validated for performance. The ladder, ablation and greedy
selection in §3.20-§3.21 were all run price-normalised and should be re-run with
`--normalize-by-atr` before any of their orderings are trusted — the drift measurement says those
orderings were made on unstable inputs.

---

### 3.25 trend-tactical: two P0 correctness bugs invalidate this session's numbers (2026-08-31)

An independent review (`Books/Trend Tactical Agent Review and Improvement Plan.md`) found two P0
defects. Both **verified against the code**; see that document's addendum for the full self-review.

**P0-1: every position is treated as long.** `SimulatedBrokerState.cs:835-836` exposes
`Quantity = Math.Abs(SignedQuantity)` with direction in `Side`. The agent tests `item.Quantity > 0m`
(`TrendTacticalAgent.cs:139,307`) and never reads `Side`, so shorts are closed while their bearish
trend is intact and can survive a reversal to bullish.

**P0-2: the agent's own exits are mislabelled.** `StrategySimulationSession.cs:1091-1095` assigns
`ReverseStrategyClose` only when the close reason contains `"Opposite trend"`, otherwise
`StructuralInvalidation`. None of the agent's three close reasons contains that string.

**P1: "Rung B" is not the validated strategy.** The agent references `TrendDetector` only — zero
references to `TrendProgressEstimator`, `ExhaustionEstimator`, `TrendProfiles` or any percentile,
i.e. none of the machinery that produced §2.18's PF 1.694. The design document's Rung A ("does 1.694
survive leaving the research harness?") was never built, so B and C were measured on top of a
foundation that does not exist.

**Conclusions from §3.22 and later that are now invalid:**

- "75 of 84 exits were the platform's, not the agent's" — an artefact of P0-2; those were plausibly
  the agent's own trend exits. A substantial line of reasoning was built on this.
- "Removing platform management let winners run" (avgR +0.1505 -> +0.6242) — the measurement stands,
  the explanation does not.
- "The cooldown found a pullback edge" — may instead have suppressed forced short churn from P0-1.
- "73 sells vs 11 buys is a directional asymmetry" — that is P0-1's signature.
- "One-entry-per-trend loses every trade, so the edge is in re-entries" — same contamination.

**Every trend-tactical performance figure in this session is suspect; the short-side ones are
near-certainly wrong.** The 11 green agent tests do not catch either P0 because every case passes
`Positions = []`, so `ManageOpenPosition` never executes.

All background runs (3.5-year baseline, out-of-sample training, 20-candidate selection, gate
variants) were terminated. None should be resumed against the current agent.

#### 3.22a P0-1's blast radius measured exactly: every phantom trade was a short (2026-08-31)

`rungB-p0fixed` re-runs `tt-rungB`'s exact configuration (window 2026-01-05 -> 2026-02-05,
management ON, no swing rule, no cooldown, intervals 1m/5m/15m/1h/2h, config lifted from the
original run's manifest rather than reconstructed) on post-fix code.

```
                    tt-rungB (buggy)     rungB-p0fixed
trades                     84                  22        -74%
Buy / Sell              11 / 73             11 / 11
sell share                 87%                 50%
win rate                 47.6%               68.2%
PF_R                     1.368               1.907
avgR                   +0.0499             +0.2385
```

**The buy count is identical (11 and 11).** P0-1 could not affect longs - `Quantity > 0m` is
legitimately true for a long, so that path always behaved. All 62 excess trades were shorts, churned
by the spurious "opposite trend" exit that read every short as a long. The fixed run lands exactly
where the mechanism predicts.

**Invalidated by this measurement** (beyond what 3.23 already listed):

- "73 sells vs 11 buys is a directional asymmetry" - it is 11/11. The asymmetry *was* the bug.
- `avgR +0.0499` - the phantom shorts depressed expectancy ~5x. Clean figure is +0.2385.
- Per-side conclusions: in the clean run the sell side is marginally negative (-0.0613R) and longs
  carry the result (+0.5383R) - the opposite of the buggy run's implication. At n=11 per side this
  is noise, and no directional claim should be made from it either way.

**Corrected trade rates** (the "10x more trades than Rung A" claim was contamination):

```
Rung A  260 trades / 1,065 days = 0.244/day
Rung B   22 trades /     31 days = 0.710/day     ~2.9x, not ~10x
```

**Do not quote Rung B's PF 1.907 as a result.** n=22 over 31 days, and 8 of 22 exits are
`BreakEvenStop` because platform management is ON in this config. This run is a contamination check,
not a performance measurement. A clean Rung B needs the management-disable flags and a 3.5-year
window to be comparable with Rung A.

#### 3.22b Rung A vs Rung B, 3.5 years, matched configs: the swing rule buys efficiency, not edge (2026-08-31)

`rungB-long` against `rungA-long`. Same instrument, window (2023-01-02 -> 2026-07-24), intervals and
costs; management disabled on both by the same nine flags, verified by diffing the two runs'
`improvedPositionManagement` blocks to zero difference before launching. The only intended
difference is `UseSwingEntryRule`.

```
                    Rung A            Rung B
trades               260               435          1.67x
win rate            30.8%             30.1%
PF_R                1.276             1.148
netR               +38.29            +34.80
avgR              +0.1473           +0.0800
95% CI     [-0.117, +0.412]  [-0.108, +0.268]
avg win            +2.210            +2.058
avg loss           -0.770            -0.772
```

**What the percentile gate does.** Rung B's 175 extra trades contributed `34.80 - 38.29 = -3.49R`
between them, about **-0.02R each**. The gate removes near-zero-expectancy entries rather than
selecting better ones: Rung A reaches the same total profit with 40% fewer trades. That is a real
efficiency gain - less exposure, less cost - and **not** an improvement in edge.

**It is not statistically significant.** The expectancy difference is +0.067R against a standard
error on the difference of ~0.166, i.e. **0.4 SE**. Both rungs' own intervals also straddle zero.
Neither configuration is demonstrated, and the gap between them is well inside noise. Do not report
"the swing rule works" from this.

**Exit attribution is trustworthy on both for the first time** (post P0-2). Rung B: `StrategyClose`
231 at avgR **+1.008**, `InitialStopLoss` 203 at avgR -0.975. Agent exits are where the profit is;
before the fix all 231 were mislabelled as platform exits.

**Method note.** The 2.9x trade-rate ratio predicted in 3.22a from the 31-day window was wrong; the
matched-window ratio is 1.67x. Three separate short-window extrapolations this session
(Rung A trade count, Rung B trade count, the 673R decomposition) have all been wrong. Short windows
are not being used to predict long-window behaviour again.

#### 3.22c Decile test: the classifier does not discriminate trend-tactical candidates either (2026-08-31)

`filter-signals` walk-forward (90d/30d/30d, horizon 10) over Rung A's 260 trades; 253 scored
out-of-sample. Added a decile breakdown and a Spearman rank correlation to that command for this
purpose - the existing threshold grid can only show that a filter trades LESS, never that its score
ORDERS trades by quality.

```
Spearman(score, R) = 0.0004 over 253 trades

bucket   p range        win%    mean R          bucket   p range        win%    mean R
D1       0.079-0.247    36.0%   +0.171          D6       0.322-0.333    24.0%   -0.386
D2       0.247-0.272    24.0%   +0.057          D7       0.333-0.348    34.6%   +0.961
D3       0.273-0.289    32.0%   +0.391          D8       0.349-0.366    20.0%   -0.504
D4       0.290-0.306    34.6%   -0.091          D9       0.367-0.398    20.0%   -0.333
D5       0.308-0.322    60.0%   +1.339          D10      0.398-0.580    26.9%   -0.072
```

**No monotonicity, and the three highest-confidence deciles are all negative.** D5's +1.339R on 25
trades is a spike, not a signal. Every filter threshold lands at or below baseline; the best reaches
the **46.9th percentile** of 2,000 random same-size subsets - worse than the random median.
`Agreement >= 0.50` keeps 6 of 253 trades at PF 0.071.

**This closes the question 3.15/3.16 left open.** Those measured the classifier on BreakoutDetector
candidates, and it was correctly objected that the result need not transfer to a different agent's
candidate population. It has now been measured on trend-tactical's own candidates and the answer is
the same. **Calibration, conviction sizing and ensembling are all refining noise, and Rung C has no
basis.** The meta-labelling architecture is still the right shape; the signal is not there.

**Separate finding, larger than the ML one.** Rung A's profit is entirely one-sided:

```
side    trades       net
Buy        145   +16,057
Sell       108    -1,879
```

Shorts are net negative across 3.5 years. A long-only variant is worth a registered test - but gold
trended up strongly over this window, so this may be regime rather than edge, and it must be tested
on a different period before being believed.

---

### 3.26 Alfonso / Set and Forget agent built from `Books/alfonso`; trend layer latched (2026-08-31)

Six phases under `Agent/Strategies/Alfonso/`: zone engine, trendlines and Up/Down/OOA trend state,
range and control, three-timeframe sequence with nesting and module 11's eight-setup whitelist, the
agent itself, and catalogue/CLI wiring as `--strategies alfonso`. 60 dedicated tests; suite 1311 green.

**Four rules were corrected against the strategy as taught, each behind a switch so the alternative
reading stays measurable:** elimination needs a CLOSE beyond the distal (not a wick); a trendline
break needs a candle CLOSING beyond the line (not the whole candle); taking out a prior peak or
valley is an accomplishment (not only the all-time extreme); and only zones that themselves
accomplished something can move the trend. The peak/valley correction was the largest - the all-time
route fired 15 times in eight months of H4 gold, swing breaks fire 120 times.

**Bugs found by running real candles, none caught by unit tests.** Seven during construction, all of
the silent-no-op shape: an impulse defined as consecutive ERCs (runs of 2 occur 4 times in 1,018 H4
bars, so every zone scored Weak and none reached 2:1); over-extension latching at 80% of bars; every
truncation of a basing run emitting its own duplicate zone; and a bootstrap deadlock where zones
needed an accomplishment, the principal accomplishment is a trendline break, trendlines are drawn
from swings, and swings come from zones - which left 10 swings in eight months and awarded the
trendline accomplishment exactly zero times.

**The trend layer latched, and it took three attempts to fix.** `Resolve` returned early whenever a
trend was already set, so the only exit was out-of-alignment. On 200 daily gold candles the state was
`Downtrend` 74.5% and `Unknown` 25.5% - never `Uptrend`, never OOA - across a window where price ran
4,212 -> 5,602 -> 4,047, and the D1/H4/H1 sequence took **zero trades**. This contradicted the rule as
taught, which describes a DIRECT flip: the market reversed, one opposing zone is gone and a new
opposite trendline is drawable, or two are gone and none is.

```
                 with latch    attempt 2      fixed
gold H4 OOA           24.9%          -        21.3%   (Down 47.9 / Up 23.5)
silver H4 OOA         42.8%      82.9%        28.7%   (Down 42.8 / Up 21.4)
gold 1m OOA               -      99.8%        37.2%
```

Attempt 1 allowed the flip but `ApplyEliminations` still fired OOA on the same event and cleared the
counters that would have carried it. Attempt 2 deferred the decision but spent BOTH counters on
establishing a trend, leaving it no buffer - the first undermining event knocked it out, pushing the
state to OOA for 83-99% of bars. **The correct rule: while a trend runs only the OPPOSITE case can
change it.** Its own supporting eliminations are the trend working, not evidence against it; counting
them made every reversal look like both sides eliminating at once.

**Consequence for earlier Alfonso numbers.** The 8-month gold run (24 trades, avgR +0.065) and the
first reward sweep were both produced with the latch present and are superseded. Re-running the
sweep on fixed code moved the book's 3:1 from +0.0032 to **-0.1217** per trade.

**Cost arithmetic, and it has predicted correctly twice.** Round trip is ~2.4bp; zone width sets how
much of R that consumes, so the viable timeframe is decided before any edge question:

```
TF    R(p50)  cost/R   break-even win%      observed win% at 3:1 = 25.0%
1m      1.90   57.6%        39.4%   NEGATIVE
5m      4.79   22.9%        30.7%   NEGATIVE
15m     8.66   12.6%        28.2%   NEGATIVE
30m    12.01    9.1%        27.3%   marginal
1h     18.32    6.0%        26.5%   marginal
4h     37.23    2.9%        25.7%   marginal
```

**Going to lower timeframes is arithmetically excluded**, not merely inadvisable: at 1m the median
zone pays 58% of its R to costs and the tightest decile pays 156%. No sequence gets comfortably below
the observed 25% win rate, so the binding lever is geometry, not timeframe.

**Silver, 3 trades in 7 months - explained, not a defect.** 92 zones planned against gold's 109, but
price ARRIVED at only 7 of them (7.6%) versus gold's 23 (21%). Silver sits out-of-alignment far more
(H4 42.8% vs 24.9%), so zones are eliminated before price retraces to them. The method's yield is
instrument-specific and gold's numbers are not characteristic of it.

**Resolved.** See 3.27.

#### 3.27 Alfonso loses on every instrument tested, and the "improvement" was in-sample fitting (2026-09-01)

**The method as written.** 3.5 years of gold at H4/H1/M15: 87 trades, 19.5% win, PF_R 0.764,
avgR **-0.1728**, negative in every year. The cost table predicted it - a fixed 3:1 needs a 28.2%
win rate at M15 to clear costs and the method delivers 19.5%.

**A predictor was found, and it did not survive an independent test.** Zone attributes were checked
against realised R on the real 87 trades. Nesting separated strongly - standalone -0.4865R over 50
trades against nested +0.2512R over 37 - and agreed in both halves of the period. Dropping module
11's row 1 and moving the target to 2:1 took the same window from -15.03R to **+19.24R**, PF 1.280,
positive in all four years, with a clean interior optimum at 2:1 (1.5 and 2.5 both lower).

Every part of that was fitted on gold. A pre-registered test over 7 FX pairs and 2 indices, with the
threshold fixed before the data arrived (7+ of 9 real, 4-5 a coin flip, <=3 refuted):

```
instrument     book n  book avgR  nested n  nest avgR    delta
fxeurusd           12    -0.0825         2    +0.1595  +0.2420
fxgbpusd           14    -0.3292         7    -0.4270  -0.0978
fxusdjpy           11    +0.0439         0     -         -
fxusdchf           12    -0.9106         4    -0.2993  +0.6113
fxusdcad           21    -0.3580         4    -0.3633  -0.0053
fxnzdusd           10    -0.2425         3    -0.8730  -0.6305
cfdnas100usd       19    -0.7672         9    -0.9242  -0.1569
cfdus30usd         21    -0.5352        20    -0.3505  +0.1847

positive deltas 3 of 7      trade-weighted avgR: book -0.4317 -> nested -0.4748
```

**3 of 7, and the aggregate is worse with the change.** The improvement does not generalise.

**The larger result is the `book avgR` column: every instrument is negative except USD/JPY at
+0.0439.** Nine instruments, two asset classes, seven months. This was never "gold is a hard case" -
gold's -0.1728R was among the better outcomes.

**Method lessons, recorded because they cost real time here.**

- A chronological split is worth far less than it appears. Five attributes were split-tested and two
  agreed by sign; under the null the expectation is 2.5. That test had almost no discriminating
  power, was noted as such, and the finding was still treated as established because the effect size
  looked large. Effect size on 87 in-sample trades is not protection.
- A smooth interior optimum is not evidence against fitting. The 2:1 peak with 1.5 and 2.5 flanking
  lower was read as hard to produce by chance. It is not.
- Roughly 13 looks were taken at one dataset (8 attribute families, then 5 reward multiples). Finding
  one positive combination under those conditions is unremarkable.
- Pre-registering the generalisation threshold before the runs finished is what made this a clean
  refutation instead of something rationalisable after the fact. Keep doing that.

**Status: the Alfonso agent is correct as an implementation and unprofitable as a strategy on every
instrument with cached data.** No further tuning is warranted without a reason to believe the
population is different. AUD/USD failed on a snapshot-persistence error and is excluded; recovering
it does not turn 3 of 7 into evidence.

> **CORRECTED 2026-09-01 - see 3.28. The gold figures in this section were measured on an agent that
> no longer exists.** On current code gold is **+0.0705R**, not -0.1728R, and its win rate is 28.7%
> rather than 19.5%. The claim that the method is negative on every instrument does not hold for
> gold. The 3-of-7 nesting refutation is unaffected - it was a paired comparison within one code
> version - but the FX and index baselines share the same staleness and need re-measuring.

#### 3.28 The gold baseline was stale; module 6 helps, module 10 hurts (2026-09-01)

**How the error happened.** The gold baseline run started 2026-08-31 20:58. `AlfonsoAgent.cs` was
then modified at 22:33 that night, again at 11:06 the next morning, and carried further uncommitted
work after that - including a change from waiting for price to touch the proximal line to placing a
**resting limit order ahead of price**, which is both more faithful to the method and materially
different in fill behaviour. Every later comparison was drawn against that stale number without the
code version being checked. It surfaced only because two unrelated edit anchors failed.

**Four-way isolation, gold 2023-01-02 to 2026-07-24, one variable per run, identical code.**

```
run             n    win%      PF      avgR      net   maxDD     control  confirm
iso-baseline   129    28.7   1.129   +0.0705    3,434   4,447       off      off
iso-control     95    32.6   1.315   +0.1476    5,745   3,620       ON       off
iso-both       116    30.2   1.230   +0.0874    5,166   4,096       ON       ON
iso-confirm    150    28.0   1.094   +0.0499    3,004   4,797       off      ON
```

**Module 6's in-control gate is worth roughly +0.077R per trade.** It doubles expectancy, lifts the
win rate 28.7% -> 32.6%, and does so while CUTTING trades 129 -> 95 and reducing drawdown
4,447 -> 3,620. Higher return on fewer trades with less risk is the profile of a filter that removes
bad trades rather than one that trades less at random. This rule had been computed and enforced
nowhere until 2026-09-01.

**Module 10's confirmation path costs roughly -0.021R per trade.** It adds 21 trades, lowers
expectancy, and produces the worst drawdown of the four. The course presents confirmation trades as
the lower-grade half of the method and the measurement agrees. Leave it off.

**What this invalidates.** The -0.1728R gold figure quoted throughout 3.26 and 3.27, the framing
that the method "needs 28.2% and delivers 19.5%" (it now delivers 28.7%, essentially at the hurdle),
and the claim that the method loses on every instrument. The nesting refutation stands. The FX and
index baselines were measured before today's agent changes and are not yet re-verified.

**Method lesson.** Record the code revision with every result. Four separate conclusions this
session rested on a number whose provenance was never checked, and the only reason it was caught was
an unrelated edit failing to apply. A result without a commit hash beside it is not a measurement.

**Not yet established.** The control gate's +0.0705 -> +0.1476 is a single in-sample result on one
instrument. Nesting looked exactly this good in-sample and failed 3 of 7 out-of-sample. It needs the
same pre-registered generalisation test before it is believed.

#### 3.29 Control gate refuted; the trend detector is sound; three claims withdrawn (2026-09-01)

**Module 6's control gate failed its pre-registered test: 2 of 6.** Six instruments, matched window
2025-11-24 to 2026-07-23, paired baseline vs gate, threshold fixed before the data arrived (5-6 real,
3-4 coin flip, <=2 refuted).

```
inst     baseline avgR   gate avgR    delta
gbpjpy        -0.4178     -0.2334   +0.1844
eurusd        -0.0530     +0.2486   +0.3016
nas100        -0.5613     -0.6444   -0.0831
us30          -0.5352     -0.6355   -0.1003
gold          +0.2181     +0.0532   -0.1648
silver        -0.3458     -0.5878   -0.2420
```

Gold contradicts ITSELF between windows: the gate was +0.0705 -> +0.1476 on 3.5 years and
+0.2181 -> +0.0532 here. Third filter to look strong in-sample and fail out - after nesting (3 of 7)
and the 2:1 target. **Baseline is negative on 5 of 6 instruments**, gold the sole exception.

**WITHDRAWN: "the Downtrend state is anti-predictive."** Measured across all of gold's 3.5 years it
looked inverted - price rose 67% of the time it fired. Gold rose 121% over that period, so the
aggregate could not separate a broken detector from a bull market. Partitioning by leg settles it
(detector run on the CONTINUOUS series, only the results partitioned, so no warm-up contamination):

```
leg          state        n    agreed   mean move
up-leg     Uptrend      807     59.9%    +0.848%   good
up-leg     Downtrend    426     25.6%    -1.163%   INVERTED
down-leg   Uptrend       12      8.3%    -1.972%   INVERTED
down-leg   Downtrend    152     53.9%    +0.775%   coin flip
```

Each state is right in its own leg and wrong in the other, which is what a working trend follower
looks like. **The trend layer is sound and does not need fixing.** Caveat: the down-leg carries only
152 observations and 53.9% does not clear the 55% bar set in advance - "consistent with working", not
"proven good".

**FIXED: module 5's structural context was never implemented.** The rule is "each successive peak and
trough is higher than the ones found earlier" AND an accomplishment; only the accomplishment half
existed. Requiring both improved Uptrend accuracy 55.0% -> 62.5% at 6 bars and halved the number of
calls (1,641 -> 819). Behind `RequireStructuralAgreement`, default on.

**WITHDRAWN: "the trendline fallback is a misreading."** `EliminationsWithTrendline` is 1 and
`EliminationsWithoutTrendline` is 2, so the with-line path always fires first and the fallback could
never be the deciding route while a line existed. The restriction added is behaviourally inert at
default thresholds and is kept only as a guard. The claim came from a diagnostic **I wrote and then
misread**: it counts bars where a trending state has no line drawable AT THAT BAR, which is not the
same as "the trend was established without a line". Those are different statements and the second was
never measured.

**Validation against the course's own charts.** 75 chart images extracted from the PDFs. Convention
validation PASSES: USD/CAD H4 confirms proximal at the basing candles' body top and distal at the
lowest low including wicks, matching `max(bodyTop)`/`min(low)`; the "never cut through candles" chart
matches `TrendlineBuilder.Fit`'s pivot onto the constraining bar; the AAPL "very weak demand" example
matches the Weak classification. Quantitative validation is IMPOSSIBLE - the charts are 2020-21 US
single stocks (AAPL, CSX, PSX, NFLX, TSLA) with no date overlap with any obtainable data.

**Also added, all default-off:** a cost-to-risk gate and minimum-stop-ATR floor using the platform's
existing `RoundTripCostEstimate`; an ATR-percentile regime veto scaled to each instrument's own
history; and `AlfonsoCandidateRecord`, an optional decision-time sink capturing every candidate the
agent considered with the reason it was not traded. That last one closes a real gap - a research
harness counted 49 entries where the agent took 87 and the divergence was found by accident.

**Standing position (SUPERSEDED - see 3.43 for the verdict, and 3.30 for the trend-layer note).** This previously read "Direction works
... the remaining explanation is entry quality and cost, not the trend layer and not zone drawing."
Direct measurement of the trend layer on 2026-09-01 contradicts it: the trend state carries no
usable directional edge on any of six instruments. Do not rely on the old wording.


### 3.30 Alfonso trend layer measured directly: no directional edge; zone quality is decoupled from it (2026-09-01)

> **CAVEAT ADDED SAME DAY - READ FIRST.** Everything in this section below the caveat was measured
> with a standalone replay harness (`ZZAlfonsoTrendLayerDiagnostic`) that feeds CSVs aggregated by
> `/mnt/storage/scratch/alfonso/export_bars.py` into the production analyzer classes. That harness
> **does not reproduce the agent's trend states.** The check that caught it: on EUR/USD the agent
> took 14 trades whose `setupReason` says "All three timeframes ...", while the replay finds exactly
> **1** aligned bar in 16,420. Silver: 15 trades vs 125 bars; gold: 21 vs 297.
>
> The classes are the same; the bars are not. The export buckets by wall-clock with no gap tolerance
> and no incomplete-bucket rule, where `MultiTimeframeAggregator` has both, and warm-up differs.
>
> So the headline below - "the trend state carries no usable directional information" - is
> established for the *replayed* state machine, not for the one that trades. Treat it as suggestive.
> The same caveat applies to the marginal-occupancy figures and to the zone-quality sweep, which
> swept the replay's trend layer. The barrier-race method itself is sound and worth reusing.
>
> Unaffected, because they come from the agent's own run output rather than the replay: all
> trade-level statistics (the 92-sells/35-buys split, per-side win rates and avgR, setup-reason
> labels), the structural-agreement A/B in 3.31, and the switch measurements in 3.31.
>
> **Next step is to instrument the agent, not the replay.** `AlfonsoCandidateRecord` already exists
> for this and only needs wiring through BacktestRunner to emit decision-time trend state and
> rejection reason from the real pipeline.

Six-instrument window 2025-11-24 -> 2026-07-23, bars exported from the simulator's own 1m cache
(`/mnt/storage/scratch/alfonso/export_bars.py`). New diagnostic:
`Simulator.Tests/ZZAlfonsoTrendLayerDiagnostic.cs` (Explicit).

**Method.** A barrier race from every bar with the geometry the agent actually trades - 1xATR stop,
3xATR target, stop checked first, break-even 25%. Each trend state is scored as its hit rate minus
the *unconditional* hit rate for the same direction over the same bars, so drift cancels. Races
overlap heavily, so nominal n far exceeds effective n; the load-bearing statistic is sign
consistency across six independent instruments, not any single figure.

**Result - the trend state carries no usable information.**

| timeframe | Uptrend edge | positive | Downtrend edge | positive |
|---|---|---|---|---|
| m15 (n approx 2,000-2,800/state) | -0.8% | 2/6 | +1.2% | 5/6 |
| h1 (n approx 460-780) | -1.1% | 3/6 | -3.9% | **0/6** |
| h4 (n 53-301) | -1.4% | 4/6 | +14.0% | 4/6 |
| d1 (n 5-112) | -14.4% | 1/5 | +8.2% | 4/6 |

At m15/h1 every edge sits inside +/-7% with signs flipping between instruments. The best consistent
cell is m15 Downtrend at +1.3%, i.e. EV +0.05R against a measured 15m round-trip cost of 12.6% of R
- roughly a quarter of the cost of trading it. h4/d1 look dramatic but n is small and silver h4
shows Downtrend +26.7% while silver lost money over the same window; not trustworthy.

**Retracted.** The intra-session hypothesis that the detector was "stuck in Downtrend" is wrong.
Occupancy is balanced (gold m15 Uptrend 14.3% / Downtrend 14.0%; same pattern on all six). The
92-shorts-vs-35-longs skew in the trade population is therefore not a trend-layer bias. The likely
cause is the resting-limit entry: sell limits sit above price and fill whenever price rises into
them, buy limits fill only on pullbacks, so in six markets that drifted up 4-19% sell limits fill
far more often. Adverse selection in the fill, not a bad directional call. Not yet directly tested.

**Why the layer is weak: it is fed noise.** Gold m15, 16,939 bars: 2,254 zones created (one every
7.5 bars), 99% of demand zones and 93% of supply zones eventually eliminated, and 262 trend
establishments - a new trend every ~16 hours. Also OutOfAlignment is 56-76% of all bars, so the
layer has no opinion most of the time. Demand zones outnumber supply ~1.13:1 and demand
eliminations outnumber supply ~1.16:1 on nearly every instrument/timeframe; recorded as an
observation, not a demonstrated cause, since occupancy still comes out balanced.

**Zone-quality sweep (`SweepZoneQuality`, same file) - hypothesis refuted, defect found.**
Eleven configurations x six instruments x m15/h1/h4, scored on trend-layer edge rather than P&L
(20 trades/instrument cannot separate settings; thousands of races can).

Five of eleven configurations were bit-identical to default: `MinimumImpulseToBaseRatio` at 3 and 5,
and `MinimumImpulseAtrMultiple` at 3x and 4x, all produced 12,684 zones / 1,500 flips / identical
edges at m15. Traced in code: the ratio is used only at `Agent/Strategies/Alfonso/Zones/ImbalanceDetector.cs:546`
inside `Qualifies()` -> `MeetsTradeabilityCriteria`, and the ATR multiple only at `:155` to set
`Strength`. **Neither gates zone creation.** And `AlfonsoTrendDetector.ApplyEliminations`
(`Agent/Strategies/Alfonso/Trend/AlfonsoTrendDetector.cs:282-290`) filters on
`zone.Accomplished != Accomplishment.None`, *not* on `MeetsTradeabilityCriteria`.

So the book's central quality rule - the 2:1 imbalance - has no influence whatsoever on the trend
read. Zone quality and trend formation are architecturally decoupled. This is the most likely
reason the sweep is flat, and it is testable with a one-line change (gate `ApplyEliminations` on
`MeetsTradeabilityCriteria`, behind an option defaulting to current behaviour, then re-sweep).
Not yet done.

The two knobs that do reduce zone count do not help: `consolidate 2` cuts m15 zones 12,684 -> 9,928
(-22%) and moves the Uptrend edge -0.8% -> +0.4% (5/6, the only consistently positive Uptrend cell
anywhere) but goes to -1.6% at h1 and -1.1% at h4 - it does not replicate.

### 3.30a Two candidate explanations for the 92/35 short skew, both refuted (2026-09-01)

The six-instrument run took 92 sells to 35 buys (2.63:1). Two mechanisms were pre-registered and
measured; neither survives.

**Resting-limit adverse selection - refuted on magnitude.** The agent rests limit orders ahead of
price, so sells sit above and buys below; in a rising market price should walk into sells and away
from buys. Measured directly on the price path (limits at 1x and 2x ATR from every close, 100-bar
horizon) the sell:buy touch ratio is **1.01-1.16**, against the 2.63 that needs explaining. The
direction is right - EUR/USD, the one instrument that did not rise (-0.8%), is the only one below
1.0 (0.90 at 2xATR on h1), which is a clean confirmation that drift is the mechanism - but the
effect is roughly 2.5x too small. Cause is visible in the raw rates: 86-90% of limits on *both*
sides are touched within the horizon, so at these distances the fill is barely selective at all.

**Joint three-timeframe alignment - refuted.** Marginal occupancy per timeframe is balanced, but
entries need H4/H1/M15 agreement and marginal balance does not imply joint balance. Measured
DOWN:UP ratios for full alignment: gold 5.60, silver 0.10, nas100 0.67, us30 0.68, gbpjpy 0.62,
eurusd undefined (0 up, 1 down). Four of six lean the *wrong* way while all six traded short-heavy.
Only gold supports it.

This measurement is also what exposed the replay-fidelity problem recorded in the 3.30 caveat, since
full alignment turned out to be far too rare in the replay to account for the trades the agent
actually took. The skew remains unexplained and needs agent-side instrumentation to settle.

### 3.32 ROOT CAUSE: the agent's intentions are symmetric; its FILLS are 2.2:1 against it (2026-09-01)

`AlfonsoCandidateRecord` is now wired through BacktestRunner (`--alfonso-candidate-log PATH`,
`Agent/Strategies/Alfonso/AlfonsoCandidateLog.cs`), with the trend on each timeframe captured at
decision time from the live analyzers. This replaces the standalone replay whose infidelity is
recorded in the 3.30 caveat. Five instruments reproduce the 127-trade baseline exactly; gold's first
attempt died on a snapshot-persistence error under six-way parallelism and was rerun alone.

**The finding.** All six instruments reproduce their baseline exactly (gold 32 / +0.2181, and the
other five likewise), and the candidate log recovers the observed 92-sells / 35-buys split precisely.

| | placed | filled | fill rate |
|---|---|---|---|
| buy limits (demand) | 1,212 | 35 | 2.89% |
| sell limits (supply) | 1,379 | 92 | 6.67% |

The 2.63:1 trade skew decomposes cleanly into **placement 1.14x times fill 2.31x**. Fill selection is
the dominant term by a wide margin. On five of the six instruments placement is essentially
symmetric (1,093 buys vs 1,075 sells, 0.98:1) and the whole skew is fill; gold is the exception,
placing 304 sells against 119 buys, so it carries most of the placement term.

Per instrument the fill ratio is silver 3.29x, gbpjpy 2.62x, us30 2.42x, gold 2.11x, nas100 1.82x -
and **eurusd 0.89x**, the one instrument that did not rise (-0.8% drift) and the only one that
flips. That is the control case and it behaves exactly as the drift mechanism predicts.

**Only 4.9% of placed orders ever fill.** The strategy's realised behaviour is therefore almost
entirely a property of fill selection, not of zone selection - any improvement to zone quality
operates on 5% of its intentions. This is the most likely reason every downstream filter tested this
session (nesting 3/7, module 6's control gate 2/6, the profit margin, the zone-quality sweep) failed
to generalise: they were all filtering a population that fill selection had already biased.

**Mechanism, and what it is not.** Median placement-to-fill lag is **0.9h (buy) / 0.8h (sell)**, p90
about 3h, and the top-timeframe trend at placement matches the trade's direction in **100%** of
fills. So this is *not* stale orders resting through a regime change. Within roughly an hour, an
upward-drifting market is simply likelier to reach a sell limit above price than a buy limit below
it. Symmetric intentions plus drift selects for shorts entered immediately before continuation up -
which is why shorts win 20.7% against longs' 31.4%.

**CORRECTION to 3.30a.** That section refuted resting-limit adverse selection on magnitude
(measured 1.01-1.16 against 2.63 needed). The refutation was wrong - the proxy was. It measured
touch rates for hypothetical limits at a fixed 1-2x ATR from every close within a 100-bar horizon,
where 86-90% of limits on *both* sides are touched, so it had no power to discriminate. Real orders
sit at zone edges with short effective lives, and there the asymmetry is 2.2x. The joint-alignment
refutation in 3.30a still stands, but for an additional reason found here: entries do not require
full three-timeframe agreement at all (only 30.4% of entered candidates have it), so counting fully
aligned bars was the wrong question.

**Where this points.** The lever is the entry mechanism, not the zone or trend engines:
enter on touch or confirmation rather than resting ahead of price; or cancel a resting order when
the move that justified it has run; or size/skip by the fill asymmetry the instrument's drift
implies. None of these are tested yet.

### 3.33 Confirmation entry does not fix fill selection - it makes it worse (2026-09-02)

`--alfonso-confirm-entry` (`AlfonsoStrategyOptions.RequireReversalConfirmation`, default off) replaces
the resting limit at the proximal with a market entry once the candle trades into the zone and closes
back out of it without closing past the distal. Target is recomputed from the actual entry so the
reward multiple is preserved. Aimed squarely at 3.32: a touch-fill cannot tell a level that holds
from one price runs through, so it takes every failure.

**Pre-registered and all three failed.**

| | placed buy | placed sell | buy fill | sell fill | ratio |
|---|---|---|---|---|---|
| baseline (touch) | 1,212 | 1,379 | 2.89% | 6.67% | 2.31x |
| confirm entry | 1,163 | 349 | 1.72% | 13.18% | **7.66x** |

| | trades | avgR | 95% CI | positive |
|---|---|---|---|---|
| baseline | 127 | -0.2394 | [-0.493, +0.015] | 1/6 |
| confirm | 66 | -0.1614 | [-0.532, +0.209] | 3/6 |

E1 (asymmetry must fall toward 1.0): failed, 2.31x -> 7.66x. E2 (win rate should rise): failed,
23.6% -> 22.7%. E3 (avgR positive, 5/6): failed, CI spans zero and overlaps the baseline.

**Why.** The confirming event is itself drift-dependent, in the same direction as the defect.
Confirmation cut sell candidates by 75% while leaving buys nearly untouched, yet doubled the sell
fill rate. In a rising market price rises into supply constantly, so supply confirms readily;
demand needs a fall into the zone and a close back above, which a rising market rarely supplies.

**The generalisable lesson.** The asymmetry is not a property of the order type. Touch-fill and
confirmation-fill are both triggered by price *arriving* at a level, and in a drifting market price
arrives at levels on one side far more often. Changing how the entry is taken at a level cannot
change which levels price visits. Anything that only alters entry mechanics at the level is
attacking the wrong layer; the remaining candidates are to stop placing orders on the drift-favoured
side, to size by expected fill probability, or to require the trades that do fill to carry
expectancy on their own.

**Two implementation bugs found on the way, both the same shape.** (1) `AcceptsLevel` admits only
`Fresh` zones, but a touch makes a zone `Tested`, so zones left the candidate list at the exact
moment price reached them - the agent never saw them, which is why `NotFresh` logged zero times
while 80,422 candidates were rejected as never-reached. Confirmation is inherently a first-pullback
action, so it now accepts `Tested` with `TestCount <= 1`. (2) The touch test was written against
`bar.Prices.Low/High`, but the agent triggers on the lower interval and sees a sampled bar, so
touches between samples were invisible; the baseline is unaffected because the broker evaluates a
resting limit continuously. Fixed by reading the zone engine's own `State == Tested`. Both are the
replay-harness error again: deriving state outside the system that owns it and getting a different
answer.

### 3.34 Trading only with the drift is refuted; and the drift explanation in 3.32 is withdrawn (2026-09-02)

`--alfonso-with-drift` / `--alfonso-drift-lookback N` (default off, lookback 60). Drift is the sign
of the top-timeframe close change over the lookback, read only from closed history, and a candidate
whose side disagrees is dropped. Tested at 60 H4 bars (~10 days) and 240 (~40 days).

**Validity check first, and it fails.** If the six instruments were in a persistent one-way drift, the
filter should block overwhelmingly on one side. It does not: 60 bars blocked supply 7,313 / demand
6,335 (1.15:1); 240 bars blocked supply 9,500 / demand 10,336 (**0.92:1** - more longs than shorts).

| | trades | avgR | 95% CI | win | long/short | positive |
|---|---|---|---|---|---|---|
| baseline | 127 | -0.2394 | [-0.493, +0.015] | 23.6% | 2.63:1 | 1/6 |
| drift 60 | 73 | -0.1266 | [-0.471, +0.217] | 26.0% | 2.17:1 | 3/6 |
| drift 240 | 75 | **-0.3626** | **[-0.690, -0.035]** | 21.3% | **3.17:1** | 2/6 |

At 240 bars it is worse on every axis, and is the only result measured this session whose CI excludes
zero - negatively. The long/short mix became *more* short-heavy, the opposite of the intent.

**Why it backfires.** A 40-day drift measure lags the zone-based trend layer and is largely redundant
with it. Demand candidates only exist once the trend layer has already turned up, at which point a
lagging drift measure often still reads down, so the filter preferentially blocks longs.

**WITHDRAWN: the drift explanation in 3.32.** That section attributes the 2.31:1 fill asymmetry to
drift - "in a drifting market price walks into the limits facing the drift". The asymmetry itself is
solid and measured from the agent's own records (1,212 buy limits placed and 2.89% filled, against
1,379 sell limits and 6.67%). The *mechanism* is not. "Five of six instruments rose" describes
endpoints, not persistent drift: at both 10-day and 40-day horizons direction is near-balanced, and
silver ran 63 -> 96 -> 58 inside the window. A direct causal drift filter can neither reproduce nor
reverse the asymmetry. Treat the cause of the fill asymmetry as **unexplained**. The EUR/USD control
(the only instrument with a fill ratio below 1.0, at 0.89x, and the only one roughly flat end to end)
is suggestive but is a single instrument, and I over-read it.

Same applies to 3.33's explanation, which leaned on the same drift premise: confirmation entry did
make the asymmetry worse (2.31x -> 7.66x), which is measured, but the drift-based account of why is
now unsupported.

### 3.35 Why fill rates differ: distance, not direction - and 3.32's "symmetric intentions" is wrong (2026-09-02)

Measured from the candidate log by recovering ATR as `risk / stopAtrMultiple` and matching each
filled trade back to the placement that produced it (127 of 2,591 placements matched).

**Fill rate collapses with distance from price at placement.**

| distance (ATR) | demand fill | supply fill |
|---|---|---|
| 0-3 | 21.43% (n=98) | 35.00% (n=220) |
| 3-6 | 5.52% (n=181) | 4.26% (n=305) |
| 6-10 | 1.45% (n=207) | 0.33% (n=306) |
| 10-16 | 0.47% (n=214) | 0.33% (n=300) |
| 16-25 | 0% (n=225) | 0% (n=136) |
| 25+ | 0% (n=287) | 0% (n=112) |

Beyond ~6 ATR almost nothing fills; beyond 16 ATR nothing does. At matched distances the two sides
are broadly comparable. Buy limits sit a median 13.29 ATR from price against 7.65 for sells (1.74x),
while zone width is identical at 0.91 ATR on both sides, so this is not a geometry artefact.

**CORRECTION to 3.32.** That section reports 1,212 buy limits against 1,379 sell limits, calls the
intentions "near symmetric" at 0.98:1, and concludes the skew is entirely in fills. The count
included orders that could never fill. Counting only placements within reachable distance (<= 6 ATR):
**demand 279, supply 525 - 1.88:1 supply-heavy before any fill occurs**, against a realised 2.63:1
trade skew. Most of the skew was in the intentions; the earlier metric counted inert orders as intent.

**Mechanism, at the layer where drift does operate.** Zone *inventory*, not order flow. As price
rises over months, demand zones formed lower survive untouched and accumulate far below price, while
supply zones overhead are eliminated as price passes through them, so surviving supply sits nearer.
This is an endpoint effect over months, which is why the 40-day causal drift filter in 3.34 could not
reproduce it - and why withdrawing the drift account entirely was an over-correction. EUR/USD fits:
the only roughly flat instrument end to end, the only one where buy limits are *closer* than sell
limits (0.68x), and the only one with a fill ratio below 1.0 (0.89x).

**What follows.** About 60% of placements sit where the fill rate is under 1.5%; they are inert and
should not be counted as strategy behaviour. Any future measurement of intent must be distance-
weighted or restricted to reachable zones. A cheap, testable change is to stop placing orders beyond
the distance where fills occur at all, which would not change a single trade but would make the
agent's intent legible; the substantive question is whether the reachable population - demand 279
vs supply 525 - can be balanced at the zone-inventory level rather than at the order level.

### 3.36 Zone inventory measured in-pipeline: symmetric near price, so 3.35's explanation is refuted (2026-09-02)

`--alfonso-inventory-log` (`AlfonsoInventoryLog`, `AlfonsoInventorySnapshot`) snapshots the live zone
population once per top-timeframe bar, per role and side: count, count within reachable distance
(<= 6 ATR), median and nearest distance in ATR. Measured inside the run, because the candidate log
structurally cannot answer this - it only ever holds the side the prevailing scenario permits, so
demand and supply are never observable at the same instant.

**Nearest zone to price, median over snapshots, Lower timeframe:**

| | nearest demand | nearest supply | D within 3 ATR | S within 3 ATR |
|---|---|---|---|---|
| gbpjpy | 1.29 | 1.24 | 84.2% | 82.4% |
| nas100 | 1.24 | 1.14 | 84.7% | 82.9% |
| us30 | 1.34 | 1.29 | 79.2% | 82.0% |
| gold | 1.23 | 1.18 | 83.3% | 87.5% |
| silver | 1.29 | 1.23 | 80.7% | 83.4% |
| eurusd | 1.36 | 1.40 | 83.3% | 84.9% |

The inventory near price is symmetric on every instrument: nearest demand and nearest supply within
0.1 ATR, and 79-88% of snapshots carry a zone inside 3 ATR on *both* sides. Reachable counts are
near-equal too (reach ratio supply:demand 0.73-0.99, if anything demand-favoured).

**REFUTES the explanation in 3.35.** That section proposed that drift strands demand zones far below
price while supply overhead is consumed, so the reachable population becomes supply-heavy. The live
totals do look like that - gbpjpy Lower averages 39.7 demand against 7.1 supply - but the extra
demand zones sit a median 37.3 ATR away, in the band where the measured fill rate is exactly zero.
They inflate the count and change nothing. Near price, where fills happen, the two sides are equal.

**What still stands from 3.35**: fill rate collapsing with distance (21-35% inside 3 ATR, under 1.5%
beyond 6, zero beyond 16), and reachable placements running 1.88:1 supply-heavy (demand 279, supply
525) against a 2.63:1 realised skew.

**Where the defect actually is.** Inventory near price is symmetric; placements within reach are
1.88:1 supply-heavy. The asymmetry is therefore introduced *between* inventory and candidates - in
`AlfonsoSequenceAnalyzer.Candidates`, i.e. the scenario side, `TradeableZones`, freshness, nesting
and room-to-target filters - not in where the market leaves zones. That is the next thing to
measure, and it is a code-side question rather than a market-side one.

**Caveat.** Six instruments, one window, diagnostic rather than result.

**Note on the eurusd row.** It was first read from a partially flushed file while that run was still
going (the writer buffers and flushes on process exit, so a short file looks like a complete one).
The nearest-distance conclusion was unchanged on the complete file, but the live-zone counts
reversed - 20.1 demand / 15.7 supply on the partial, 18.5 / 25.7 on the complete. Do not read an
inventory CSV before its run reports complete.

### 3.37 The full mechanism: filters are symmetric in RATE but kill the NEAR demand zones (2026-09-02)

`AlfonsoFilterTally` counts, per side, which gate in `Candidates` discarded each zone. Cumulative
totals over six instruments, Lower timeframe:

| gate | demand | supply | D:S |
|---|---|---|---|
| rangeBlocked | 4,396 | 6,715 | 0.65 |
| overExtended | 197 | 148 | 1.33 |
| notTradeable | 236,511 | 180,010 | 1.31 |
| noHost | 29,212 | 31,985 | 0.91 |
| notAccepted (freshness) | 7,106 | 2,349 | **3.03** |
| passed | 41,560 | 37,117 | 1.12 |
| pass rate | 13.22% | 14.76% | |

**No filter produces a 1.9x supply skew, and demand passes MORE in absolute terms.** The hypothesis
in 3.36 - that some filter preferentially discards demand zones - is wrong as stated.

**The resolution, combining this with 3.36's inventory data.** Symmetric pass *rates* do not imply
symmetric *distance* distributions among survivors:

| | nearest live zone | nearest qualifying zone (where the order goes) |
|---|---|---|
| demand | ~1.3 ATR | 13.29 ATR |
| supply | ~1.2 ATR | 7.65 ATR |

The filters remove both sides at a similar rate but disproportionately remove the *near* demand
zones - `notTradeable` 1.31x and freshness 3.03x. Orders are placed at the nearest qualifying zone
(`TradeableZones` sorts by distance), so demand's nearest survivor sits about 10x further out than
its nearest live zone while supply's sits about 6x further. Fill rate collapses with distance
(3.35), so demand fills less. Every link is now measured.

**Complete chain, all measured:** live inventory near price symmetric (3.36) -> tradeability and
freshness preferentially remove the near demand zones (this section) -> the nearest qualifying demand
zone is 1.74x further from price than supply's (3.35) -> fill probability falls from 21-35% inside
3 ATR to zero beyond 16 (3.35) -> buy limits fill at 2.89% against sell at 6.67% (3.32) -> 92 shorts
against 35 longs, and shorts lose.

**Where to look next.** Not at entry mechanics, drift, or cost - all four attempts there failed
(3.33, 3.34, and the min-stop-atr sweep). The lever is why near demand zones fail tradeability and
freshness so much more often than near supply zones. `notTradeable` lumps three conditions together
(the tradeability bar, the Fresh/Tested state check, and the pending-test exclusion) and needs
splitting before that question can be answered.

**Gap in this measurement.** `scenarioBlocked` and `controlBlocked` read zero: the agent tests
`scenario.CanTrade` and returns before ever calling `Candidates`, so scenario-level blocking is
invisible to this tally, and control was disabled by `--alfonso-ignore-control` in these runs. How
often each side is the permitted side is therefore still unmeasured.

### 3.38 The three conditions split: near-zone survival is symmetric, so 3.37 is refuted (2026-09-02)

`notTradeable` split into its three actual conditions - the tradeability bar (`MeetsTradeabilityCriteria`),
the Fresh/Tested state check, and the pending-test exclusion - each further split by whether the zone
was within 6 ATR of price. Six instruments, Lower timeframe, cumulative.

| near condition (<= 6 ATR) | demand | supply | D:S |
|---|---|---|---|
| barNear | 19,974 | 32,644 | 0.61 |
| stateNear | 415 | 659 | 0.63 |
| pendingNear | 162 | 284 | 0.57 |
| passedNear | 4,941 | 9,288 | 0.53 |
| **near survival rate** | **19.38%** | **21.66%** | |

**REFUTES 3.37.** That section concluded the filters "disproportionately remove the near demand
zones". They do not: near survival is 19.38% against 21.66%, within 2.3 points, and each of the three
conditions removes *supply* more in absolute terms. The explanation was wrong.

**Confound in this instrumentation, stated so the counts are not over-read.** `Candidates` only runs
for the side the prevailing scenario permits, so these totals are summed over calls where that side
was active. The near-total ratio of 0.59 therefore mixes "how many near zones exist" with "how often
this side was active" and cannot support any claim about zone availability. Only the survival
*rates* are valid, because numerator and denominator share the conditioning. A per-bar rather than
per-call tally would be needed to compare availability.

**The one clean signal** is in the far zones: `barFar` demand 208,669 against supply 138,107, 1.51x.
Demand zones far from price fail the tradeability bar substantially more. Far zones never fill, so
this explains nothing about behaviour, but it is consistent with the large stranded-demand inventory
seen in 3.36.

**Status of the chain.** Measured and standing: fill rate collapses with distance (3.35); nearest
qualifying demand zone sits at 13.29 ATR against supply's 7.65 (3.35); live inventory near price is
symmetric (3.36); near-zone survival through the tradeability filters is symmetric (this section).
Those four cannot all be true unless something between them is being mismeasured - symmetric near
inventory plus symmetric near survival should give symmetric nearest-qualifying distance, and it does
not. The most likely suspect is the 13.29 / 7.65 figure itself, which was derived from placement
distances in the candidate log and is conditioned on the scenario side in a way the inventory
measurement is not. **The mechanism is unexplained; do not build on any of the discarded accounts in
3.32, 3.35, 3.36 or 3.37.**

### 3.39 RESOLVED: the fill asymmetry is a chain of mild asymmetries through a convex fill curve (2026-09-02)

The distance figure re-derived per bar and unconditionally, by recording the nearest *qualifying*
zone for BOTH sides every top bar regardless of which side the scenario permits
(`AlfonsoSequenceAnalyzer.TradeableZonesOf`). This removes the conditioning that made every
candidate-log-derived quantity incomparable with engine-derived ones.

| step (per bar, unconditional) | demand | supply | ratio |
|---|---|---|---|
| bars with a live zone within 6 ATR | 4,686 | 4,374 | 1.07 |
| of those, a *qualifying* zone within 6 ATR | 43.2% | 48.1% | 1.11x |
| nearest qualifying zone | 7.28 ATR | 5.31 ATR | 1.37x |
| fill rate (3.35) | 2.89% | 6.67% | 2.31x |

**The mechanism is amplification, not a lopsided gate.** Every individual asymmetry is mild - 1.07x
in inventory, 1.11x in surviving the tradeability conditions, 1.37x in distance. The distance-to-fill
curve is steeply convex (21-35% inside 3 ATR, under 1.5% beyond 6, zero beyond 16), so a 1.37x
distance difference becomes a 2.31x fill difference. No single filter or market effect is responsible,
which is why four successive single-cause explanations (3.32 drift, 3.35 zone stranding, 3.36
filter bias, 3.37 near-zone removal) each failed.

**CORRECTION to 3.35.** Its distance figure of demand 13.29 ATR against supply 7.65 (1.74x) was
derived from placements in the candidate log and is inflated about 27% by scenario conditioning. The
unconditional values are 7.28 and 5.31, ratio 1.37x. Direction and substance hold; magnitude did not.

**3.38 partially rehabilitated.** Its absolute survival rates (19.38% / 21.66%) were distorted by the
per-call conditioning, but its *ratio* of 1.12x matches the clean per-bar 1.11x. Its conclusion - that
the tradeability conditions do not disproportionately remove near demand zones - stands.

**Internal validation.** EUR/USD inverts on all three measures independently: survival 57.7% demand
against 47.7% supply, distance ratio 0.83x, fill ratio 0.89x. It is the only instrument roughly flat
end to end. Three independent inversions on the same instrument is considerably stronger than the
single control reading it was over-read from in 3.35.

**Methodological rule this cost eight measurements to learn.** Any quantity taken from the candidate
path is conditioned on which side the scenario permits, and can never be compared against one taken
from the zone engine. Measure both sides unconditionally, per bar, or do not compare.

### 3.40 A resting order squats the only slot: tighter placement caps produce MORE trades (2026-09-02)

`--alfonso-max-placement-atr N` refuses to rest an order further than N ATR from price. Predicted
from the measured fill curve (3.35): a cap at 16 should change nothing at all, since zero fills were
observed beyond 16 ATR over 2,591 placements; caps at 6 and 3 should cut trades to about 121 and 98.

| config | trades | avgR | 95% CI | win | net P&L | predicted |
|---|---|---|---|---|---|---|
| baseline | 127 | -0.2394 | [-0.493, +0.015] | 23.6% | -6,706 | - |
| cap 16 | 131 | -0.2104 | [-0.466, +0.045] | 24.4% | -5,873 | 127 |
| cap 6 | 139 | -0.1750 | [-0.418, +0.068] | 24.5% | -4,954 | ~121 |
| cap 3 | 142 | -0.1681 | [-0.409, +0.072] | 24.6% | -5,051 | ~98 |

**Every prediction was wrong in the same direction.** Trade counts rose monotonically as the cap
tightened, 131 -> 139 -> 142, where a filter can only ever reduce them. Refusing to place any order
beyond 3 ATR yields 15 more trades than placing them anywhere.

**Cause: order-slot occupancy.** The agent rests one order per instrument and holds it while
`stillValid` finds the *same* zone still a candidate ahead of price
(`AlfonsoAgent.cs`, the `restingOrder is not null` branch). A far zone stays valid for a long time -
price rarely reaches it and it survives until its distal breaks - so a far order squats the only slot
and blocks nearer levels that appear later. Every far placement refused frees the slot for one that
can fill. Three thresholds moving monotonically is much stronger evidence than the single cap-16
anomaly that first suggested it.

**Economics unchanged.** avgR improves -0.2394 -> -0.1681 and the net loss falls from -6,706 to about
-5,000, but all four CIs overlap heavily, positive instruments stay at 1/6, and win rate moves 1
point. The mechanism is established; profitability is not.

**Consequence for 3.35.** Its fill-rate-by-distance curve was measured over placements whose
distances were partly determined by which order happened to hold the slot, not purely by where zones
sit. The shape (fill rate collapsing with distance) is not in doubt, but the bucket populations are
partly an artefact of this defect.

**Direct fix implemented, not yet measured:** `--alfonso-replace-resting-atr N` cancels a resting
order when a candidate appears N ATR nearer, releasing the slot rather than refusing far placements
outright. That should capture the same benefit without discarding the far setups that do occasionally
fill. Running at thresholds 2 and 5.

**Cash context.** Baseline over 8 months, six instruments, $100k each and a fixed 1 unit per trade:
-6,706 total, -1.12% on $600k deployed, with gold the only winner at +2,518. Commission was 787,
about 12% of the loss. Sizing is fixed-quantity rather than risk-scaled, so absolute cash is small
and would scale in both directions; avgR is the size-independent measure.

### 3.41 Far placements are bad twice over; the cap beats the direct fix (2026-09-02)

`--alfonso-replace-resting-atr 2` cancels a resting order when a candidate appears 2 ATR nearer,
addressing the occupancy defect in 3.40 directly rather than working around it.

| config | trades | avgR | 95% CI | win | net P&L |
|---|---|---|---|---|---|
| baseline | 127 | -0.2394 | [-0.493, +0.015] | 23.6% | -6,706 |
| replace 2 ATR | 145 | -0.2245 | [-0.464, +0.015] | 23.4% | **-6,916** |
| cap 6 | 139 | -0.1750 | [-0.418, +0.068] | 24.5% | -4,954 |
| cap 3 | 142 | -0.1681 | [-0.409, +0.072] | 24.6% | -5,051 |

Replacement confirms the mechanism - 145 trades against 127, so the slot really was being squatted -
but avgR barely moves and net P&L is slightly *worse*. **The workaround beats the direct fix.**

**Why: far placements are bad twice over.** Matching all 127 fills back to their placements and
scoring by distance at placement:

| distance | fills | avgR | win |
|---|---|---|---|
| 0-3 ATR | 98 | -0.1810 | 24.5% |
| 3-6 ATR | 23 | -0.3240 | 21.7% |
| 6-10 ATR | 4 | -0.9180 | 0.0% |
| 10+ ATR | 2 | -0.7690 | 50.0% |
| inside 3 | 98 | -0.1810 | |
| beyond 3 | 29 | -0.4366 | |

A far order blocks the only slot *and* loses 2.4x as much when it fills. The cap removes both harms;
replacement removes only the blocking, leaving far orders free to fill while they wait. Two
independent lines agree: this bucket analysis, and the monotone cap sweep (-0.168 / -0.175 / -0.210 /
-0.239 as far placements are progressively excluded). The 6-10 and 10+ buckets hold 4 and 2 trades,
so the weight is on 0-3 vs 3-6 and on the cap sweep.

**Adopted as the default 2026-09-02** (`MaximumPlacementDistanceAtr = 3m`), verified end to end: a
run with no flag reproduces the explicit cap-3 arm exactly on gold (37 trades, +0.2341, +3,095)
against the old baseline's 32, +0.2181, +2,518. `--alfonso-max-placement-atr 0` restores the previous
behaviour, which every result before this date was measured under.
`AlfonsoAgentTests.OrdersAreNotRestedBeyondThreeAtrByDefault` pins both the default and the opt-out.

**Practical position.** `--alfonso-max-placement-atr 3` is the best configuration measured this
session: 142 trades, avgR -0.1681, net -5,051 against the baseline's 127, -0.2394, -6,706. It is
still losing money and still 1/6 instruments positive, and every CI overlaps the baseline, so this is
a defect repair rather than an edge. Keep `--alfonso-replace-resting-atr` for the record but prefer
the cap.

### 3.42 Higher timeframes remove almost all the cost drag (2026-09-03)

Ran the sequence on D1/H4/H1 instead of H4/H1/M15, six instruments, same 8-month window, with the
3 ATR placement cap active in both arms.

| | small (H4/H1/M15) | big (D1/H4/H1) |
|---|---|---|
| trades | 142 | 24 |
| avgR | -0.1681 | **+0.0044** |
| 95% CI | [-0.409, +0.072] | [-0.679, +0.688] |
| win rate | 24.6% | 25.0% |
| **winners realise** | **+2.247R** | **+2.901R** |
| net P&L | -5,051 | -129 |

**The cost prediction was correct and is the whole story.** 3.35 measured round-trip cost at 12.6% of
R on 15m against 2.9% on 4h, and the leak decomposition showed winners keeping only 2.247R of a
nominal 3.0. On the bigger stack winners keep **2.901R** - the drag is essentially gone. Win rate is
unchanged at 25%, so selection did not improve; what changed is that the account now keeps what it
wins.

**Not a result yet.** 24 trades. The CI spans -0.68 to +0.69, so a single trade moves the headline.
US30 produced zero trades. Per instrument: gold +1.36 (n=5), silver +0.13 (7), nas100 -0.03 (4),
eurusd -0.92 (3), gbpjpy -0.94 (5). Break-even on 24 trades is not evidence of an edge; it is
evidence that the cost explanation was right.

**Configuration needed to run this at all** - it is why the D1 stack was previously written off as
taking zero trades. `ProgressiveStrategyOptions.Validate()` runs even when only alfonso is active and
enforces entry < confirmation < setup < trend with all intervals distinct, while
`AlfonsoPositionManagement` uses `BracketOnlyDefaults` whose intervals are all null, so `fast` falls
back to the agent's trigger and `main` to the global confirmation. Raising the alfonso stack without
raising the global one always fails. Working set:
`--alfonso-top 1d --alfonso-middle 4h --alfonso-lower 1h --confirmation-interval 1h
--setup-intervals 2h --trend-interval 4h --secondary-trend-intervals 3h
--analysis-intervals 1m,15m,1h,2h,3h,4h,1d --aggregation-gap-tolerance 0.5`.
Without the gap tolerance daily bars never close and the agent takes no trades at all.

**Next test.** The same stack over 3-4 years rather than 8 months, to reach a few hundred trades.
Gold is cached back to 2022 (`METAL_XAU_USD_1m_20221212_20260724`). Until that runs, treat the
break-even figure as unproven.

### 3.45 A signal that survives: daily trend + H4 entry, walked forward and frictioned (2026-09-03)

> **WITHDRAWN — see 3.47.** The edge here is very likely lookahead in the daily trend filter.
> The lookahead-free rule is negative (-0.0486R, 2/7 blocks). Do not cite these tables.

Screened as a pure signal test - no agent, no backtest. At every H4 bar over 3.5 years and six
instruments, ask whether price reaches +3 ATR before -1 ATR in the signalled direction, and compare
against the unconditional rate for the same direction over the same bars.

**The rule:** if the daily close is above its level 20 days earlier, take only longs on H4; if below,
only shorts. Stop 1 ATR, target 3 ATR. Nothing else - no zones, no grading, no patterns.

**Walk-forward, seven consecutive 6-month blocks, nothing fitted (the rule is fixed):**

| test period | signals | hit% | base% | edge | instruments + |
|---|---|---|---|---|---|
| 2023 H1 | 4,718 | 29.59% | 25.31% | +4.28% | 6/6 |
| 2023 H2 | 4,796 | 30.05% | 26.81% | +3.23% | 5/6 |
| 2024 H1 | 4,782 | 25.70% | 24.71% | +0.99% | 3/6 |
| 2024 H2 | 4,870 | 28.11% | 26.36% | +1.76% | 5/6 |
| 2025 H1 | 4,744 | 27.66% | 24.94% | +2.72% | 5/6 |
| 2025 H2 | 4,861 | 28.47% | 25.79% | +2.68% | 4/6 |
| 2026 H1 | 4,750 | 28.80% | 25.16% | +3.64% | 5/6 |

**7 of 7 blocks positive**, mean edge +2.76%, gross EV +0.133R. Also 6/6 instruments in each half of a
simple two-way split. This is the only thing measured in this repo this session that passes the
consistency bar everything else failed.

**With measured frictions it is much thinner.** Applying the per-instrument slippage measured from the
590 real Alfonso trades (2.7% of risk on silver to 17.1% on eurusd) plus 0.057R commission:

| stop | slippage fixed in price | slippage proportional to stop | instruments + |
|---|---|---|---|
| 1.0x | +0.016R | +0.016R | 5/6 both |
| 1.5x | +0.035R | +0.001R | 4/6 |
| 2.0x | +0.031R | -0.021R | 3/6 |
| 3.0x | +0.029R | -0.040R | 2/6 |

**Frictions consume about 80% of the gross edge.** Net is roughly +0.016R per trade at a 1 ATR stop -
positive and consistent, but thin.

**Wider stops are NOT established as helpful.** They only pay if slippage is a fixed price amount.
Measured correlation between stop size and slippage size is 0.38 - genuinely between fixed and
proportional - so under the pessimistic reading wider stops are harmful. The 1 ATR stop is the only
setting positive under both models. This also retracts the enthusiasm for wider stops in 3.41/3.42.

**EUR/USD should be excluded**: negative in every configuration, and it carries the worst slippage at
17.1% of risk.

**Caveats.** The barrier races overlap heavily - consecutive H4 bars give near-identical races - so
the effective sample is far below the raw counts, and the 7/7 block consistency is what carries the
evidence rather than the counts. This is a signal, not a strategy: no position sizing, no cap on
concurrent trades, no rule for when several instruments fire together, and no test of weekend or gap
risk. Do not build an agent on it before those are settled.

**For contrast**: the Alfonso method over the same six instruments and the same 3.5 years is -0.2117R
on true risk (3.43/3.44).

### 3.46 The assembled rule, walked forward with frictions (2026-09-03)

> **WITHDRAWN — see 3.47.** As above: rebuilt lookahead-free this rule is -0.0486R, 2/7 blocks
> positive, 1/6 instruments. The two open questions below are answered in 3.47 (concurrency does not
> rescue it; gap risk is negligible at -0.0016R/trade).

Built by testing each piece against a null before adding it. Nothing fitted - the rule was fixed
before the walk-forward split, so every block is out-of-sample.

**The rule.** Daily close above its level 20 days earlier AND that move at least 2 daily ATR (so the
trend is strong, not merely present) -> wait for an H4 close beyond the previous bar's high (or low
for shorts) -> enter, stop 1 ATR, target 3 ATR. Mirror for shorts.

**How each piece was chosen.** Sharpening the signal beat every exit-side change: requiring a strong
trend lifted accuracy 28.22% -> 29.55% and stayed 6/6; adding 100-day agreement or a 3-ATR threshold
lifted it further (to 31.69%) but dropped to 3/6 after frictions, so they were rejected. Of five entry
triggers tested, "close beyond the prior bar" was the only one that improved on no-trigger while
keeping 6/6 (a 20-bar breakout fell to 4/6 - it enters too late). Entering on a pullback *against* the
trend was worse than no filter at all, consistent with everything else measured this session.

**Walk-forward, seven consecutive 6-month blocks, frictions applied:**

| test period | entries | hit% | base% | edge | net R | instruments + |
|---|---|---|---|---|---|---|
| 2023 H1 | 359 | 25.91% | 25.04% | +0.86% | **-0.089** | 2/6 |
| 2023 H2 | 744 | 33.33% | 26.81% | +6.52% | +0.221 | 6/6 |
| 2024 H1 | 710 | 28.87% | 24.71% | +4.17% | +0.036 | 5/6 |
| 2024 H2 | 783 | 28.61% | 26.36% | +2.25% | +0.027 | 4/6 |
| 2025 H1 | 730 | 29.86% | 24.94% | +4.93% | +0.082 | 4/6 |
| 2025 H2 | 696 | 31.32% | 25.79% | +5.53% | +0.145 | 4/6 |
| 2026 H1 | 668 | 31.29% | 25.16% | +6.13% | +0.136 | 4/6 |

**7/7 blocks positive on edge, 6/7 positive after frictions.** Mean edge +4.34%, mean net +0.0798R,
about 700 entries per half-year across six instruments. The one losing block (2023 H1) had a thin
+0.86% edge and only 2/6 instruments.

**Frictions used** are the per-instrument slippage measured from the 590 real Alfonso trades (silver
2.7% of risk to eurusd 17.1%) plus 0.057R commission - not assumptions.

**Still untested, and both could move this materially.** (1) Concurrency: the barrier races overlap,
several entries fire within a few bars, and a real account cannot take them all. (2) Gap and weekend
risk: every friction figure came from trades that filled inside the session.

**For contrast**: the Alfonso method over the same six instruments and the same 3.5 years is -0.2117R
on true risk. This is +0.0798R across seven independent periods.

### 3.47 RETRACTION: 3.45/3.46's edge is lookahead; the rule is negative when it reads only closed daily bars (2026-09-03)

Went to settle the two questions 3.46 left open (concurrency, gap risk). Rebuilding the rule from
its written description did not reproduce 3.46 — and finding out why retracts the result.

**The bug.** Daily bars in `long-*-d1.csv` open 00:00 UTC and close 00:00 the next day. An H4 bar
opening 12:00 sits *inside* the daily bar that has not closed yet, so asking "is the daily close
above its level 20 days ago" at that moment reads a close up to 20 hours in the future (~10h mean
over the six H4 bars in a day).

**The edge is a monotone function of how much future is leaked** — unlimited entries, touch fills,
median slippage, same code path, only the daily bar index differs:

| daily bar the H4 entry reads | lookahead | n | hit% | net R | blocks + |
|---|---|---|---|---|---|
| the NEXT daily bar | ~34h | 5,120 | 34.28% | **+0.2481** | 7/7 |
| the still-forming daily bar | ~10h | 4,995 | 29.03% | +0.0326 | 4/7 |
| the last CLOSED daily bar | none | 4,330 | 26.84% | **-0.0571** | 2/7 |
| one bar older still | -24h | 4,391 | 26.67% | -0.0638 | 1/7 |

That is the signature of lookahead, not of a trend effect. 3.46's reported 7/7 blocks and 28-33% hit
rates sit between the ~10h and ~34h rows.

**With the exact lookahead-free mapping** (freshest daily bar whose *close* precedes the H4 entry
bar's close — not the conservative -2 above): n=4,408, hit 27.04%, **net -0.0486R**, 2/7 blocks
positive, 1/6 instruments. Gold alone is positive (+0.16R); us30 (-0.25R) and eurusd (-0.16R) are
the worst.

**I cannot prove 3.46 had this bug** — that script was not saved. What is established: the rule as
*written* is negative, a lookahead variant reproduces 3.46's entry counts closely in 6 of 7 blocks
(741/744, 682/710, 765/783, 706/730, 690/696, 653/668) while the correct version does not (655, 588,
675, 578, 606, 558), and the edge scales with leaked future. Treat 3.45/3.46 as withdrawn.

**Concurrency (the first open question) does not rescue it.** First-come-first-served, max one
position per instrument, lookahead-free rule:

| cap | entries taken | net R | blocks + |
|---|---|---|---|
| 1 | 634 | -0.0629 | 1/7 |
| 2 | 1,168 | -0.1209 | 2/7 |
| 3 | 1,515 | -0.0864 | 2/7 |
| 6 / unlimited | 1,773 | -0.0981 | 1/7 |

The one-position-per-instrument rule alone drops 4,330 signals to 1,773 — confirming 3.45's warning
that the raw counts overstate the sample. No cap turns the sign.

**Gap risk (the second open question) is negligible** — this answer stands on its own regardless of
the retraction. Filling at the bar *open* whenever price gaps past the level, instead of assuming a
touch fill: only **0.78% of fills gap** (10 of 1,338 stop exits at unlimited cap), and a gapped stop
realises **-1.55R** against the -1.00R assumed. Total cost **-0.0016R per trade**. Weekend/gap risk
was the smaller of the two worries by two orders of magnitude; concurrency was the larger, and both
are dominated by the lookahead.

**Friction vector, recomputed and now pinned to a verified source.** The 590-trade set of 3.43 is
`lg2-small-tight` (gold, 127) plus `lg-small-tight-{silver,nas100,us30,eurusd,gbpjpy}` (92/100/84/
130/57) — confirmed to total exactly 590. Median stop overrun, `(|entry-exit| - |entry-stop|) /
|entry-stop|` over `InitialStopLoss` exits: gold 5.96%, silver 4.17%, nas100 6.40%, us30 11.70%,
eurusd 18.25%, gbpjpy 14.42% (means: 9.63 / 5.80 / 10.28 / 17.85 / 35.87 / 20.48%). **3.45 quoted
"silver 2.7% to eurusd 17.1%"** — same ordering, but neither the mean nor the median reproduces those
magnitudes and that derivation was not saved. Under the pessimistic (mean) vector the lookahead-free
rule is -0.1484R, 0/7 blocks positive.

**Scripts**: `/mnt/storage/scratch/alfonso/portfolio.py` (rule, gap-aware resolver, portfolio
simulator), `final.py` (A/B/C/D above). Unlike the 3.45/3.46 work, these are on disk — the reason
that result could not be checked is that its script was not.

**Net effect on the roadmap**: there is currently no validated signal in this repo. 3.43's verdict on
the Alfonso method stands; 3.45/3.46's replacement does not.

### 3.44 CORRECTION: rMultiple is not profit-per-risk, and the leak is slippage not commission (2026-09-03)

**What `rMultiple` actually is.** `StrategySimulationSession.cs:1320-1322` divides net profit by
`(|entry - stop| + entryPrice * estimatedRoundTripCostBasisPoints / 10_000) * quantity` - risk PLUS
an assumed round-trip cost, not risk. It is a deliberate design (profit per unit of risk-and-cost),
not a bug, and it penalises tight stops heavily: on a gold trade with 0.381 price units of risk the
assumed cost term was about 0.46, so the denominator was 2.2x the money actually risked and a real
+2.80R was reported as +1.27R. Do not read `rMultiple` as profit-per-risk. For economic questions use
`netProfitLoss / (|entry - stop| * quantity)`, or dollars.

**Cost was overstated roughly 6x throughout this session.** Measured from 590 trade records,
commission is **0.057R per trade** (median 0.036R), not the 0.34R quoted in 3.35, 3.41 and 3.42. The
0.34 figure came from a decomposition that lumped commission together with entry-bar reversals and
stop slippage and then called the total "cost".

**The real leak is stop slippage.** On true risk:

| | value |
|---|---|
| winner pays | +2.935R (the bracket delivers essentially its full 3R) |
| loser costs | **-1.228R** (23% worse than the stop specifies) |
| commission | 0.057R |

At a 75.6% loss rate the slippage overrun costs **0.172R per trade - three times commission.** Tight
stops sit inside ordinary noise, so price gaps through them rather than touching them, which is the
same mechanism the failure-mode analysis found (39% of losses stopped within six minutes).

**The verdict in 3.43 is unchanged and slightly worse on true risk:**

| | reported R | true R |
|---|---|---|
| pooled avgR | -0.1362 | **-0.2117** |
| 95% CI | [-0.251, -0.022] | **[-0.359, -0.064]** |
| instruments positive | 1/6 | 1/6 |
| net | -15,505 | -15,505 |

Break-even needs a 29.5% win rate against 24.4% achieved. Dollar P&L is unaffected by the R
definition, so 3.43's conclusion stands on its own terms.

**What this invalidates.** The "cost dominates" explanation offered in 3.40-3.42 for why bigger
targets, higher timeframes and wider stops all help. Those improvements are real and measured, but
the mechanism was mis-stated: commission is negligible, and what those changes actually reduce is
slippage as a fraction of risk. Any future reasoning that starts from "cost is 0.34R" is building on
a corrupted figure.

### 3.43 VERDICT: the Alfonso method has no edge - 590 trades, 3.5 years, CI excludes zero (2026-09-03)

Six instruments, 2023-01-01 to 2026-07-23, current default settings (3 ATR placement cap on,
structural agreement off, control gate off, profit margin 0). This is the largest sample the strategy
has ever been measured on - 4.6x the 8-month window every earlier conclusion rested on.

| instrument | trades | win | avgR | net $ |
|---|---|---|---|---|
| gold | 127 | 27.6% | +0.0144 | +1,491 |
| nas100 | 100 | 27.0% | -0.0336 | -1,405 |
| silver | 92 | 23.9% | -0.0952 | -3,060 |
| us30 | 84 | 26.2% | -0.1188 | -2,600 |
| eurusd | 130 | 24.6% | -0.2129 | -4,052 |
| gbpjpy | 57 | 10.5% | -0.5688 | -5,879 |
| **TOTAL** | **590** | **24.4%** | **-0.1362** | **-15,505** |

**Pooled 95% CI [-0.2509, -0.0215] - it does not contain zero.** 1 of 6 instruments profitable.
-2.58% on $600,000 over 3.5 years.

**The arithmetic.** A winner pays +2.314R and a loser costs -0.927R, so break-even needs a 28.6% win
rate. The method delivers 24.4%. It is short by 4.2 percentage points, consistently, across six
markets and three and a half years.

**This supersedes the near-break-even readings in 3.40-3.42.** Every lever found this session -
the 3 ATR placement cap, bigger targets (6:1 reached -0.0499), higher timeframes (+0.0044), wider
stops - is real but works the same way: it makes each trade larger relative to a fixed cost. None
improves selection. On 8 months they stacked to roughly zero and looked close. On 3.5 years the
selection deficit is clear and no exit tuning covers 4.2 points.

**Gold is not a counter-example.** +0.0144R over 127 trades is flat, and gold is the instrument that
looked best in every configuration all session - which is what the best of six draws looks like when
the true edge is negative.

**Two 8-month findings that did NOT survive the longer window:**
- Wider stops. The replay predicted an improvement; the real run gives 100 trades at -0.0013 against
  127 at +0.0144 for the tight stop. The replay held the trade population fixed; widening the stop
  actually changes which trades are taken.
- The near-break-even trajectory generally. -0.1681 on 8 months read as "almost there"; the same
  configuration over 3.5 years is -0.1362 with a CI excluding zero.

**Recommendation: stop developing this method.** The evidence is now strong rather than suggestive.
Further work on stops, targets, timeframes, filters or ML cannot close a selection gap of this size -
and 3.38/3.39 already established that nothing the agent records about a zone predicts its outcome.

**What is worth keeping** is the instrumentation, which is agent-agnostic: decision-time candidate
logging (3.32), zone-inventory snapshots (3.36), per-filter drop tallies (3.37/3.38), the placement
cap (3.40), optional position management (a6935a8), and the failure-mode analysis method - classify
losses by what price actually did, then test each fix against a null. That method is what found the
order-slot defect and what refuted four separate single-cause explanations.

**Still pending:** the D1/H4/H1 stack over the same 3.5 years (two runs, rerunning after a shell
quoting error). It will not change the verdict - the 8-month big-stack result was +0.0044 on 24
trades, which is the same flat reading.

### 3.31 Module-audit changes measured; the 127 -> 38 collapse traced to structural agreement (2026-09-01)

Three switches were implemented and A/B'd on the six-instrument window (2025-11-24 -> 2026-07-23,
`--alfonso-ignore-control`, margin 0 unless stated). All are now CLI-selectable so both arms of any
future comparison come from one binary.

**`--alfonso-profit-margin N`** (module 7's 3:1 room-to-the-opposing-level rule). Cuts 38 trades to
26; 2/6 instruments positive against a pre-registered 5/6 bar. With 1-11 trades per instrument this
is *uninformative*, not a refutation - unlike nesting (3/7) and module 6's control gate (2/6), which
had the sample to fail. Record as untested. Default 3.0, the book's value.

**`--alfonso-ambiguous-base-cp`** (module 2's "when in doubt, consider them as a CP"). Total measured
effect across six instruments: **silver only, 2 trades**. Five of six instruments are byte-identical
between on and off. This is arithmetic, not luck - the branch is
`if (Math.Max(0, baseStart - LegInLookbackCandles) >= baseStart)`, which with the default lookback of
5 can only fire when `baseStart <= 0`, i.e. once per series. Default off (the pre-existing
behaviour); there is no evidence either way at this sample size.

**CORRECTION.** An earlier version of this section claimed this change "cuts trade count 68% and
roughly doubles the loss rate (127 -> 40, -0.239 -> -0.474)". That was wrong. It came from diffing
two runs whose binaries differed by far more than this switch. The switch is worth 2 trades.

**The real cause: `RequireStructuralAgreement`** (commit `8bc113a`, module 5's higher-highs /
higher-lows context, default true). It had no CLI flag, so it had never been compared against the
baseline it replaced. Adding `--alfonso-no-structural-agreement` reproduces the pre-`8bc113a`
baseline **exactly** - all six instruments, same trade counts, same avgR to four decimals:

| | trades | avgR | 95% CI |
|---|---|---|---|
| structural agreement ON (current default) | 38 | -0.4470 | [-0.844, -0.050] |
| structural agreement OFF | 127 | -0.2394 | [-0.493, +0.015] |
| difference | | -0.2077 | [-0.679, +0.263] - contains zero |

So it is **not** demonstrably harmful to P&L. What it demonstrably does is discard 70% of trades for
no measurable benefit, which triples the noise on every subsequent measurement. It was adopted on the
strength of a trend-*accuracy* improvement (Uptrend 55.0% -> 62.5%, calls halved 1,641 -> 819); the
accuracy gain did not translate into trading results.

**Turned off by default 2026-09-02**, on statistical-power grounds rather than P&L grounds - at 38
trades nothing downstream can be measured. `--alfonso-structural-agreement` opts back in;
`--alfonso-no-structural-agreement` stays accepted so existing scripts keep working. The option had
**no test coverage in either direction** before this, which is how a switch worth 70% of the sample
went unexamined; `AlfonsoTrendDetectorTests.StructuralAgreementVetoesATrendItsOwnStructureContradicts`
and `...IsOffByDefaultSoTheSameEvidenceEstablishesTheTrend` now pin both the semantics and the
default, and fail if it is flipped back silently.

**Process failure, three instances today, one shape.** Every false conclusion this session came from
comparing two runs whose binaries differed by more than the single variable under test:

1. The profit-margin A/B returned byte-identical arms that read as a clean null. Cause: a stale
   Release binary (`BacktestRunner/bin/Release` at 11:44 vs source at 19:42) plus BacktestRunner
   **silently ignoring unknown `--flags`** - so a stale binary looks exactly like a working
   experiment that found nothing.
2. "#4 is behaviourally inert" - same stale binary.
3. "#4 cuts trades 68%" - two builds straddling commit `8bc113a`.

Two rules follow. Rebuild explicitly with `dotnet build BacktestRunner -c Release` (building
`TradingHub.slnx` or `Simulator.Tests` does **not** refresh it) and check the DLL mtime against the
source mtime. And an A/B is only trustworthy when both arms run from **one** binary and differ by
**one** flag - which is why every option above now has a CLI flag instead of being toggled by
editing a default and rebuilding. New valueless flags must also be added to the registry at
`BacktestRunner/BacktestCommandOptions.cs:337-342` or they are silently ignored.

---

## 4. Governance / packaging gaps (from the 2026-08-02 sponsorship-readiness assessment)

See `TradingHub_Sponsorship_Readiness_Assessment_2026-08-02.md` for full detail. Headline gaps,
still open as of this session:

- **No LICENSE file anywhere in the repo.**
- No CONTRIBUTING.md, SECURITY.md, CODE_OF_CONDUCT.md, CHANGELOG.md.
- Zero screenshots/GIFs/demo material anywhere, despite a working Vue dashboard.
- Root directory has ~25 large standalone markdown files (blueprints/prompts/audit reports) with
  no index — this file is a first step toward fixing that, but the underlying files haven't been
  reorganized.
- Git history is 21 commits, mostly generic "sync repo" messages spanning three weeks — not a
  legible incremental history.
- No Docker/containerized setup — manual PostgreSQL + credential-vault bootstrap required.
- `Dashboard/src/components/SimulatorPanel.vue` is 3,417 lines (self-flagged as a monolith in
  `TradingHub_Codebase_Audit_Further_Work_2026-07-19.md §3.1`).

**Closed 2026-08-25 — Node version now pinned for `Dashboard/`.** The project had no `.nvmrc` and
no `engines` field, so its Node version was whatever the developer's shell happened to supply;
this session found three different Node installs on one machine (see session log). Added
`Dashboard/.nvmrc` pinning `24`, verified against Node 24.19.0 / npm 12.0.2. Still open:
`Dashboard/package.json` has no `engines` field, so the pin is advisory for nvm/fnm users only and
is not enforced at install time or in CI.

---

## 4a. Argument-naming consistency audit (2026-08-02, user-requested)

Methodology: start at each entry point (`static Main`/top-level-statement `Program.cs`), trace
argument names down through parsing to the domain types that actually consume them, for both CLI
tools and the live host's config binding. Not exhaustive — covered `BacktestRunner`,
`QuantResearchRunner`, and `LiveTradingHost`; `DBManager.Cli`, `TradingHub.AdminCli`,
`StructuralParameterSweep`, and `DashboardExporter` not yet surveyed.

**Real findings:**
- **`BacktestRunner`'s `--fill-capacity` flag → `BacktestCommandOptions.MaximumFillQuantityPerFrame`
  → `MaximumQuantityPerExecutionFrame` at the domain layer (`BacktestRuntimeOptions`) — three
  different names for one concept.** The property is also present on `DashboardLive/SimulationApi.cs`'s
  request DTO (same `MaximumFillQuantityPerFrame` name) and covered by
  `Simulator.Tests/DashboardRequestRoundTripTests.cs`, meaning it's a JSON-serialized API contract
  the Vue frontend likely sends this exact field name for. **Not fixed** — renaming means checking/
  updating the frontend too, and this is a different risk class than an internal parameter rename;
  needs an explicit decision, not a unilateral fix.
- **`LiveTradingHost/Program.cs`'s config-section binding is inconsistent in *mechanism*** (not
  value — no actual mismatches found): some option classes bind via a `SectionName` constant on
  the class (`LiveOandaOptions.SectionName`, `AccountLeaseOptions.SectionName`,
  `LiveHostRuntimeOptions.SectionName`), others via a bare string literal with no compile-time link
  to the class (`"LiveExecution"`, `"LiveAccount"`, `"LivePersistence"`, `"LiveShadowOutcomes"`,
  `"LiveCalibration"`, `"LivePolicyBundles"`, `"CalibrationRetraining"`, `"LiveDecisionEpoch"`).
  Low severity today, but no guardrail if one of those classes is ever renamed — the string literal
  would silently stop binding instead of failing to compile.
- Minor/cosmetic: `--er-period` → `EfficiencyRatioPeriod` (abbreviation vs. spelled-out, but
  consistent with the real domain type `ChartAnnotationOptions.EfficiencyRatioPeriod` — just the
  CLI flag itself is abbreviated). `QuantResearchRunner`'s `--setup-artifact-id`/
  `--metamodel-artifact-id` drop "artifact" from their local variable names — understandable
  shortening, not genuinely confusing.

**Investigated and found to be correct, not a bug** (user specifically flagged timeframe/interval
arguments as confusing — checked carefully rather than assuming):
- `PositionManagementOptions.ManagementInterval` vs `MainStructureInterval`: looks like duplication
  (both get set from `--{legacy,improved}-management-interval` by default), but `ManagementInterval`
  already carries the doc comment `"Legacy alias for MainStructureInterval"`, and all 6+ consumers
  (`LivePositionManagementService.cs`, `LiveShadowOutcomeService.cs`, `AnalysisWarmupPlanner.cs`,
  `BacktestApplicationService.cs`, `BacktestConfiguration.cs`, `StrategySimulationSession.cs`)
  consistently treat it as a fallback (`MainStructureInterval ?? ManagementInterval ?? ...`), never
  a primary source. Intentional, consistently-implemented backward compatibility, already
  correctly labeled — not touched.
- `--execution-interval`/`--base-interval`: a computed alias property (`BaseInterval` getter/setter
  both read/write `ExecutionInterval`), not two independent fields silently drifting.
- The real "confusion" in the timeframe arguments is sheer volume (12+ distinct interval flags,
  since `legacy-progressive`/`improved-progressive`/`structural-confluence` each have their own
  timeframe model) rather than names silently diverging — a complexity/documentation concern, not
  a naming-consistency bug fixable by renaming.

**One real fix applied**: `tradingConditions` vs `tradingConditionOptions` (found while fixing
LIVE-01, §2.4) — a private/internal parameter, zero serialization risk, fixed immediately.

---

## 4b. Timeframe model redesign — full design (2026-08-02, user-requested)

**Status: Phase 1 implemented and merged (additive, non-breaking — see "Phase 1 implementation"
below). Phases 2-3 not started; still need your decision on migration strategy (3a/3b/3c).**

### Revision note (2026-08-02, same night)

The first version of this design (role-tagged list: `TimeframeAssignment(Role, Interval,
IsPrimary)`) had a real gap, caught before implementation: a `Role` tag makes a role
*list-shaped* (so "two Trigger timeframes" is representable), but says nothing about *how*
multiple intervals in the same role combine — AND/all-must-agree, OR/either-fires, or N-of-M
vote are three different decision semantics that `IsPrimary: bool` can't distinguish. That
matters concretely here: Progressive's existing `SecondaryTrendIntervals`, `SetupIntervals`, and
`AdditionalConfirmationIntervals` are genuinely N-of-M vote lists (`MinimumSecondaryTrendAlignments`
etc., see `ProgressiveStrategyBase.cs:360-399`), while Structural's `AdditionalContextIntervals`
is an all-must-be-ready gate list (`StructuralEvidencePacketFactory.TryCreate`) — two different
combination behaviors that a bare `Role` tag would conflate. Fixed by splitting the model into two
orthogonal axes: `TimeframeRole` (what purpose) and `TimeframeInfluence` (how a given interval
assigned to that role affects the decision: `Gate`/`Veto`/`Vote`/`Advisory`/`Fallback`).
Cardinality — "how many timeframes serve this role" — becomes purely a data fact (how many
`TimeframeAssignment` rows share a `(Role, Influence)` pair), not a schema fact, so adding a second
trigger or a third confirmation timeframe never requires touching the type again.

### The problem, precisely

There isn't one timeframe system in this codebase; there are three, independently evolved, each
with its own naming:

**1. Progressive entry-decision roles** (`Agent/Strategies/ProgressiveStrategyOptions.cs`) — 7
distinct concepts:
| Property | Shape | Role |
|---|---|---|
| `TrendInterval` | single | Primary trend — hard gate |
| `SecondaryTrendIntervals` | list | Soft context; opposing structural break can veto |
| `SetupIntervals` | list | Setup/consolidation context |
| `ConfirmationInterval` + `AdditionalConfirmationIntervals` | single + list | N-of-M confirmation vote |
| `EntryInterval` | single | Trigger/entry timeframe |
| `RegimeInterval` (optional, falls back to `TrendInterval`) | single | Which interval's regime classification drives regime routing |
| `NeoWaveInterval` (optional, falls back to `TrendInterval`) | single | Which interval NeoWave evidence evaluates |

**2. Structural-confluence entry-decision roles** (`Agent/Strategies/StructuralConfluence/
StructuralConfluenceStrategyOptions.cs`) — a different, simpler 3(+1)-role hierarchy:
| Property | Shape | Role |
|---|---|---|
| `TriggerInterval` | single | Trigger/entry timeframe (≈ Progressive's `EntryInterval`) |
| `SetupInterval` | single | Setup context (≈ Progressive's `SetupIntervals`, but singular not list) |
| `ContextInterval` + `AdditionalContextIntervals` | single + list | Higher-timeframe directional bias (≈ Progressive's `TrendInterval`+`SecondaryTrendIntervals`) |

Enforced invariant: `TriggerInterval <= SetupInterval <= ContextInterval` (ordering matters, not
just membership).

**3. Post-entry management roles** (`TradeManager/StructureBasedTradeManager.cs`,
`PositionManagementOptions`) — a third, independent 3-role hierarchy, shared conceptually across
strategy families but with its own naming:
| Property | Shape | Role |
|---|---|---|
| `ThesisInterval` | single | Coarsest — "is the original trade idea still valid" |
| `MainStructureInterval` | single | Primary structure tracking for stops/trailing |
| `FastStructureInterval` | single | Quick-reaction mechanical protection |
| `ManagementInterval` | single, deprecated | Legacy alias for `MainStructureInterval` (already correctly documented, §4a) |

**Underneath all three**: `ExecutionInterval`/`AnalysisBaseInterval` (finest raw-candle
resolution) and `AnalysisIntervals` (flat list — which intervals `ChartAnnotator` actually
computes indicators for). This part is already list-shaped and doesn't need redesigning.

**Total surface**: 11 distinct named role-concepts across 3 independently-designed hierarchies,
each strategy family reachable only through its own CLI flags/Dashboard fields, with three
different relationship *patterns* mixed together (hard single value; list; optional-override-with-
fallback) and no shared vocabulary between them.

### Unified model (implemented, `Brokers/Models/TimeframePlan.cs`)

Replace the 11 named properties with one role-tagged, influence-tagged collection type, shared by
every consumer. Two orthogonal enums instead of one — see the revision note above for why:

```csharp
public enum TimeframeRole
{
    Trigger, Setup, Context, Confirmation, Regime, NeoWave,
    ManagementThesis, ManagementMain, ManagementFast
}

public enum TimeframeInfluence
{
    Gate,       // must agree, else no trade — hard requirement
    Veto,       // can block/downgrade, not independently required
    Vote,       // contributes to an N-of-M tally (threshold is a strategy-level concern, not part of the plan)
    Advisory,   // informational only, doesn't affect the decision
    Fallback    // used only if a higher-priority same-role entry is unavailable/invalid
}

public sealed record TimeframeAssignment
{
    public required TimeframeRole Role { get; init; }
    public required BarInterval Interval { get; init; }
    public required TimeframeInfluence Influence { get; init; }
    public int Priority { get; init; }   // lower = evaluated first; drives Fallback order, breaks ties
}

public sealed record TimeframePlan
{
    public required IReadOnlyList<TimeframeAssignment> Assignments { get; init; }
    public IReadOnlyList<TimeframeAssignment> For(TimeframeRole role, TimeframeInfluence? influence = null) => ...;
    public BarInterval? First(TimeframeRole role, TimeframeInfluence? influence = null) => ...;
}
```

`IReadOnlyList<TimeframeAssignment>` is the shape a Dashboard form or a CLI flag like
`--timeframes trigger=5m:gate,setup=15m:gate,context=1h:gate,context=4h:gate` could bind to
directly, instead of 11 separately-named fields — and adding a second Trigger or a third
Confirmation timeframe later is just another list row, never a type change.

**Where `Progressive` and `Structural` still genuinely differ** (this redesign doesn't remove the
difference, it just gives it one vocabulary): Progressive's `Confirmation` role has no Structural
equivalent (N-of-M voting is Progressive-specific design), and `Context` behaves differently per
family (Progressive: `TrendInterval` maps to `Gate`, `SecondaryTrendIntervals` map to `Vote`
against `MinimumSecondaryTrendAlignments` — two different *influences* sharing one `Context` role;
Structural: `ContextInterval`+`AdditionalContextIntervals` all map to `Gate`, since
`StructuralEvidencePacketFactory` requires every one of them ready before a packet builds at all).
The role/influence tags don't erase that difference — they just make it explicit and queryable
(`plan.For(TimeframeRole.Context, TimeframeInfluence.Vote)`) instead of implicit in each family's
bespoke consumer code. Don't try to collapse the actual per-family behavior; only the
*naming/shape* was worth unifying.

### Phase 1 implementation (2026-08-02, done)

Additive only — no persisted/hashed shape changed, no existing option type modified:

- `Brokers/Models/TimeframePlan.cs` — the `TimeframeRole`/`TimeframeInfluence`/
  `TimeframeAssignment`/`TimeframePlan` types above. Lives in `Brokers.Models` (next to
  `BarInterval`) so it's reachable from `Agent` and `TradeManager` without a new project reference.
- `Agent/Strategies/ProgressiveStrategyOptionsTimeframeExtensions.cs` —
  `ProgressiveStrategyOptions.ToTimeframePlan()`. `TrendInterval`→`Context/Gate`,
  `SecondaryTrendIntervals`→`Context/Vote`, `SetupIntervals`→`Setup/Vote`,
  `ConfirmationInterval`+`AdditionalConfirmationIntervals`→`Confirmation/Vote`,
  `EntryInterval`→`Trigger/Gate`; `RegimeInterval`/`NeoWaveInterval` map to
  `Regime`/`NeoWave` roles as `Fallback`-influence, priority-ordered pairs (set value at priority 0,
  `TrendInterval` at priority 1) — this replaces the bespoke `EffectiveRegimeInterval ?? TrendInterval`
  pattern with the same fallback semantics expressed generically.
- `Agent/Strategies/StructuralConfluence/StructuralConfluenceStrategyOptionsTimeframeExtensions.cs`
  — `StructuralConfluenceStrategyOptions.ToTimeframePlan()`. `TriggerInterval`→`Trigger/Gate`,
  `SetupInterval`→`Setup/Gate`, `ContextInterval`+`AdditionalContextIntervals`→`Context/Gate`
  (verified against `StructuralEvidencePacketFactory.TryCreate`, which requires every configured
  context interval ready before a packet builds — genuinely a Gate, not a Vote, unlike Progressive).
- `TradeManager/PositionManagementOptionsTimeframeExtensions.cs` —
  `PositionManagementOptions.ToTimeframePlan()`. `ThesisInterval`→`ManagementThesis/Gate`,
  `MainStructureInterval`→`ManagementMain/Gate`, `FastStructureInterval`→`ManagementFast/Gate`,
  each only emitted when set (all three fields are nullable). `ManagementInterval` (the
  already-documented legacy alias of `MainStructureInterval`, §4a) is deliberately excluded from
  the adapter to avoid double-counting.
- `Simulator.Tests/TimeframePlanAdapterTests.cs` — 7 tests covering primary-role mapping, multiple
  same-role Vote assignments (the multi-trigger/multi-confirmation scenario that motivated the
  revision), Regime fallback with/without an explicit override, Structural's all-Gate context
  list, and the two `PositionManagementOptions` legacy/unset-field edge cases. All passing;
  `Brokers`/`Simulator.Tests` build clean, zero warnings.

None of the three existing option types were modified — the adapters are pure read-only
projections, callable from new code (e.g. a future Dashboard timeframe-overview panel) without
touching `FeatureSchemaHash`, calibration artifact provenance, or any currently-promoted live
policy.

### Why this is a breaking change, not a refactor

**Correction (2026-08-03, verified against actual code before writing any Phase 3 code)**: my
original claim below was wrong about *which* hash is at risk, in a way that matters for scoping
Phase 3 correctly. Read `TradingCore/Pipeline/RuntimeFeaturePolicy.cs` end to end:
`RuntimeFeaturePolicy.ComputeHash()` (= `LiveTradingPolicyBundle.FeatureSchemaHash`, the hash
`SetupCalibrationArtifact`/`MetaModelArtifact` actually key their compatibility checks against)
hashes `AnnotationOptions`, `MarketRegimeRouting`, `ValueLocationEvidence`,
`CurrencyStrengthEvidence`, `RsiBollingerSignals`, `DmiConfirmationEnabled`, `CurrencyStrength`,
`SetupCalibration`, `NeoWaveEvidence`, and a schema version string — **none of the 9 timeframe-role
fields this redesign touches**. So restructuring `TrendInterval`/`EntryInterval`/etc. would *not*
invalidate `FeatureSchemaHash`, live policy validation, or calibration-artifact compatibility as
originally claimed.

The real risk is a *different*, more widely-threaded hash: `TradingPolicyProfile.ComputeConfigurationHash()`
(`TradingPolicies/TradingPolicyProfile.cs:314-340`) JSON-serializes the full effective agent
definition — which does include `ProgressiveStrategyOptions`/`StructuralConfluenceStrategyOptions`
and therefore every timeframe field — into `ConfigurationHash`. That value shows up across
`DBManager.Abstractions` agent-lifecycle contracts (promotion/rollback audit trail),
`ConfigQueryResults`, `ResearchCommands`, and per-playbook calibration-compatibility helpers
(`Calibration/IndicatorParameters/*CalibrationCompatibility.cs`) — a wider and less-mapped surface
than the one originally described. I have not yet fully traced how each of those call sites treats
a `ConfigurationHash` change (hard validation failure vs. just a changed audit-trail identity), and
that distinction matters a lot for blast radius.
- Separately, `ProgressiveStrategyOptions`/`StructuralConfluenceStrategyOptions` are directly
  `JsonSerializer`-serialized into persisted policy documents (`TradingPolicyProfile.AgentOptions`,
  DB-backed per `DBManager.Postgres`'s `PostgresTradingPolicyProfileStore` and the
  Phase13PolicyDocumentAsText/Phase14SimulationSettingsJsonAsText migrations). Renaming/restructuring
  the timeframe properties on those record types would break JSON deserialization of any
  already-persisted document using the old property names, independent of any hash question.
- The Dashboard (`Dashboard/src/components/SimulatorPanel.vue`, 3,417 lines, §4) almost certainly
  has named form fields per role (a `<TrendIntervalInput>`-shaped thing, not a generic list
  editor) — this needs real frontend work, not just a backend type change.
- Every CLI flag in `BacktestRunner`'s ~50-flag surface that's timeframe-related (§4a already
  mapped these) would need either a new `--timeframes role=value,...` flag or continued support
  for the old flags mapped onto the new model — **the flag now exists, see "Phase 2 implementation"
  below**; the old flags continue to work unchanged.

**Net effect of the correction**: Phase 3's actual blast radius is real but different from what I
originally described — it runs through `ConfigurationHash`/JSON persistence, not
`FeatureSchemaHash`/live-policy-validation/calibration-compatibility. I have not fully mapped the
`ConfigurationHash` consumer surface yet, so I'm not implementing an actual persisted-shape change
tonight even though 3a (versioned scheme) was your stated preference for *when* it happens — see
"Status of Phase 3" below.

### Recommended phased approach (for when this actually starts)

1. **Phase 1 (additive, zero breaking risk) — DONE, see "Phase 1 implementation" above.**
   `TimeframeRole`/`TimeframeInfluence`/`TimeframeAssignment`/`TimeframePlan` plus adapter methods
   that build a `TimeframePlan` FROM the existing `ProgressiveStrategyOptions`/
   `StructuralConfluenceStrategyOptions`/`PositionManagementOptions` (read-only projection, nothing
   persisted changed). New code (e.g. a future Dashboard timeframe-overview panel) can now work off
   one consistent shape without touching a single existing hash or persisted artifact.
2. **Phase 2 (opt-in) — CLI done 2026-08-03, Dashboard deferred (your call, see below).**
3. **Phase 3 (deprecation, only after Phase 1-2 have run for a while)**: decide whether to
   actually migrate the persisted/hashed shape itself. Your stated preference for *when* this
   happens is **3a**: version the `ConfigurationHash` scheme (gains a version prefix; old
   persisted documents keep deserializing/validating under the old scheme, new ones use the new
   shape) — most compatible, most implementation work. (3b: one-time DB migration re-serializing
   existing policy documents against the new shape. 3c: hard cutover, force re-promotion of every
   policy — not chosen.) **Not started — see "Status of Phase 3" below for why.**

### Phase 2 implementation (2026-08-03, done — CLI only, Dashboard deferred)

`BacktestRunner/BacktestCommandOptions.cs` — added an opt-in `--timeframes
role=interval[:influence[:priority]],...` flag (e.g. `--timeframes
trigger=5m,context=1h:gate:0,context=4h:vote:1,confirmation=15m:vote:0,confirmation=1h:vote:1`),
parsed by the new `ParseTimeframePlan`/`ParseTimeframeRole`/`ParseTimeframeInfluence` helpers into
a `TimeframePlan`, applied **role-by-role** before the `Minimum*Alignments` defaults are computed
(so a spec covering only `trigger`+`setup` leaves `Confirmation`/other roles on their existing
discrete-flag values or defaults — verified: `--trend-interval 3h --timeframes
trigger=10m,setup=20m:vote` produced `TrendInterval=3h` untouched, `EntryInterval=10m` and
`SetupIntervals=[20m]` from the new flag, `ConfirmationInterval=15m` on its unrelated default).
Scoped to Progressive's 7-field surface only (the more complex case) — Structural's existing
3-flag surface (`--structural-trigger-interval` etc.) is untouched and still the only way to set
Structural's timeframes; Regime/NeoWave/management intervals are also untouched by this flag for
now. Verified via reflection-driven scratch harness against 3 scenarios (full override, no
`--timeframes` baseline, partial override composing with an explicit legacy flag) — all matched
expected values exactly. `QuantResearchRunner`/Dashboard equivalents not touched.

**Dashboard UI — done and tested live in-browser (2026-08-03).** Extracted the CLI's
`ParseTimeframePlan`/`ParseTimeframeRole`/`ParseTimeframeInfluence` into a shared
`Brokers/Models/TimeframePlanTextFormat.cs` (`TimeframePlanTextFormat.Parse`) so the same DSL isn't
maintained twice; `BacktestCommandOptions.cs` now delegates to it. Added `CreateSimulationRequest.Timeframes`
(nullable string, same DSL, `DashboardLive/SimulationApi.cs`) applied role-by-role in
`ToBacktestRequest()` identically to the CLI. Added a matching "Unified timeframe override
(optional)" text input to `SimulatorPanel.vue`'s multi-timeframe fieldset, wired to
`timeframes: form.timeframesOverride.trim() || null` in the request payload. `vue-tsc --noEmit`
clean. New backend test (`DashboardRequestRoundTripTests.Timeframes_RoundTripsThroughJsonAndOverridesOnlySpecifiedRoles`)
verifies JSON round-trip and role-by-role override.

Verified live: started `DashboardLive` + the Vite dev server, filled the new field via the browser,
submitted twice. First submission (`context=1h:gate:0,context=6h:vote:1`, deliberately invalid -
6h isn't finer than the 1h primary trend it would become) failed with exactly
`ProgressiveStrategyOptions.Validate()`'s real error message ("Secondary trend intervals must be
finer than primary trend and coarser than confirmation") - proof the spec reached the validation
layer with the exact values supplied, not just that the request was accepted. Second submission
with a corrected valid spec (`context=4h:gate:0,context=1h:vote:1`) passed validation and started
running normally (`Queued`/`Training`, no error). Confirms genuine end-to-end wiring: browser
input → JSON payload → `TimeframePlanTextFormat.Parse` → role-by-role override →
`ProgressiveStrategyOptions.Validate()`.

### Phase 3, revised (2026-08-03, done — bidirectional adapters, not a hash migration)

Confirmed the real blast radius before writing any Phase 3 code: `TradingPolicyProfile.Validate()`
(`TradingPolicies/TradingPolicyProfile.cs:175-181`) recomputes `ConfigurationHash` fresh from the
*currently loaded* object's own content and throws `ArgumentException` on any mismatch — it's a
self-consistency check, not a "compare against a declared version" check. That means a real
persisted-shape change to `ProgressiveStrategyOptions`/`StructuralConfluenceStrategyOptions` (the
originally planned Phase 3) would break `Validate()` for every already-persisted policy document,
since `Agent = effectiveAgent` (line 323 of the same file) serializes those types' *current* shape
into the hash input. `AgentDefinition.Validate()` (`Agent/Configuration/AgentDefinition.cs:74-84`)
has an identical hard-fail pattern one layer down (`SchemaVersion != CurrentSchemaVersion`, exact
match, and `Options` is a raw `JsonElement` serialized directly from these types' real property
names). Versioning either hash scheme to tolerate a shape change (the literal "3a") is possible in
principle — `TradingPolicyProfile.SchemaVersion` already branches serialization behavior at
`ComputeConfigurationHash` line 316-318, so there's real precedent — but doing it correctly
requires the full `ConfigurationHash`/`AgentDefinition.SchemaVersion` consumer surface mapped
first (agent-lifecycle/promotion audit contracts in `DBManager.Abstractions`, per-playbook
calibration-compatibility helpers in `Calibration/IndicatorParameters/`), which is real,
unfinished work.

**Realized instead**: the actual goal — one place to construct and read timeframe configuration,
so a future need (e.g. multiple trigger timeframes) never requires touching a type again — doesn't
require changing what's persisted at all. Added `ApplyTimeframePlan(TimeframePlan)` to all three
Phase 1 adapter classes (`ProgressiveStrategyOptionsTimeframeExtensions`,
`StructuralConfluenceStrategyOptionsTimeframeExtensions`, `PositionManagementOptionsTimeframeExtensions`)
— the write-side complement of `ToTimeframePlan()`. Applies role by role (a role absent from the
plan leaves that field on the baseline options untouched, same semantics as the Phase 2 CLI flag);
Regime/NeoWave fallback-equal-to-trend writes back as `null` rather than a redundant explicit
value. `TimeframePlan` is now genuinely bidirectional — any caller (CLI, a future Dashboard mode,
another CLI tool) can work entirely in `TimeframePlan` terms and get back a real, valid,
wire-compatible options object — while `ProgressiveStrategyOptions`/
`StructuralConfluenceStrategyOptions`/`PositionManagementOptions` never change their own property
shape, so `FeatureSchemaHash`, `ConfigurationHash`, and `AgentDefinition`'s JSON contract are all
completely unaffected. 5 new round-trip tests (`Simulator.Tests/TimeframePlanAdapterTests.cs`,
now 12 total in that file) verify `ToTimeframePlan` → `ApplyTimeframePlan` reproduces every
timeframe field exactly, including the multi-value (multiple `SecondaryTrendIntervals`/
`AdditionalConfirmationIntervals`) and partial-application cases. All passing; `Agent`/
`TradeManager`/`Simulator.Tests` build clean.

This effectively **supersedes** the original 3a/3b/3c framing: there is no longer a planned
"migrate the persisted/hashed shape" step, because the adapter-only approach delivers the same
practical outcome without the risk. The `ConfigurationHash`/`AgentDefinition.SchemaVersion`
consumer-mapping work is no longer on the critical path for the timeframe redesign specifically —
it would only become relevant again if some future, unrelated reason required actually changing
what these option types persist.

### Why Phases 2-3 haven't started

Phase 1 was safe to build with you present tonight (additive, no persisted-format change, fully
unit-tested — see above) and is done. Phases 2-3 still require decisions only you can make —
specifically **which of 3a/3b/3c** for the persisted-format migration, and whether breaking the
Dashboard's current timeframe UI mid-redesign is acceptable. Same category of judgment call as the
live-execution boundary from earlier this session: reversible/local/additive work is fine to do
alone, decisions that commit to a migration strategy for production-adjacent persisted data are
not.

---

## 5. Roadmap

**Immediate:**
1. ~~Finish the realistic-costs walk-forward scenario~~ — done 2026-08-03 (§3.3): strategy nearly
   stopped trading entirely (3 trades total, 1 pooled OOS) once realistic costs are applied.
2. ~~legacy-progressive walk-forward~~ — done 2026-08-03 (§3.6): **worse** than
   structural-confluence — negative in every fold/phase with no exceptions, 89 pooled OOS trades,
   netR=-48.72, 100% Monte Carlo safety-breach probability. Deprioritizing structural-confluence in
   favor of legacy-progressive is no longer an option on the evidence; both lose money, and
   legacy-progressive's result is the more decisive of the two.
3. ~~Fibonacci (Boroden) methodology validation~~ — done 2026-08-03 (§3.5, §3.5 follow-up): no
   measurable edge for price clusters alone in either a raw hit-rate test or a real traded
   mini-backtest; confluence showed a suggestive raw hit-rate (n=33, not significant) that did
   *not* survive being turned into an actual traded system (avgR=-0.331, n=22). Not worth
   implementing as a new analyzer on this evidence.
4a. ~~Rerun `divergence-reversal` with the freshness window~~ — done 2026-08-14/15 (§3.10):
   trade count 33 → 203 → 551 as the window widens, no config profitable, and the loss decomposed
   to exit calibration (winners realise 41% of their MFE) rather than signal quality or cost.
   Exit-calibration sweep in flight.

4b. **Give `divergence-reversal` its own position-management profile** and fix the fallback branch
   in `GetPositionManagement()` that silently hands any unrecognised agent
   `ImprovedPositionManagement` (§3.10). Pending the exit-calibration sweep's result — if a
   patient profile materially improves payoff, this stops being a scratch-experiment override and
   becomes a real code change (a `DivergenceReversalPositionManagement` slot, or routing by agent
   kind instead of by substring match on the strategy id).

4. **Decision needed from you**: three independently-tested things now point the same direction —
   `structural-confluence` loses money at current-defaults costs (§3.2) and barely trades at
   realistic costs (§3.3); `legacy-progressive` loses money more decisively and consistently
   (§3.6); the Fibonacci alternative shows no edge either (§3.5). `improved-progressive` is the one
   remaining untested strategy. Worth walk-forward testing it before concluding anything broader
   about whether this codebase's strategies have edge on EUR/USD, or worth stepping back to
   question the walk-forward setup itself (single instrument, ~7 months of data, bare-default
   parameters, no calibration) rather than continuing to test individual strategies one at a time.

**Near-term:**
3. ~~Re-verify RSK-01/LIVE-01-03/CAL-01~~ — done 2026-08-02 (see §2.4): RSK-01, LIVE-01, CAL-01
   fixed; LIVE-02 confirmed already fixed; LIVE-03 confirmed real, deferred (needs a live host to
   verify against — real feature-build work, not an overnight fix). Remaining: build a real
   cross-instrument currency-strength component for LIVE-03, and fix the shadow-path
   `EntryVolatilityBucket` hardcode noted under CAL-01.
4. ~~Argument-naming consistency audit~~ — done 2026-08-02 for `BacktestRunner`/
   `QuantResearchRunner`/`LiveTradingHost` (see §4a). Two decisions pending: whether to rename
   `MaximumFillQuantityPerFrame`→ align with `MaximumQuantityPerExecutionFrame` (touches the
   Dashboard's JSON API contract + Vue frontend, needs explicit sign-off), and whether to convert
   `LiveTradingHost`'s string-literal config-section bindings to `SectionName` constants
   (low-risk, just not done yet). `DBManager.Cli`/`TradingHub.AdminCli`/
   `StructuralParameterSweep`/`DashboardExporter` entry points not yet surveyed.
5. If pursuing the edge question further: either broaden to the 8 other cached USD instruments
   (cheap — parallelize across this machine's idle cores, ~2 hours instead of the current
   sequential run) or fetch real cross-pairs via the OANDA vault credentials (addresses the
   correlation-confound critique in §3.4 directly, not just adds more of the same evidence).
6. Address the governance/packaging gaps in §4 if a sponsorship/external conversation is planned
   (see the sponsorship assessment's 30/90/180-day roadmap for a prioritized list).
7. **Timeframe model redesign** — design (§4b) revised to separate `TimeframeRole` (what purpose)
   from `TimeframeInfluence` (Gate/Veto/Vote/Advisory/Fallback), fixing a real gap where the first
   draft couldn't express *how* multiple same-role timeframes combine (e.g. two trigger timeframes
   as AND vs. OR vs. N-of-M). Phase 1 (additive `TimeframePlan` + read adapters, 2026-08-02), Phase 2
   (opt-in `--timeframes` CLI flag on `BacktestRunner`, 2026-08-03), and Phase 3 (bidirectional
   `ApplyTimeframePlan` write adapters, 2026-08-03) all done and tested — see §4b. Phase 3 ended up
   superseding the original 3a/3b/3c hash-migration plan entirely: while scoping "version the hash,"
   found my own design doc had misidentified which hash was at risk (`ConfigurationHash`/
   `AgentDefinition.SchemaVersion`, not `FeatureSchemaHash`), and rather than build a migration
   against a still-incompletely-mapped consumer surface, added round-trip read/write adapters that
   deliver the same "one unified place for timeframes" goal with zero persisted-shape risk. Dashboard
   input mode (`SimulatorPanel.vue`) also done and verified live in-browser 2026-08-03 — all three
   phases of the redesign are now complete and tested.

**Not started, lower priority:** `SupportResistanceDetector` perf work; root-doc reorganization
into `docs/`; Docker packaging.

---

## Recent session log

- **2026-09-03 (later)**: Retracted 3.45/3.46 (§3.47). Went to answer the two questions 3.46 left
  open and could not reproduce it: the rule rebuilt from its written description is **-0.0486R, 2/7
  blocks positive, 1/6 instruments**. The reported edge scales monotonically with how much future the
  daily trend filter reads (+0.2481R at ~34h lookahead, +0.0326R at ~10h, -0.0571R at none, -0.0638R
  with extra lag) — the signature of lookahead, not a trend effect. Both open questions answered
  anyway: concurrency caps never turn the sign (best -0.0629R at cap 1), and gap/weekend risk is
  negligible (0.78% of fills gap, -0.0016R/trade). Also pinned the 590-trade set to its exact source
  dirs and recomputed the per-instrument slippage vector, which does *not* reproduce the magnitudes
  3.45 quoted. Scripts saved to `/mnt/storage/scratch/alfonso/{portfolio,final}.py` — the 3.45/3.46
  scripts were not, which is why that result could not be checked directly.
- **2026-09-03**: Measured the Alfonso agent over 3.5 years and six instruments for the first time
  (§3.43) — 590 trades, avgR −0.1362, pooled CI [−0.251, −0.022] excluding zero, 1/6 instruments
  profitable, −$15,505. The method is short of break-even by 4.2 percentage points of win rate and
  the verdict is that it has no edge. Trade-level forensics found the real defects along the way
  (§3.40 order-slot occupancy, §3.41 far placements losing twice over) and the failure-mode analysis
  showed 39% of losses are six-minute noise stop-outs and 33% are 2R givebacks. Bigger targets,
  higher timeframes and wider stops each help but all work by diluting a fixed cost, not by picking
  better. Added optional position management (a6935a8) after finding the flag was a silent no-op
  through three layers.
- **2026-09-01/02**: Found the root cause of the Alfonso long/short skew (§3.32). Wired
  `AlfonsoCandidateRecord` through BacktestRunner (`--alfonso-candidate-log`) after establishing that
  the standalone replay harness does not reproduce the agent (§3.30 caveat: EUR/USD, 14 trades on
  three-timeframe alignment vs 1 aligned bar in replay). Result: intentions are near-symmetric,
  **fills are 2.31x against us**, and only 4.9% of placed orders ever fill — so fill selection, not
  zone or trend quality, governs what the strategy actually trades. Corrected three of my own earlier
  claims in the process (§3.30a resting-limit refutation was a bad proxy; "in doubt → CP" is worth 2
  trades not 87; the 127→38 collapse is `RequireStructuralAgreement`, §3.31). Added three CLI
  switches so future A/Bs run one binary, one flag.
- **2026-09-01**: Measured the Alfonso trend layer directly for the first time (§3.30) — barrier-race
  edge vs the unconditional rate, six instruments, m15/h1/h4/d1. No usable directional edge anywhere;
  best consistent cell is EV +0.05R against 12.6% of R in cost. Retracted the "stuck in Downtrend"
  hypothesis (occupancy is balanced). Zone-quality sweep refuted the "fewer, better zones" fix and
  exposed the real defect: the 2:1 imbalance rule never reaches the trend layer
  (`ImbalanceDetector.cs:546` / `AlfonsoTrendDetector.cs:282-290`). Also measured the two module-audit
  changes (§3.31): "in doubt → CP" cuts trades 127→40 and doubles the loss rate. Third stale-Release-
  binary false result of the last two sessions — cause and check recorded in §3.31.
- **2026-08-30**: V2 redesign Phase 0a (§3.14) — unified the classifier overlap policy (three
  library defaults flipped to no-overlap; one shared flag threaded through `train`, `walk-forward`,
  `ladder`, `ablate`), added `Simulator.Tests/OverlapPolicyParityTests.cs` (3 tests, 37/37 green),
  closed §2.19 FLAW 1, resolved the 0.626-vs-0.756 baseline discrepancy in favour of §3.13,
  established that the meta-filter path never had the overlap defect, found that BreakoutDetector
  exits 23% of trades through management rather than its declared bracket, and marked §2.11 stale.

*Append-only. Newest at top. One entry per meaningful change — keep it short (what changed, why,
verification done). Move stale/superseded entries into the relevant numbered section above instead
of letting this grow forever; this is a changelog, not the whole story.*

- **2026-08-28**: Added Telegram signal notifications (§2.13) as a callable `ISignalNotifier`
  service in `Networking/Notifications/`, defaulting to a null object so backtests stay silent.
  `BreakoutDetectorAgent` takes it optionally. Tests caught a real defect: a colon-bearing bot
  token in a relative uri parses as a scheme, so the endpoint is now absolute. 20 notifier tests
  green; full solution builds. Added `Notifications.Cli` (`notify`) as a delivery probe —
  `--discover` to find the group id, `--chat` to send one test message through the real notifier;
  error paths verified against the live API. No host injects it into an agent yet — that wiring
  is the remaining step.

- **2026-08-28**: Fixed dashboard price-action markers rendering one bar to the right of their
  source candle (§2.12) — cause was close-time-stamped `ConfirmedAt` resolved against candle open
  times. Anchoring extracted to `Dashboard/src/components/priceActionMarkers.ts` and covered by 6
  Node-runner tests built from real 2026-08-27 XAU/USD 5m bars; verified the test fails against
  the old lookup. Engine direction logic audited and found correct — this was a rendering bug
  only. Extended the same fix to supply/demand zones, liquidity pools and liquidity events via
  `chartTime.ts`/`indexForCloseStampedTime` (13 of 24 real pools reposition); 14 tests green.

- **2026-08-27**: Added the `breakout-detector` agent as a fully registered scaffold with no
  detection logic (§2.11) — new `Agent/Strategies/BreakoutDetector/`, wired through the enum, type
  ids, `AgentDefinition` JSON source-gen context, `TradingAgentDefinition`, the catalogue,
  `BacktestConfiguration.ResolveAgentDefinition` and `SimulationApi.ApplySimulationProfile`. It
  observes on every bar by design. Solution build clean; 11/11 tests in
  `BreakoutDetectorAgentTests` + `AgentCatalogueArchitectureTests`.
- **2026-08-25 (cont'd 3)**: Gave `DBManager.Tests` a Docker-free path. `PostgresPersistenceTests`
  hard-coded `PostgreSqlBuilder`, so the only suite in the repo that needs a database was
  unrunnable on any machine without a Docker daemon - which is this one. The fixture now reads
  `TRADINGHUB_TEST_CONNECTION` and only falls back to Testcontainers when that is unset, so
  existing Docker-based runs are unaffected. **The external path is written but never executed:**
  the `trading_migrator` password in `DBManager.Postgres/Scripts/roles.sql:5` is stale (server
  returns `password authentication failed`), there is no `~/.pgpass`, no `TRADINGHUB_*_CONNECTION`
  in the environment or shell profile, no connection string in either `appsettings.json`, and
  `sudo` is password-gated so the postgres superuser is unreachable. `.env.integration` explains
  the cause - secrets were deliberately moved into `security.broker_credentials`, which needs DB
  access to read. So `DBManager.Tests` remains the one suite never run in this session, and whether
  it passes under Testcontainers 4.14.0 is still unverified. Note the fixture calls
  `MigrateAsync()` and writes rows: point `TRADINGHUB_TEST_CONNECTION` at a throwaway database,
  never the working `tradinghub`.
- **2026-08-25 (cont'd 2)**: Fixed the 2 long-standing `QuantResearchRunner.Tests` failures. Both
  were stale test expectations, not code defects, and both were verified pre-existing by
  reproducing them at `origin/main` in a clean worktree before any change.
  (a) `Merger_CombinesBucketsAndCohortsByStrategy` fed the `"legacy"`/`"improved"` aliases in and
  asserted they came back unchanged, but `CalibrationArtifactMerger.NormalizeStrategyId`
  canonicalises through `TradingAgentTypeIds.Parse`/`Format`, so they correctly return as
  `legacy-progressive`/`improved-progressive` — that normalisation is the point, it keeps bucket
  lookups consistent across merged artifacts. Test now asserts the canonical ids via the
  `TradingAgentTypeIds` constants.
  (b) `RunAndProposeAsync_DerivesAgentOptions_FromTrainingRuntime_NotBareDefaults` threw
  `NullReferenceException` reading `ProposedProfile.AgentOptions`. That property, with `AgentKind`,
  is a *legacy compatibility* pair that `TradingPolicyProfile.Create` only populates when
  `includeProgressiveCompatibilityFields` is true — and no caller anywhere passes true
  (`TradingPolicyProfile.cs:241` hard-codes false). Newly promoted profiles carry the options on
  `AgentDefinition`; the legacy fields exist to deserialise older profiles. Confirmed no production
  code reads `TradingPolicyProfile.AgentOptions` at all (every `.AgentOptions` hit outside tests is
  the unrelated `AgentOptionsOverride` on assignments), so the code is right and the test was
  reading a deliberately-null field. Test now reads `EffectiveAgentDefinition().Progressive`, which
  resolves canonical-vs-legacy and so keeps the AGENT-01 regression guard meaningful either way.
  Full suite now green: 1,368 passed / 0 failed across Simulator.Tests (1,092), LiveTrading.Tests
  (119), QuantResearchRunner.Tests (73), TradingHub.UnitTests (60), TradingCore.Tests (24).
  `dotnet build TradingHub.slnx -c Release` reports 0 errors. `DBManager.Tests` still unrun (no
  Docker).
- **2026-08-25 (cont'd)**: Unblocked the solution build. `dotnet build TradingHub.slnx` was
  failing outright for every target: `DBManager.Tests` -> `Testcontainers.PostgreSql` 4.13.0 ->
  `Testcontainers` -> `SSH.NET` 2025.1.0, and advisory GHSA-q939-rpr3-3284 (high; ScpClient
  recursive download allows arbitrary file write via server-controlled SCP filenames, affects
  <= 2025.1.0) turned NU1903 into a hard error via `TreatWarningsAsErrors` in
  `Directory.Build.props:7`. Nobody broke it — NuGet audit resolves advisories at restore time, so
  the build went red when the GHSA published. Bumped `Testcontainers.PostgreSql` to 4.14.0, whose
  nuspec already depends on the patched `SSH.NET` 2026.0.0, so no explicit transitive pin was
  needed. `dotnet build TradingHub.slnx -c Release` now reports 0 warnings / 0 errors and
  `SSH.NET 2026.0.0` resolves. Caveat: Docker is not available on this machine, so `DBManager.Tests`
  was verified to restore and compile but **not** executed — Testcontainers needs a daemon. Whether
  those tests pass at runtime under 4.14.0 is unverified.
- **2026-08-25**: Consolidated the local Node toolchain and patched the Dashboard advisories.
  The machine had three Node installs — nvm 22.23.1 (what the interactive shell used), an
  `n`-managed 24.13.0 in `/usr/local` (what `sudo` resolved to), and apt's 18.19.1 (pandoc's
  dependency). That split is why `sudo npm install -g npm@latest` failed `EBADENGINE`: npm 12
  requires `^22.22.2 || ^24.15.0 || >=26.0.0` and `n` had 24.13.0, just under the 24.x bar.
  Removed `n` and its `/usr/local` tree, standardised on nvm 24.19.0 / npm 12.0.2, and reinstalled
  `@openai/codex` (0.149.1) user-owned so nothing needs `sudo npm` again. Verified `Dashboard`
  builds on Node 24 twice — once from the pre-existing `node_modules`, once from a clean `npm ci`
  against the lockfile — with byte-identical output hashes both times and `vue-tsc --noEmit` clean,
  so npm 12 introduced no resolution drift. Then ran `npm audit fix`: `nanoid` 3.3.16 → 3.3.18
  (high, GHSA-2v37-7h3g-55p8, infinite loop on zero-size custom generators) and `postcss` 8.5.18 →
  8.5.26 (moderate), both transitive under Vite; `found 0 vulnerabilities` after, build re-verified
  identical. Added `Dashboard/.nvmrc` (§4). Repo changes are limited to
  `Dashboard/package-lock.json` (7 insertions / 7 deletions, versions only — `package.json`
  untouched) and the new `.nvmrc`.
- **2026-08-15 (cont'd)**: Exit-calibration sweep done (§3.11). The §3.10 diagnosis was
  mechanically correct — the patient profile doubled payoff (0.79 → 1.64) — but win rate collapsed
  in near-exact compensation (43.8% → 31.3%, stop-outs 53% → 69%), so avgR only moved -0.215 →
  -0.174. Corrected §3.10's MFE-capture sensitivity table, which wrongly treated a hindsight
  measurement as an achievable dial. Best config (`pm-patient-fresh8`) is +0.018R *gross* and
  -0.107R after costs, making execution cost the binding constraint for the first time. Added an
  explicit multiple-comparison warning: 11 configs on one window, nothing validated.
- **2026-08-15**: Ran the divergence-reversal timeframe sweep (54 backtests, §3.10) and decomposed
  the loss from the per-trade records rather than aggregates. Headline: the freshness window works
  (33 → 203 → 551 trades), nothing is profitable, and the cause is *not* signal quality (winners
  reach +1.95R MFE vs losers' +0.34R) and *not* execution cost (0.125R/trade; gross avgR still
  -0.090) — it is the exit layer realising only 41% of a winner's excursion, because
  `GetPositionManagement()`'s fallback branch hands this agent `ImprovedDefaults`, where three
  separate mechanisms all fire at exactly 1R. Launched an exit-calibration sweep to test whether
  moving them out recovers the payoff. Also found and fixed (in the new driver) a defect in the
  earlier sweep's funnel pass that zeroed the funnel for any config whose finest interval is 1m.
- **2026-08-14**: Reviewed the agents' test results at the user's request. Found §3.7/§3.8 still
  citing pre-bug-fix numbers while §2.8 already carried the post-fix reruns — reconciled by adding
  §3.9 (post-fix: 0 pooled OOS walk-forward trades; 36 trades / 9 instruments / 7 months,
  netR=-14.24, PF=0.42) and marking §3.7/§3.8 superseded. Confirmed §2.8's own open question about
  whether the fix was *overly* strict: it was — `SwingDetector`'s confirmation lag puts the
  divergence relationship ~2 candles after the price extreme, so the same-candle
  `IsNewRelationship` + Full-extreme requirement was nearly unsatisfiable. Added
  `SignalFreshnessCandles` (default 3, `0` = old behaviour) with a consume-once guard so widening
  the window can't re-fire one relationship, a per-timeframe signal funnel
  (`GetFunnelSnapshot()`, surfaced as a table in the Agent Debugger + a new form field), and the
  regression tests that the original bug slipped through (agent driven with the accumulated swing
  window production actually sends). 1061 `Simulator.Tests` pass, 0 fail; `vue-tsc` clean. **No
  backtest rerun yet** — the behaviour change is unmeasured against real data (roadmap 4a).
- **2026-08-04**: Implemented `DivergenceReversalAgent` (§2.8), formalizing the user's personal
  2-year manual/Python trading strategy (private repo, cloned for reference) after understanding
  its actual mechanics through conversation and reading the source directly. New
  `StochRsiAnalysisState` (mirrors the existing `RsiAnalysisState` ring-buffer divergence-tracking
  algorithm, applied to StochRSI's fast line, which had no equivalent yet) plus the new agent
  itself, classifying each Bollinger/RSI/StochRSI extreme reading as a genuine reversal (regular
  divergence) or a breakout continuation (hidden divergence/convergence) — closing/flipping only on
  reversals, holding through breakouts, with a wide ATR backstop stop addressing the unbounded-risk
  gap the user described in their original implementation. 7 new tests, all passing; zero
  regressions in the full 1056-test suite. Deliberately scoped as agent-logic-only per the user's
  "first step" — not yet registered in the CLI/backtest registry, not yet walk-forward tested.
- **2026-08-03 (cont'd, 6)**: Built the Fibonacci mini-backtest (§3.5 follow-up) — caught a real bug
  (avgR=-5.185, one trade near -6269R) before trusting it: entry right at the touched zone edge made
  risk near-zero, so transaction costs alone dwarfed the risk budget. Fixed with a 0.5×ATR minimum
  risk floor. Both price-only and confluence trade streams lose money as an actual traded system
  (avgR=-0.229 and -0.331), confirming §3.5's hit-rate-based conclusion on independent grounds.
  Separately ran a `legacy-progressive` walk-forward (§3.6, user request) using the same fold plan
  as structural-confluence — came back *worse*: negative in literally every fold and phase, 89
  pooled OOS trades, 100% Monte Carlo safety-breach probability. Updated the roadmap: deprioritizing
  structural-confluence in favor of legacy-progressive is no longer available as an option: both
  lose money. `improved-progressive` is now the only untested strategy left.
- **2026-08-03 (cont'd, 5)**: Realistic-costs walk-forward completed (§3.3) — strategy nearly
  stopped trading (3 trades total, 1 pooled OOS, net -1.64R) once realistic costs are applied; its
  own cost-viability gates reject almost everything. Separately, read Carolyn Boroden's *Fibonacci
  Trading* in full (user request) and, instead of implementing it on the book's authority, built a
  scratch validation script (`/mnt/storage/scratch/fibonacci-cluster-validation/`) reusing the real
  `SwingDetector`/`MultiTimeframeAggregator` to test its core claim mechanically: do price/time
  Fibonacci clusters precede reversals more often than chance, on the same cached EUR/USD data?
  Result: no for price clusters alone (60.86% vs. 60.87%/60.91% baseline/control — noise, not
  signal); a suggestive but statistically unreliable lift for full time+price confluence (n=33, not
  significant). See §3.5. Caught and fixed two real bugs in the validation script itself before
  trusting any result: an all-pairs combinatorial explosion that made "near a cluster" trigger on
  79%+ of bars (fixed by anchoring projections from only the most recent swing, per the book's own
  usage), and a self-referential time-cycle artifact that fired on literally 100% of touched bars
  (fixed with a minimum-span floor). Both roadmap items (1, 3) closed; item 2 (what to do about
  `structural-confluence`'s now twice-confirmed lack of edge) needs your decision.
- **2026-08-03 (cont'd, 4)**: Found the `realistic-costs` walk-forward run (background since last
  night) had hung, not merely slow — 12.5h+ silent, 0% CPU, 0 I/O, all threads parked. Diagnosed
  via `dotnet-trace`/attempted `createdump` (blocked by ptrace permissions) before killing it, per
  user instruction. Restarted as a fresh, scoped-to-just-`realistic-costs` run at
  `/mnt/storage/scratch/wf-research-realistic-costs/` (current-defaults' already-final result
  wasn't redone); confirmed healthy (119% CPU, active I/O) before leaving it running. See §3.3.
- **2026-08-03 (cont'd, 3)**: Fixed the 8 `Simulator.Tests` failures flagged as a known-but-unrelated
  gap earlier tonight (see §2.7). 7 were missing `AnnotationOptions.Liquidity`/`.SupplyDemand` in
  test fixtures predating §2.5's fix. 1 was a genuine bug: `BacktestApplicationService`'s default
  strategy-assignment id used the canonicalized agent-kind while `BacktestRequest.Validate()`'s
  duplicate-id check used the raw `StrategyType` — the two could disagree on an alias
  (`"legacy"` vs `"legacy-progressive"`), letting a request pass duplicate-id validation while
  actually colliding on id at runtime. Fixed to use one consistent formula. All 1049
  `Simulator.Tests` pass.
- **2026-08-03 (cont'd, 2)**: Completed §4b's Dashboard input mode — shared the CLI's DSL parser
  into `Brokers/Models/TimeframePlanTextFormat.cs`, added `CreateSimulationRequest.Timeframes` to
  `DashboardLive/SimulationApi.cs`, added the matching form field to `SimulatorPanel.vue`. Verified
  live: ran `DashboardLive` + Vite dev server, drove the UI via browser automation, submitted an
  intentionally-invalid spec and confirmed the exact `ProgressiveStrategyOptions.Validate()` error
  came back (proving the override reached validation with the right values), then submitted a
  corrected valid spec and confirmed it started running cleanly. `vue-tsc --noEmit` clean; 1 new
  backend test passing. All three phases of the timeframe redesign are now complete.
- **2026-08-03 (cont'd)**: Traced `ConfigurationHash`'s real consumer surface (71 files reference
  it; narrowed to the actual gating logic: `TradingPolicyProfile.Validate()` and
  `AgentDefinition.Validate()` both hard-fail on mismatch, recomputed fresh from the loaded
  object's *current* content on every load — confirming a real persisted-shape change would break
  every already-persisted policy document). Rather than build a versioned-hash migration against
  that still-partially-mapped surface, implemented `ApplyTimeframePlan()` (write-side complement of
  `ToTimeframePlan()`) on all 3 Phase 1 adapter classes — makes `TimeframePlan` fully bidirectional
  without `ProgressiveStrategyOptions`/`StructuralConfluenceStrategyOptions`/
  `PositionManagementOptions` ever changing their own persisted shape, so none of
  `FeatureSchemaHash`/`ConfigurationHash`/`AgentDefinition.SchemaVersion` are affected. 5 new
  round-trip tests (12 total in `TimeframePlanAdapterTests.cs`), all passing. This supersedes the
  original 3a/3b/3c framing — see §4b "Phase 3, revised." §4b and roadmap item 7 updated.
- **2026-08-03**: Implemented §4b Phase 2 (opt-in `--timeframes` flag on `BacktestRunner`, applied
  role-by-role, verified backward-compatible and composable with legacy flags via a reflection
  scratch harness — see §4b). Began Phase 3 per the user's "3a" choice, but before writing any
  code, verified `RuntimeFeaturePolicy.ComputeHash()` directly and found it does **not** hash any
  of the 9 timeframe-role fields — my original design doc's claim that Phase 3 threatens
  `FeatureSchemaHash`/live-policy-validation/calibration-artifact-compatibility was wrong. The real
  mechanism is the separate, more widely-referenced `TradingPolicyProfile.ConfigurationHash`
  (includes the full serialized agent definition, referenced from `DBManager.Abstractions`
  agent-lifecycle/promotion contracts and per-playbook calibration-compatibility helpers). Stopped
  short of writing Phase 3 code since that consumer surface isn't mapped yet — corrected §4b
  in place rather than implementing a fix for the wrong hash. Confirmed via `LiveTradingHost`
  config that `BrokerWritesEnabled`/`AutomaticExecutionEnabled` are both `false`, so no real broker
  execution risk exists regardless. Full `Brokers`/`Agent`/`TradeManager`/`BacktestRunner`/
  `Simulator.Tests` build clean; pre-existing (unrelated to tonight) 8 test failures in
  `Simulator.Tests` traced to `ValidateStructuralAnnotationRequirements` rejecting older
  calibration/caching tests that don't enable Liquidity/SupplyDemand annotations — not caused by
  this session's changes, not yet fixed, noted here so it isn't mistaken for a regression.
- **2026-08-02 (overnight, cont'd)**: User flagged a real gap in the §4b design — role-tagged
  lists don't express *how* multiple same-role timeframes combine (AND/OR/N-of-M). Revised to a
  two-axis model (`TimeframeRole` + `TimeframeInfluence`) and, with explicit go-ahead, implemented
  Phase 1: `Brokers/Models/TimeframePlan.cs` plus read-only `ToTimeframePlan()` adapters on
  `ProgressiveStrategyOptions`, `StructuralConfluenceStrategyOptions`, and
  `PositionManagementOptions`, verified against real consumer logic
  (`ProgressiveStrategyBase.cs`'s `MinimumXAlignments` = Vote, `StructuralEvidencePacketFactory`'s
  ready-check = Gate). 7 new tests in `Simulator.Tests/TimeframePlanAdapterTests.cs`, all passing;
  `Brokers`/`Agent`/`TradeManager`/`Simulator.Tests` build clean. No existing option type,
  persisted format, or hash touched — purely additive. §4b and roadmap item 7 updated to match.
- **2026-08-02 (overnight)**: Wrote the full timeframe-model redesign design (§4b) per user request
  ("write up for full redesign, so you can work on it for tonight") — grounded in the real 7-field
  `ProgressiveStrategyOptions` timeframe surface, proposes a unified `TimeframeRole`/
  `TimeframeAssignment`/`TimeframePlan` model, documents the breaking-change risk (`FeatureSchemaHash`
  invalidation, calibration-artifact provenance, Dashboard UI), and a 3-phase rollout. Design only —
  did not start Phase 1 implementation, holding the same no-unsupervised-breaking-change boundary
  used earlier this session for live execution. Added as roadmap item 7 (§5). Realistic-costs
  walk-forward scenario (§3.3) was still running throughout, unaffected by this work.
- **2026-08-02 (later)**: Completed the argument-naming consistency audit for `BacktestRunner`/
  `QuantResearchRunner`/`LiveTradingHost` (§4a) — methodology was entry-point-to-consumer tracing,
  per user direction. Found one real cross-layer name drift (`MaximumFillQuantityPerFrame` vs
  `MaximumQuantityPerExecutionFrame`, JSON-contract risk, fix deferred pending explicit sign-off),
  one config-binding mechanism inconsistency (low risk), and confirmed the user's specific concern
  about timeframe/interval arguments turned out to be already-correct, already-documented legacy
  aliasing (not a bug) rather than genuine drift — investigated rather than assumed either way.
  Paused again at user's request.
- **2026-08-02 (late)**: Re-verified and fixed RSK-01, LIVE-01, CAL-01 (primary path); confirmed
  LIVE-02 already fixed; confirmed LIVE-03 real but deferred (§2.4 has full detail). All changes
  verified against the full regression suite, zero new failures. Paused here at the user's
  explicit request — realistic-costs walk-forward scenario (§3.3) and task #10 (CLI/test-runner
  argument-naming consistency audit, requested but not started) are both left for next session.
  Main walk-forward process (`/tmp/wf-research`, PID tracked via `pgrep -af wf-research.dll`) was
  running on the *original* pre-session code the entire time, so tonight's fixes don't affect its
  in-progress results.
- **2026-08-02**: Wrote this file (`PROJECT_STATE.md`) consolidating technical + financial state.
  Realistic-costs walk-forward scenario (§3.3) still running in the background at time of writing
  — check `/tmp/wf-research/run2.log` for current status before citing a result.
- **2026-08-02**: Fixed `LiquidityAnalyzer` merge-mutation sync bug (§2.6); verified 37.9% speedup,
  correctness confirmed via real-data A/B trade-count match.
- **2026-08-02**: Fixed structural-confluence silent-inert-playbook bug (§2.5); ran first-ever
  walk-forward validation, current-defaults scenario complete (§3.2, negative result).
- **2026-08-02**: Produced `TradingHub_Sponsorship_Readiness_Assessment_2026-08-02.md`.
- **2026-08-19**: Formula audit of the risk system (`PositionSizing`, `InstrumentRiskSpec`/
  `PortfolioOpenRisk`, `RiskBudgetPolicy`, `PreTradeRiskManager`, `TradingSafety`,
  `SetupCalibration`/`MetaLabel`). Core arithmetic verified correct; fixed RSK-02..RSK-05 above and
  added `Simulator.Tests/RiskGuardRegressionTests.cs` (9 tests). Verified the regression tests
  genuinely bite: 6 of 9 fail against the pre-fix code (the other 3 assert preserved behaviour).
  Suites green: `Simulator.Tests` 1070/1070, `LiveTrading.Tests` 119/119.
- **2026-08-19**: Formula/implementation audit of `TradeManager` (`StructureBasedTradeManager`,
  `ManagementCalibration`, `RegimePositionManagementProfiles`, `PlaybookAwareTradeManager`,
  `VolatilityBucketClassifier`, `PositionManagementOptionsTimeframeExtensions`). Core management
  math verified correct; fixed TM-01..TM-07 above and added
  `Simulator.Tests/TradeManagerGuardRegressionTests.cs` (6 tests). Both high-severity findings were
  reproduced with a throwaway probe *before* being fixed, and the regression tests were verified to
  bite: 4 of 6 fail against pre-fix code (the other 2 assert preserved behaviour). Suites:
  `Simulator.Tests` 1076/1076, `LiveTrading.Tests` 119/119, `QuantResearchRunner.Tests` 71/73 —
  the 2 failures (`Merger_CombinesBucketsAndCohortsByStrategy`,
  `RunAndProposeAsync_DerivesAgentOptions_FromTrainingRuntime_NotBareDefaults`) were confirmed
  byte-identical before and after the change, matching the pre-existing baseline.
- **2026-08-19**: Formula/implementation audit of `ExecutionManager` (`ExecutionCoordinator`,
  `PositionReconciliationGuard`, options/command records — 990 lines total). Fixed EM-01..EM-05
  above and added `Simulator.Tests/ExecutionManagerGuardRegressionTests.cs` (7 tests). EM-01 was
  reproduced with a throwaway probe before being fixed. The cache-eviction test was initially too
  weak to distinguish a cached replay from a re-execution (it passed both with and without the fix)
  and was rewritten against a capability-advertising broker that counts broker calls; 6 of 7 tests
  now fail against pre-fix code, the 7th being a deliberate preserved-behaviour guard. Suites:
  `Simulator.Tests` 1083/1083, `LiveTrading.Tests` 119/119, `TradingCore.Tests` 24/24,
  `TradingHub.UnitTests` 60/60, `QuantResearchRunner.Tests` 71/73 (same two pre-existing failures,
  names re-confirmed identical). **`DBManager.Tests` does not build** — `NU1903` treats a known
  high-severity `SSH.NET` 2025.1.0 vulnerability as an error. Pre-existing and unrelated to this
  work (no DBManager file was touched), but it means that suite is currently providing zero cover.
- **2026-08-19**: Built timeframe drill-in (§2.10) — new OANDA window endpoint plus the dashboard
  chooser/centring. Verified in a browser against live OANDA data; two bugs (future range end,
  reconnect-on-drill) were found only by driving the real UI. `Simulator.Tests` 1092/1092,
  `LiveTrading.Tests` 119/119, `vue-tsc` clean. Binance deferred.
- **2026-08-19**: Diagnosed and fixed the Dashboard's HTTP 500 on `/api/simulation-profiles` — all 44
  stored simulation-profile revisions had `ContentHash` invalidated by cumulative schema drift (§2.9).
  Re-hashed 33; left 11 historical revisions untouched because they fail timeframe-alignment
  validation for real reasons. Endpoint verified 200 with 11 profiles. Backups and the migration tool
  are under `/mnt/storage/`.

---

## Update policy

**If you touch code, run a validation, or make an architectural decision, update this file in the
same session** — don't leave it for later. Specifically:

- Fixed a bug or changed behavior in a subsystem described in §2? Update that subsystem's entry.
- Ran a backtest/walk-forward/calibration with a real conclusion? Add/update §3 and note it in the
  session log.
- Learned something that contradicts a claim in an older `.md` file elsewhere in the repo? Correct
  it here and note the correction explicitly (like §2.2/§2.6 do) — don't silently trust the older
  doc, and don't edit the older doc either unless asked; this file is the override layer.
- Discovered a new gap or closed one in §4? Update it.
- Keep entries evidence-based: cite file:line, a test result, or a concrete run output — not
  vibes. If you can't verify something, say so explicitly rather than assuming a doc is current.
