# TradingHub

TradingHub is a .NET 10 multi-broker market-analysis, simulation, and automated-trading project. The current implementation combines deterministic chart annotation with safety-first execution controls and a Vue dashboard.

## Project boundaries

The runtime is split by responsibility so strategy code does not own trading safety or broker execution:

- `Agent` — analyses market context and produces `AgentDecision` objects only.
- `Brokers` — broker adapters, broker account/order-state execution checks, and the pure broker/local position reconciliation engine.
- `RiskManager` — pre-trade risk policy, sizing/exposure limits, protective-order requirements, and daily/weekly/consecutive-loss safety state.
- `ExecutionManager` — idempotent order orchestration, broker-state/risk assessment coordination, order submission, execution journaling, and reconciliation safety escalation.
- `TradeManager` — post-entry break-even, trailing-stop, and structure-break exit recommendations.
- `TradingCore` — the end-to-end safe trading pipeline and market-data quality gate.
- `TradingJournal` — reusable bounded audit-journal contracts and in-memory implementation.
- `ChartAnnotator` — indicators, swings, zones, trendlines, channels, and market structure.
- `Simulator` — deterministic broker simulation, streamed comparative orchestration, job queue, and chunked replay.
- `DashboardLive` — live workspace feeds plus simulation REST/SignalR API.
- `BacktestRunner` — thin CLI adapter over the shared `IBacktestApplicationService`.

`ExecutionManager` intentionally coordinates broker safety and risk policy, but it does not define either policy. Broker-specific/account-order checks live in `Brokers.Safety`; financial risk thresholds live in `RiskManager`; and the comparison portion of reconciliation lives in `Brokers.Reconciliation`.

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

> The current broker abstraction supports stop-loss/take-profit instructions when opening an order, but it does not yet expose a broker-neutral amend-stop endpoint. `StructureBasedTradeManager` therefore produces deterministic stop/exit recommendations for the execution layer to apply when amend APIs are added.

## Implemented Phase 2: chart annotation and structure

- Confirmed swing-high and swing-low detection without lookahead.
- ATR, RSI, and Bollinger indicator snapshots.
- Derived indicator context:
  - Bollinger normalized bandwidth, historical percentile, narrowing/widening state, squeeze detection, and squeeze-release/expansion events;
  - RSI zone and momentum direction, regular and hidden bullish/bearish divergence, and same-direction bullish/bearish convergence confirmed only after price swings are known;
  - normalized ATR percentage, recent percentile, volatility regime, and contracting/expanding direction.
- DBSCAN support/resistance zones.
- Deterministic RANSAC trendlines.
- Channel detection.
- Rising, falling, sideways, and unknown market-structure classification.
- Bullish and bearish break-of-structure detection.
- Trendline/channel recalculation when market direction changes.
- Optional removal of stale lines after a structure-direction change.
- Confidence scoring that includes structure alignment, recent RSI relationships, volatility context, Bollinger squeeze/expansion state, and adverse-break penalties.
- Full replay-window dashboard support, including an **All candles** option and market-structure display.

## Streamed dual-strategy simulator (dashboard-first)

The primary UX is **Dashboard → Simulator**:

1. Start `DashboardLive` and the Vue dashboard.
2. Open the **Simulator** tab.
3. Configure instrument/range/strategies and click **Run Simulation**.
4. Receive a `simulationId` immediately; progress, balances, and trades stream while the backend processes 1m candles as fast as correctness allows.
5. Pause/resume **compute** independently from visual playback.

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

## Recommended next work

- Add broker-neutral amend/cancel protection APIs and wire trade-management recommendations to live positions.
- Persist the audit journal and safety state to PostgreSQL rather than memory only.
- Run long soak tests with forced disconnects, restarts, partial fills, and position reconciliation failures.
- Add portfolio-level currency/correlation exposure controls before expanding the live instrument universe.
- Generate Phase 3 ML training rows from historical annotations using time-split, lookahead-safe labels.
