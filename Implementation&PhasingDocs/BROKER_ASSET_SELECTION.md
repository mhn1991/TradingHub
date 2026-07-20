# Simulator Broker and Asset Selection

The Dashboard simulator now discovers instruments from the selected historical data source instead of requiring users to remember canonical instrument keys.

## Supported choices

### OANDA

- Requires the configured OANDA account ID and access token.
- Lists only instruments enabled for that account.
- Groups instruments into Forex, Metals, CFDs, and Other.
- Supports fast 1-minute execution and OANDA-native 5-second precision.
- Historical execution currently uses midpoint candles plus the configured spread model.

Broker credentials are loaded from the encrypted PostgreSQL vault. Bootstrap or rotate them with
the procedure in [`BROKER_CREDENTIAL_VAULT.md`](BROKER_CREDENTIAL_VAULT.md). Non-secret broker
environment selection remains in application configuration.

Secret environment variables are intentionally not used by the runtime.

### Binance Spot

- Discovers currently available public spot pairs.
- Does not require API keys for historical candle simulations.
- Groups instruments as Crypto Spot.
- Supports native Binance candle intervals, including 1-second execution where the public endpoint provides it.
- This enables historical simulation only; it does not enable live Binance order execution.

An optional public data endpoint override can be supplied with:

```bash
export BINANCE_MARKET_DATA_BASE_URL="https://data-api.binance.vision/"
```

### Imported candle dataset

- Upload or select a validated 1-second or 5-second midpoint OHLC CSV.
- The execution interval is automatically set to the selected dataset interval.
- The instrument key remains user-supplied because the file format does not contain a trusted broker catalog identifier.

### IG

IG is shown as unavailable. Broker account/order integration exists, but a certified historical candle source and instrument catalog have not yet been connected to the simulator. The UI must not pretend otherwise.

## Dashboard workflow

1. Select the historical broker.
2. Review the broker environment and capability description.
3. Filter by asset class.
4. Search by symbol, display name, or canonical instrument key.
5. Select the instrument from the catalog.
6. Select a precision mode supported by that broker.
7. Start the simulation.

The server validates that the submitted instrument belongs to the selected broker catalog. A stale or manually forged instrument is rejected before a long-running job is queued.

## Canonical examples

```text
OANDA Forex:       FX:GBP/JPY
OANDA metal:       METAL:XAU/USD
OANDA CFD:         CFD:...
Binance spot:      CRYPTO:BTC/USDT
Imported dataset:  user-supplied canonical key
```

## Refresh behaviour

Broker catalogs are cached for 30 minutes. Use **Refresh broker assets** after enabling a new OANDA instrument, changing account credentials, or when a Binance listing changed.
