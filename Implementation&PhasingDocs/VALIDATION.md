# Validation record

Date: 2026-07-16 (quantitative-enhancements corrective pass, through §7 multi-instrument
portfolio clock — see `TradingHub_Quantitative_Enhancements_Final_Audit_and_Corrective_Prompt.md`
for the audit this validates, and `ARCHITECTURE_SIMULATOR.md` § Quantitative risk /
portfolio layer for what changed). Prior record (2026-07-13, 290+58 tests) preserved
below for history.

## Commands run

```bash
dotnet build TradingHub.slnx -c Release
# Build succeeded. 0 Warning(s). 0 Error(s).

dotnet test TradingHub.slnx -c Release
# Simulator.Tests: Passed 593
# TradingHub.UnitTests: Passed 60
# Brokers.IntegrationTests: Skipped 13 (environment-controlled, needs live credentials)

cd Dashboard && npm ci && npm run typecheck && npm run build && npm run data:validate
# vue-tsc + vite build succeeded; replay validation passed for 680 frames
```

## Prior record (2026-07-13)

```bash
dotnet build TradingHub.slnx -c Release
# Build succeeded. 0 Warning(s). 0 Error(s).

dotnet test TradingHub.slnx -c Release
# Simulator.Tests: Passed 290
# TradingHub.UnitTests: Passed 58
# Brokers.IntegrationTests: Skipped 13 (environment-controlled)

cd Dashboard && npm ci && npm run build
# vue-tsc + vite build succeeded
```

## Streaming engine checks

- Sequential vs parallel strategy modes produce identical trade counts / net P/L / balances on synthetic streams.
- Application service completes jobs with inline candles, writes `manifest.json` + `COMPLETE`.
- Prefetch stream preserves order and count under backpressure.
- Runtime options reject invalid prefetch bounds.

## Security

- Broker credentials are read from encrypted PostgreSQL rows through `IBrokerCredentialStore`; secret environment variables are not used by runtime hosts.
- `BacktestRequest.AccessToken` / `AccountId` / `InlineCandles` are `[JsonIgnore]` and not persisted in job snapshots.
- `.gitignore` covers `.env`, `.env.*`, `.cache/`, simulation outputs, and compressed caches.
- `.env.example` remains a placeholder template; real broker secrets are imported from a temporary ignored file and then removed.

## Remaining limitations

See `ARCHITECTURE_SIMULATOR.md` § Known limitations.
