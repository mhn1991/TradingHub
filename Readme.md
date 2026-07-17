# TradingHub

TradingHub is a .NET 10 multi-broker market-analysis, simulation, and automated-trading project. The current implementation combines deterministic chart annotation with safety-first execution controls and a Vue dashboard.

## Project boundaries

The runtime is split by responsibility so strategy code does not own trading safety or broker execution:

- `Agent` — analyses market context and produces `AgentDecision` objects only.
- `Brokers` — broker adapters, broker account/order-state execution checks, and the pure broker/local position reconciliation engine.
- `RiskManager` — pre-trade risk policy, sizing/exposure limits, protective-order requirements, and daily/weekly/consecutive-loss safety state.
- `ExecutionManager` — idempotent order orchestration, broker-state/risk assessment coordination, order submission, execution journaling, and reconciliation safety escalation.
- `TradeManager` — post-entry break-even, structure/ATR trailing, staged scale-out, profit-floor/giveback, and deterioration/exhaustion recommendations.
- `TradingCore` — the end-to-end safe trading pipeline and market-data quality gate.
- `TradingJournal` — reusable bounded audit-journal contracts and in-memory implementation.
- `ChartAnnotator` — indicators, swings, zones, trendlines, channels, and market structure.
- `Simulator` — deterministic broker simulation, streamed comparative orchestration, job queue, and chunked replay.
- `DashboardLive` — live workspace feeds plus simulation REST/SignalR API.
- `BacktestRunner` — thin CLI adapter over the shared `IBacktestApplicationService`.

`ExecutionManager` intentionally coordinates broker safety and risk policy, but it does not define either policy. Broker-specific/account-order checks live in `Brokers.Safety`; financial risk thresholds live in `RiskManager`; and the comparison portion of reconciliation lives in `Brokers.Reconciliation`.


The current quantitative-risk upgrade adds risk-based position sizing, role-based multi-timeframe confirmation, and multi-speed position management. See [`QUANTITATIVE_RISK_AND_MTF.md`](QUANTITATIVE_RISK_AND_MTF.md) for the execution order, defaults, invariants, and the deliberate boundary between independent strategy comparison and shared portfolio allocation. A `SharedPortfolioAccount` mode now exists with real combined heat, correlation, currency-exposure risk, and a shared account ledger (balance + net-per-instrument margin). The simulator can also stream more than one instrument in a single run (§7 multi-instrument portfolio clock — strategies are assigned an instrument via the Dashboard Simulator panel or `BacktestRequest.StrategyAssignments`), so correlation and cross-instrument capital competition are no longer architecturally inert. See [`ARCHITECTURE_SIMULATOR.md`](ARCHITECTURE_SIMULATOR.md) § Quantitative risk / portfolio layer for what's real versus still limited (positions themselves remain a federation of independent broker accounts rather than one netted broker-side position — deliberately out of scope, see § Known limitations).

## Implemented Phase 1: safe trading foundation

The Phase 1 path is available through `SimulationFactory.CreatePhaseOneSafeHistorical(...)` and the reusable `SafeTradingPipeline`.

Implemented controls:

- Stateful market-data quality checks for incomplete, stale, future, duplicated, overlapping, missing, or invalid OHLC candles.
- Multi-timeframe continuity checks that support strategies evaluated less frequently than the source timeframe.
- Pre-trade risk assessment with:
  - required stop-loss and optional required take-profit;
  - minimum reward/risk ratio;
  - maximum position quantity;
  - maximum simultaneous positions;
  - absolute and account-percentage risk limits;
  - confidence threshold;
  - pyramiding and projected-position gates.
- Broker-owned execution-state validation for account permission and duplicate open orders.
- Stable client-order identifiers and concurrent duplicate-decision coalescing.
- Daily-loss, weekly-loss, and consecutive-loss safety trips.
- Manual pause/resume and emergency trip support.
- Broker/local position reconciliation in `Brokers.Reconciliation`, with mismatch safety escalation in `ExecutionManager`.
- Bounded, thread-safe audit journal for signals, rejections, orders, safety events, reconciliation events, and closed trades.
- Structure-aware trade-management recommendations:
  - break-even protection;
  - ATR-buffered swing/support/resistance trailing stops;
  - adverse market-structure-break exit recommendations;
  - stops are never widened.
- Simulation integration that feeds realised P/L back into the safety controller.

