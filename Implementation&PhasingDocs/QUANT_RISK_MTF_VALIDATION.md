# Quantitative risk and multi-timeframe validation

## Implemented paths

- Opening order: Agent decision → PositionSizer → PreTradeRiskManager → ExecutionCoordinator → broker.
- Entry evidence: primary trend → secondary trend context → setup intervals → N-of-M confirmations → entry trigger.
- Open-position management: thesis → main structure → fast structure → execution-frame mechanical protection.
- Dashboard, API, CLI, replay manifest, configuration identity, and simulator runtime use the same settings.

## Quantitative invariants

- Risk-based quantity uses current equity, the original entry-to-stop distance, estimated round-trip costs, quote-to-account conversion, leverage and margin caps.
- Quantity is rounded down to the configured broker step and never rounded up beyond the risk budget.
- Missing currency conversion rejects risk-based sizing instead of assuming a rate.
- Maximum account margin and maximum one-position margin are enforced independently.
- The primary trend is a hard gate. Secondary trend is soft by default, but a confirmed opposing structural break can veto the setup.
- Strategy and management intervals must be valid, unique where required, ordered and aggregatable from the analysis base interval.
- Mechanical break-even, fixed-R scale-out, profit floors and MFE giveback are evaluated on every execution frame.
- Structural and thesis management use completed candles only; no current incomplete candle is promoted into structure analysis.
- Existing stop/target fills have priority. Thesis exit precedes main, fast and mechanical recommendations.
- Stop amendments never widen risk. Partial-exit stages are one-shot, pending reductions block duplicates, and protective quantities reconcile after fills.
- Simulation configuration identity includes the complete MTF stack, sizing policy and multi-speed management policy, preventing unlike runs from sharing an identity.

## Focused tests added

`Simulator.Tests/QuantitativeRiskAndMultiTimeframeTests.cs` covers:

- fixed-fractional sizing and quantity-step rounding;
- quote-to-account conversion and margin caps;
- converted monetary pre-trade risk;
- required MTF interval registration;
- soft secondary-trend pullback handling;
- strong secondary structural opposition veto;
- aligned role-based consensus entry;
- invalid derived management-interval rejection;
- execution-scope profit protection;
- thesis-scope non-duplication.

`Simulator.Tests/Phase4TrailingStopTests.cs` also verifies that streamed mechanical protection activates without waiting for a 5m or 15m structure close.

## Validation completed in this environment

- Vue/TypeScript typecheck: passed.
- Vite production build: passed.
- Replay fixture validation: passed for 680 frames.
- C# tree-sitter syntax parse: 228 files, zero syntax-error files.
- Duplicate C# object-initializer member scan: zero duplicates.
- Project-reference/XML integrity: 18 projects, zero broken references.

The environment does not contain the .NET 10 SDK, so the Roslyn build and NUnit execution could not be run here. Run `./validate.sh` on a machine with the .NET 10 SDK to perform restore, Release build, all .NET tests, clean npm install, Dashboard typecheck/build and replay validation.

## Deliberate architecture boundary

> Updated 2026-07-16. A `SharedPortfolioAccount` mode now exists (`SharedPortfolioRuntime`)
> with real combined portfolio heat, correlation clusters, and currency-exposure ranking
> — this section's original framing ("belong in a separate shared-account portfolio
> mode") is stale. Current, accurate limitations:

- `SharedPortfolioAccount` mode's **account ledger** (balance + margin) is real and
  netted, as of the 2026-07-16 §6 pass: one shared balance pool, and `MarginUsed`
  computed from net exposure per instrument across strategies' virtual lots rather
  than summed independently. Its **positions** remain a federation of independent
  per-strategy broker accounts, not one authoritative broker-side netted position —
  same-instrument positions across strategies still can't be reconciled to one
  position, deliberately deferred. See `ARCHITECTURE_SIMULATOR.md` § Known limitations
  for why (exit-management/stop-ownership constraints in `StrategySimulationSession`
  and `ExecutionCoordinator`).
- **No longer single-instrument per run (2026-07-16, §7).** `StreamingComparativeEngine`
  now runs a real chronological merge across one `IHistoricalCandleStream` per distinct
  traded instrument, each with its own `AnalysisBaseAggregator`/`MultiTimeframeAggregator`/
  `MarketDataQualityTracker`; strategies are assigned an instrument via
  `BacktestRequest.StrategyAssignments` (Dashboard: Simulator panel's "Multi-instrument
  portfolio clock" section) and routed frames only for their own instrument.
  `RollingCorrelationClusters`/`SharedPortfolioRuntime.EvaluateCorrelation` — already
  real and unit-tested (`CorrelationWiringTests.cs`) — can now actually receive a
  second instrument's returns in production. Proven end-to-end via
  `Simulator.Tests/MultiInstrumentPortfolioClockTests.cs` (real two-instrument routing,
  per-instrument end-of-stream liquidation, staggered warmup/calibration) and
  `ApplicationService_CompletesWithStrategyAssignmentsAcrossTwoInstruments` (the same
  proof through the real public `BacktestApplicationService`, not just the engine
  constructed directly). **Not yet proven**: a dedicated test that exact-correlated
  real streamed multi-instrument data actually trips a correlation risk penalty
  end-to-end (as opposed to the existing synthetic-decision proof in
  `CorrelationWiringTests.cs` plus the routing/wiring proof above) — the underlying
  code path is unchanged and already covered by both, so this is a real but low-risk
  gap, not an open question about correctness.
- `IndependentStrategyAccounts` mode (each strategy fully isolated, no shared budget)
  remains available and unchanged as the strategy-comparison mode.
- Dashboard chart replay filters the candle series by instrument when a run trades
  more than one, but trade markers on that chart are not yet instrument-filtered
  (`ReplayTrade` doesn't expose `Instrument` in the API yet) — a real, minor, known gap.

See `TradingHub_Quantitative_Enhancements_Final_Audit_and_Corrective_Prompt.md` for
the full audit and current status of every quantitative-risk feature.
