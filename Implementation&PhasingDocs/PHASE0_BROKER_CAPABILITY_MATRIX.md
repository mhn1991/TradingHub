# Phase 0 — OANDA Broker Capability Matrix

**Purpose:** Deliverable 3 of the Live OANDA Multi-Agent Production Implementation Plan's Phase 0
(§38). Documents what `Brokers/Oanda/*` supports today versus what a live trading host (Phase 1+)
will need, so the gap is explicit rather than discovered mid-build.

## Construction

```csharp
BrokerClientFactory.CreateOanda(OandaOptions options) -> OandaBrokerClient
```
(`Brokers/BrokerClientFactory.cs`). `OandaOptions` requires `Environment` (`BrokerEnvironment`),
`AccountId`, `AccessToken`; optional `BaseAddress`/`StreamBaseAddress`/`RequestTimeout`/
`InstrumentMappings`. Already used in `Simulator/Services/BacktestApplicationService.cs:768` (for
network-backed backtests) and `DashboardLive/SimulationBrokerCatalog.cs:222`.

`OandaBrokerClient` implements `IProtectiveOrderBrokerClient : ITradingBrokerClient : IBrokerClient`
— already type-compatible with `ExecutionManager.IExecutionCoordinator.ProcessAsync`/
`AmendProtectiveStopAsync`, which take `ITradingBrokerClient` as a parameter. This means the
*type-level* plumbing for live execution already exists; what's missing is any code that actually
constructs that pairing (see "Gaps" below).

## Capabilities already implemented

| Capability | Type / Method | Notes |
|---|---|---|
| REST completed candles | `OandaMarketDataClient.GetCandlesAsync(CandleQuery, CancellationToken)` (internal, exposed as `OandaBrokerClient.MarketData`) | Used today for backtests against real historical data. |
| Live pricing stream | `OandaBrokerClient.StreamPricesAsync(IReadOnlyCollection<string> instruments, CancellationToken) : IAsyncEnumerable<OandaPriceTick>` | Hits `v3/accounts/{accountId}/pricing/stream`; line-delimited JSON parsed via `OandaMappings.ToPriceTick`. |
| Transaction / order-event stream | `OandaOrderClient.StreamOrderEventsAsync(CancellationToken) : IAsyncEnumerable<OrderEvent>` (internal, exposed as `OandaBrokerClient.Orders`) | Hits `v3/accounts/{accountId}/transactions/stream`; parsed via `OandaMappings.ToOrderEvent`. |
| Broker-agnostic execution interface compatibility | `OandaBrokerClient : IProtectiveOrderBrokerClient : ITradingBrokerClient` | Type-compatible with the same `IExecutionCoordinator` the simulator uses — no separate live-specific execution interface needed. |

## Gaps — explicitly out of scope for Phase 0, named for Phase 1+

| Gap | Current state | Why it matters |
|---|---|---|
| No live order-placement wiring | `ExecutionManager.ExecutionCoordinator` is constructed only against `SimulatedBrokerClient` today (`Simulator/Engine/SimulationFactory.cs`, `StrategySimulationSession.cs`, `Simulator.Tests/*`). Grep-confirmed: no call site anywhere in the repo passes an `OandaBrokerClient` into `ProcessAsync`/`AmendProtectiveStopAsync`. | This is the single largest piece of net-new work for Phase 1 — even though the types line up, nobody has exercised order placement/amendment/cancellation against a real (even practice) OANDA account through this coordinator. |
| No account summary / margin query path exercised live | `Brokers/Oanda/*` client surface not audited here for an account-summary endpoint wrapper beyond what pricing/orders/candles need. | A live host needs real-time equity/margin/available-funds visibility feeding `TradingSafetyController`/`PreTradeRiskManager` — must be designed and built, not assumed to exist. |
| Atomic `stopLossOnFill` not verified against a real account | Plan doc §3 requires every autonomous entry to carry a broker-side protective stop attached atomically at order submission. `OandaCommands.cs`/`OandaDtos.cs` presumably support the request shape (OANDA's API supports `stopLossOnFill`), but this has not been exercised end-to-end against a practice account in this repo. | Core safety invariant for autonomous demo trading (plan §3) — must be proven with a real fill before any `AutonomousDemo`-mode code ships. |
| `Brokers.IntegrationTests` are network-skipped | 13 tests skip in this sandboxed environment (pricing stream, multi-timeframe candles, open orders/positions reads) — confirmed in the Phase 0 baseline report. | These are the existing tests closest to proving live-path correctness; they need to be run somewhere with real network access before Phase 1 can trust the broker client end-to-end. |

## Disambiguation: two `ExecutionCoordinator` types exist

- `ExecutionManager.ExecutionCoordinator` / `IExecutionCoordinator` — the real one, used
  throughout `Simulator`, and the one `TradingCore.Pipeline.StrategyDecisionPipelineFactory`
  (Stage 4) is built against.
- `Agent.Execution.ExecutionCoordinator` — a simpler, unused type (`ExecutionCoordinator(ExecutionOptions? options = null)`
  only, no risk manager/journal/safety wiring, no `AmendProtectiveStopAsync`). Grep-confirmed
  nothing outside `Agent/Execution/*.cs` references it. Not used anywhere in Phase 0, and should
  not be confused with the real one in any later phase.

## Conclusion for Phase 0

No code in this phase constructs an `OandaBrokerClient` and passes it into
`ExecutionCoordinator`/`StrategyDecisionPipelineFactory`. The factory built in Stage 4 is
broker-agnostic by design (it accepts `IExecutionCoordinator`/`ITradingBrokerClient`-shaped
collaborators through `SafeTradingPipeline`, never a concrete broker type), so wiring OANDA into
it later is additive, not a redesign — but that wiring itself does not happen until Phase 1.
