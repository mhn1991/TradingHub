# TradingHub Final Phase 2 Validation

## Added in this completion pass

- deterministic price-action snapshot layer;
- close-confirmed BOS and CHoCH;
- stateful break/retest tracking;
- structural rejection, displacement, liquidity-sweep, and compression-breakout evidence;
- price-leg metrics;
- incremental ADX/+DI/-DI;
- per-instrument/per-timeframe warm-up calibration frozen before evaluation;
- Agent price-action `Disabled`/`Soft`/`Required` modes;
- price-action decision reason codes and replay events;
- `TradeManagementEvaluated` events, including hold decisions;
- price-action replay contracts and chart markers;
- versioned persisted-job envelopes with corrupt/legacy-file quarantine;
- health endpoint and structured start errors;
- corrected decimal input constraints for R values;
- corrupt-job and price-action regression test source;
- clean packaging rules and security cleanup.

## Frontend validation executed

```bash
cd Dashboard
rm -rf node_modules dist
npm ci
npm run typecheck
npm run build
```

Result: passed. npm reported zero vulnerabilities. The sample replay also passed `npm run data:validate` across 680 frames.

## C# validation limitation

The artifact environment does not contain the .NET SDK, so `dotnet restore`, `dotnet build`, and `dotnet test` could not be executed here. C# files were statically reviewed, balanced for delimiters, and new NUnit regression tests were added, but the recipient must run the commands below on a .NET 10 development machine before deployment.

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
```

## Remaining Phase 2 limitations

- OANDA historical execution remains midpoint plus configured spread, not true historical bid/ask;
- native OANDA protective-stop amendment remains capability-dependent; the simulator path is deterministic;
- overnight financing/swap is not yet modelled;
- price-action thresholds are calibrated from warm-up, but outcome-sensitive strategy parameters are not automatically optimised;
- walk-forward and untouched holdout runs still need to be performed with the user’s market datasets.

These limitations do not prevent rule-based research, but they must be addressed or measured before real-money deployment.


## One-command recipient validation

Run:

```bash
./validate.sh
```

This performs the full .NET restore/build/test and Dashboard install/typecheck/build/data validation on a machine with the .NET 10 SDK and Node.js installed.
## Broker and asset catalog completion

- Added `GET /api/simulations/catalog` with a 30-minute capability-aware cache.
- Added OANDA account instrument discovery and Forex/Metals/CFDs grouping.
- Added public Binance Spot instrument discovery and historical candle streaming.
- Added broker, asset-class, search, and instrument selectors to Simulator mode.
- Added server-side broker/instrument validation before job creation.
- Added imported-dataset interval synchronisation.
- IG remains visibly unavailable until its historical source is certified.
- Frontend typecheck and production build pass after these changes.
- The artifact environment still lacks the .NET SDK; run `./validate.sh` on a .NET 10 machine.