Phase 4 adds a broker-neutral protective-stop amendment contract. The simulator performs an atomic replacement that preserves OCO linkage and becomes executable only on the next execution sequence. `ExecutionManager` validates the broker position, exact existing stop, tick size, executable side, and risk reduction before mutation. OANDA advertises amendment as unsupported in this build because a native, atomic mapping has not yet been implemented; the old stop is never cancelled as an unsafe fallback.

## Implemented Phase 2: chart annotation and structure

- Confirmed swing-high and swing-low detection without lookahead.
- ATR, RSI, and Bollinger indicator snapshots.
- Derived indicator context:
  - Bollinger normalized bandwidth, historical percentile, narrowing/widening state, squeeze detection, and squeeze-release/expansion events;
  - RSI zone and momentum direction, regular and hidden bullish/bearish divergence, and same-direction bullish/bearish convergence confirmed only after price swings are known;
  - same-instrument/timeframe relative volume regime against a rolling median, preserving broker `VolumeKind` semantics (including tick activity versus traded quantity);
  - normalized ATR percentage, recent percentile, volatility regime, and contracting/expanding direction.
- DBSCAN support/resistance zones.
- Deterministic RANSAC trendlines.
- Channel detection.
- Rising, falling, sideways, and unknown market-structure classification.
- Bullish and bearish break-of-structure detection.
- Trendline/channel recalculation when market direction changes.
- Optional removal of stale lines after a structure-direction change.
- Confidence scoring that includes structure alignment, volatility context, directional price action, and adverse-break penalties. RSI relationships and Bollinger direction remain visible here but are scored for the expected trade side by the agents.
- Direction-aware agent use of RSI momentum/divergence/convergence and Bollinger `%B`/width context: aligned confluence can confirm or trigger an entry, while strong opposing divergence or expansion vetoes it. An unresolved squeeze never supplies direction by itself.
- Direction-aware support/resistance and volume confluence: a matching zone and elevated same-direction activity add weight to RSI relationships; remote zones are ignored, low participation is penalized, and opposing volume spikes can veto. Volume never supplies direction by itself.
- Full replay-window dashboard support, including an **All candles** option and market-structure display.

## Streamed dual-strategy simulator (dashboard-first)

The primary UX is **Dashboard → Simulator**:

1. Start `DashboardLive` and the Vue dashboard.
2. Open the **Simulator** tab.
3. Configure instrument/range/strategies and click **Run Simulation**.
4. Receive a `simulationId` immediately; progress, balances, and trades stream while the backend processes 1m candles as fast as correctness allows.
5. Pause/resume **compute** independently from visual playback.

Legacy and Improved have separate break-even/structure activation, management-interval, ATR-buffer, and adverse-structure settings. The live panel displays current/initial stops and locked R; completed trades retain every accepted or rejected amendment and render a stepped stop line.

Architecture details: [`ARCHITECTURE_SIMULATOR.md`](ARCHITECTURE_SIMULATOR.md).

### CLI (same application service)

```bash
export Oanda__AccountId='YOUR_PRACTICE_ACCOUNT_ID'
export Oanda__AccessToken='YOUR_PRACTICE_ACCESS_TOKEN'

dotnet run -c Release --project BacktestRunner/BacktestRunner.csproj -- \
  --instrument FX:GBP/JPY \
  --from 2025-07-13 \
  --to 2026-07-13 \
  --execution-interval 1m

cd Dashboard
npm ci
npm run dev
```

See `RUN_BACKTEST.md` for all controls, cache behaviour, and dashboard output details.

For a reproducible disabled-versus-structure/ATR comparison of both strategies, add `--trailing-comparison` to the CLI command. Results are written to `trailing-comparison.json` with P/L, drawdown, R, MFE/MAE, exit reasons, amendments, locked R, and giveback metrics.

## Safe simulation example

```csharp
await using SimulationSession session = SimulationFactory.CreatePhaseOneSafeHistorical(
    instrument,
    historicalCandles,
    [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)],
    agent,
    simulationOptions: new SimulationOptions { StartingBalance = 10_000m },
    safetyOptions: new TradingSafetyOptions
    {
        MaximumDailyLoss = 100m,
        MaximumWeeklyLoss = 300m,
        MaximumConsecutiveLosses = 3
    });

SimulationResult result = await session.Runner.RunAsync();
TradingSafetySnapshot safety = session.Safety!.Snapshot;
IReadOnlyList<TradeJournalEntry> journal = session.Journal!.Snapshot();
```

