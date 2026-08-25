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

*Append-only. Newest at top. One entry per meaningful change — keep it short (what changed, why,
verification done). Move stale/superseded entries into the relevant numbered section above instead
of letting this grow forever; this is a changelog, not the whole story.*

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
