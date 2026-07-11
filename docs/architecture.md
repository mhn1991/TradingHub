# Architecture

## Design goals

TradingHub is organized for both horizontal and vertical growth:

- Horizontal modules contain stable capabilities shared across features: domain contracts, time, market-data boundaries, broker boundaries, application services, and simulation infrastructure.
- Vertical folders contain one integration or workflow end to end: OANDA, Binance, historical CSV replay, a strategy, and eventually reconciliation or monitoring.

All broker providers live inside one `TradingHub.Brokers` project. Adding another account is configuration-only. Adding a genuinely new provider means adding one provider folder and registering its `IBrokerProvider`; it does not require a project or changes to the simulator, risk engine, or other providers.

## Dependency direction

```text
                    TradingHub.Domain
                           |
                TradingHub.Abstractions
                    /             \
           Application       TradingHub.Brokers
                    \             /
                   Simulation / hosts
```

The source projects do not depend on executable hosts. `TradingHub.Application` and `TradingHub.Simulation` depend only on `IBrokerGateway` from `TradingHub.Abstractions`; they do not reference the external broker implementation project. OANDA and Binance are provider folders in the same broker module and share HTTP safety, result factories, live-account guards, factory contracts, and configuration models.

## Runtime broker composition

```text
configuration item
    -> IBrokerFactory
       -> provider registry (IBrokerProvider)
          -> credential store
             -> configured IBrokerGateway instance
```

`BrokerDefinition.Id` identifies a runtime instance such as `oanda-demo`, not a provider. `Provider` selects OANDA or Binance. This allows many accounts for the same provider without new classes:

```json
{
  "Id": "oanda-demo",
  "Provider": "Oanda",
  "Environment": "Demo",
  "Account": {
    "AccountId": "...",
    "CredentialKey": "OANDA_DEMO"
  },
  "Instruments": [
    {
      "InstrumentId": "FX:GBP-JPY",
      "BrokerSymbol": "GBP_JPY"
    }
  ]
}
```

Configuration contains identity, environment, settings, and whitelist mappings. Provider code still owns protocol behaviour such as OANDA bearer authentication and Binance HMAC signing. Making those behaviours arbitrary database rows would hide type safety and make order safety difficult to verify.

The factory lazily creates and caches one gateway per configured broker ID. Concurrent requests for the same ID receive the same instance, which leaves room for each instance to own streaming connections and reconciliation state later.

## Canonical internal contracts

These are broker-neutral records in `TradingHub.Domain`:

- `PriceBar`: canonical bid/ask OHLC bar used by strategies and the simulator.
- `TradeIntent`: a strategy proposal, not permission to trade.
- `OrderRequest`: a risk-approved internal order command.
- `OrderCancellationRequest`: broker-neutral cancellation information, including the instrument needed by Binance.
- `OrderSubmissionResult`: accepted, rejected, or unknown submission state.
- `ExecutionReport`: canonical fill and order-state update.
- `BrokerAccountSnapshot`: canonical balances and positions for reconciliation.

External DTOs such as `OandaOrderResponseDto` and `BinanceOrderResponseDto` are `internal` and live under each provider's `Contracts` folder. Mapper classes are the only place that translates external names and semantics into canonical contracts.

## Asynchronous boundaries

- File and HTTP I/O use `Task`, `await`, and cancellation tokens.
- Historical feeds use `IAsyncEnumerable<PriceBar>` so a database, object store, or broker downloader can replace CSV without changing the engine.
- Strategy evaluation uses `ValueTask` because many bars intentionally produce no signal.
- The deterministic simulation event queue awaits every handler in stable timestamp, phase, and sequence order.

CPU-only state transitions such as risk evaluation and portfolio fill accounting remain synchronous. Wrapping them in artificial tasks would add scheduling noise without making them more scalable.

## Simulation causality

For each historical bar close, the event queue runs two phases:

1. Existing eligible orders are evaluated against that completed interval.
2. Strategies receive the completed bar and may create new orders.

An order created from a bar therefore cannot fill inside that same bar. It becomes eligible on a later bar after configured outbound latency. A stop-limit trigger is also conservative: when triggered from OHLC data, its limit leg cannot fill until a later bar because the intrabar path is unknown.

## Environment safety

The official endpoints are fixed in code:

| Adapter environment | REST endpoint |
|---|---|
| OANDA Practice | `https://api-fxpractice.oanda.com/` |
| OANDA Live | `https://api-fxtrade.oanda.com/` |
| Binance Spot Testnet | `https://testnet.binance.vision/` |
| Binance Live | `https://api.binance.com/` |

An injected `HttpClient` with a different base endpoint is rejected. Live order and cancellation requests additionally require `LiveAccountConfirmation` to exactly match the configured account ID. There is no automatic demo-to-live fallback.

Ambiguous HTTP timeouts, rate-limit responses, and server errors return `OrderStatus.Unknown`. The order must be reconciled before any retry to avoid duplicate live orders.

## Near-term vertical modules

The next useful slices are:

1. OANDA pricing/transaction stream plus reconnect and heartbeat handling.
2. Binance book-ticker/user-data stream plus listen-key lifecycle.
3. Reconciliation worker that compares canonical local state with broker snapshots.
4. Instrument-catalog synchronizers for OANDA instrument metadata and Binance `exchangeInfo` filters.
5. Durable event journal and run metadata hashes.
6. Shadow/demo worker host using the same `TradingSession` as simulation.

The broker-project consolidation decision and its relationship to the preserved baseline are recorded in [ADR 001](decisions/001-single-broker-module.md).
