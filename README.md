# TradingHub

TradingHub is a simulator-first, multi-broker trading platform foundation. The current vertical slice replays historical bid/ask bars through a strategy, pre-trade risk controls, order management, a simulated broker, portfolio accounting, and result reporting.

It also contains safe REST foundations for OANDA v20 practice/live accounts and Binance Spot Testnet/live accounts. Live submission is locked unless configuration repeats the exact configured account ID as an explicit confirmation.

## Current architecture

```text
Historical CSV -> canonical PriceBar -> strategy -> TradeIntent
    -> risk engine -> canonical OrderRequest -> IBrokerGateway
       -> SimulatedBroker / OandaBrokerGateway / BinanceBrokerGateway
          -> canonical ExecutionReport -> portfolio and reports
```

All broker integrations live in one `TradingHub.Brokers` project. Broker-specific JSON DTOs are internal provider details and are mapped at the boundary; they never leak into the application, strategies, risk engine, or simulator.

| Project | Responsibility |
|---|---|
| `TradingHub.Domain` | Canonical instruments, bars, intents, orders, fills, and account snapshots |
| `TradingHub.Abstractions` | Broker, time, and asynchronous market-data boundaries shared by all implementations |
| `TradingHub.Brokers` | Broker contracts, config/DI factory, credentials boundary, OANDA and Binance providers |
| `TradingHub.Application` | Strategies, risk, portfolio accounting, and trading-session orchestration |
| `TradingHub.Simulation` | Virtual time, deterministic event queue, historical input, fill model, and results |
| `TradingHub.Simulation.Runner` | Composition root and executable simulation workflow |
| `TradingHub.Simulation.Tests` | Simulation, risk, broker safety, signing, and mapping tests |

The dependency direction and extension points are described in [docs/architecture.md](docs/architecture.md).

## Run the sample simulation

The included sample is deliberately small and exists to exercise the pipeline, not to demonstrate a profitable strategy.

```bash
dotnet run --project apps/TradingHub.Simulation.Runner
```

Or provide another configuration:

```bash
dotnet run --project apps/TradingHub.Simulation.Runner -- \
  --config /absolute/path/to/simulation.json
```

The default configuration is [samples/simulation.json](samples/simulation.json). Its paths are resolved relative to the configuration file. Results are written to `samples/results/<run-id>/` and are intentionally ignored by Git.

Generated files:

- `summary.json`
- `orders.csv`
- `fills.csv`
- `equity-curve.csv`

## Verify

```bash
dotnet build TradingHub.sln
dotnet test tests/TradingHub.Simulation.Tests/TradingHub.Simulation.Tests.csproj
```

Warnings are treated as errors across the repository.

## Broker credentials

Broker accounts are declared in [samples/brokers.json](samples/brokers.json). The generic `IBrokerFactory` selects the provider and creates an `IBrokerGateway` by configured instance ID:

```csharp
services.AddTradingHubBrokers(configuration.GetSection("TradingHub:Brokers"));

var brokerFactory = serviceProvider.GetRequiredService<IBrokerFactory>();
var oandaDemo = await brokerFactory.CreateAsync("oanda-demo");
var binanceTestnet = await brokerFactory.CreateAsync("binance-testnet");
```

Adding another OANDA or Binance account requires another configuration item, not another class or project. Multiple instances of the same provider can coexist with different accounts and instrument whitelists.

The default credential store reads environment variables through the configuration item's `CredentialKey`:

```text
OANDA_DEMO__ACCESS_TOKEN
BINANCE_TESTNET__API_KEY
BINANCE_TESTNET__SECRET_KEY
```

Applications can replace `IBrokerCredentialStore` in DI with a vault-backed implementation. No secrets belong in committed JSON configuration.
The expected names are also listed in [.env.example](.env.example); the application does not automatically load `.env` files.

Never reuse a test key for live trading. Use broker-side IP restrictions and the minimum API permissions required. The adapters do not automatically retry ambiguous order submissions; they return `Unknown` so reconciliation can determine the actual broker state first.

## Implemented scope and deliberate limits

Implemented:

- Deterministic asynchronous historical replay.
- Separate bid/ask OHLC values.
- Same-candle look-ahead prevention.
- Market, limit, stop, and conservative stop-limit simulation.
- Configurable slippage, commission, latency, and per-bar fill quantity.
- Central quantity, position, spread, open-order, whitelist, expiry, and short-selling checks.
- Simulated broker-side account independent of the application portfolio.
- OANDA and Binance market/limit REST order foundations and account snapshots.
- Config/DI creation of multiple broker instances through one broker module.
- Explicit demo/testnet versus live endpoints and account-specific live locks.

Not production-ready yet:

- Broker price/user-data streams and reconnect supervisors.
- Order and position reconciliation workers.
- Broker instrument-catalog synchronization and Binance exchange-filter enforcement.
- Persistent database/event journal.
- Currency conversion for P&L when account and quote currencies differ.
- Funding, swaps, margin, liquidation, and corporate actions.
- Tick/order-book replay and queue-position modelling.
- Web API, operator dashboard, alerts, and kill switch.

Those should be added as new vertical modules around the existing canonical contracts rather than inserted into the broker or simulator classes.