The phase-safe factory defaults to conservative entry controls, including a required stop-loss, minimum `1.5R` reward/risk, no pyramiding, a three-position cap, and a maximum planned risk of `0.5%` of account balance per entry. Risk settings are supplied through `PreTradeRiskOptions`; broker account/order-state settings are supplied through `BrokerExecutionSafetyOptions`; and execution-only behaviour is supplied through `ExecutionOptions`.

## Validation

Backend:

```bash
dotnet test Simulator.Tests/Simulator.Tests.csproj -c Release
dotnet test TradingHub.UnitTests/TradingHub.UnitTests.csproj -c Release
```

Dashboard:

```bash
cd Dashboard
npm ci
npm run build
npm run data:validate
```

Integration tests under `Brokers.IntegrationTests` are explicit and require broker test/demo credentials in the environment.


## Profit protection and position reduction

The simulator now supports staged partial exits, a protected runner, profit-floor ratchets, MFE maximum-giveback protection, stagnation reduction, structural deterioration, corroborated momentum decay, confirmed post-expansion volatility exhaustion, and optional UTC risk-window or spread/ATR stress reductions.

Every close decision is broker-neutral **reduce-only**. The simulated broker clamps stale close quantities to the actual remaining position and atomically resizes attached stop/target orders, preventing a partial close or stale full-close request from reversing the position. OANDA maps reduce-only closes to `positionFill = REDUCE_ONLY`.

The long-run trade-feed endpoint also tolerates old, null, truncated, or partially written trade indexes and advances past corrupt rows instead of returning repeated HTTP 500 responses.

See [`PROFIT_PROTECTION_AND_LONG_RUN_FIX.md`](PROFIT_PROTECTION_AND_LONG_RUN_FIX.md) and [`FINAL_PROFIT_PROTECTION_VALIDATION.md`](FINAL_PROFIT_PROTECTION_VALIDATION.md).

## Remaining production work

- Implement and certify a native atomic OANDA stop-amendment mapping before enabling live trailing there.
- Add a historical bid/ask candle source; current historical fills remain midpoint plus configured spread/slippage.
- Persist the audit journal and safety state to PostgreSQL rather than memory only.
- Run long soak tests with forced disconnects, restarts, partial fills, and position reconciliation failures.
- Portfolio-level currency/correlation exposure controls exist and gate admission in `SharedPortfolioAccount` mode, and the simulator can now stream more than one instrument in a run (§7 multi-instrument portfolio clock — see `ARCHITECTURE_SIMULATOR.md` § Quantitative risk / portfolio layer), so correlation/cross-instrument competition can actually be exercised in production. Remaining gap: no dedicated test proves an exact-correlated real streamed pair trips a correlation penalty end-to-end, and Dashboard replay trade markers aren't yet instrument-filtered (only the candle series is).
- `SharedPortfolioAccount` mode's account ledger now nets for real: one shared balance pool and margin computed from real net exposure per instrument across strategies, not summed independently (see `ARCHITECTURE_SIMULATOR.md` § Known limitations). Remaining gap is position-level: same-instrument positions across strategies still can't be reconciled to one broker-side netted position, deliberately deferred because it needs every exit-management code path in `StrategySimulationSession` retrofitted to a virtual-lot model first.
- Generate Phase 3 ML training rows from historical annotations using time-split, lookahead-safe labels.
- Wire an operational QuantResearch runner (walk-forward/ablation/sensitivity/calibration CLI) and a calibration-artifact repository — the library primitives exist but have no end-to-end workflow yet.

## Price action and warm-up calibration

The analysis pipeline now includes deterministic BOS/CHoCH, break-and-retest, structural rejection, displacement, liquidity-sweep, compression/expansion, price-leg, and ADX/DMI evidence. Profiles are maintained separately per instrument/timeframe during warm-up and frozen before evaluation. See `PRICE_ACTION_CALIBRATION_AND_DIAGNOSTICS.md` and `FINAL_PHASE2_VALIDATION.md`.

Composite same-TF setups (break-retest, sweep-displacement, sweep-CHOCH) and multi-timeframe PA entry gating for progressive agents are documented in [`PRICE_ACTION_SETUPS_AND_MTF.md`](PRICE_ACTION_SETUPS_AND_MTF.md).

## Simulator broker and asset picker

See [`BROKER_ASSET_SELECTION.md`](BROKER_ASSET_SELECTION.md) for the supported OANDA, Binance Spot, imported-dataset, and IG availability rules.
