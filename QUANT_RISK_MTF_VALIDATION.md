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

The comparative simulator still gives each strategy an isolated account. This release prevents a single order from consuming the configured risk/margin budget, but it is not yet a shared multi-instrument portfolio allocator. Combined portfolio heat, currency buckets, correlation clusters and capital ranking across simultaneous instruments belong in a separate shared-account portfolio mode.
