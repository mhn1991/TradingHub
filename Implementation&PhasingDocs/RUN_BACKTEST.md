# Running streamed dual-strategy backtests

## Dashboard (primary)

```bash
# Terminal 1 — API + SignalR + static dashboard host
dotnet run -c Release --project DashboardLive/DashboardLive.csproj

# Terminal 2 — Vite dev server (proxies /api to DashboardLive when configured)
cd Dashboard
npm ci
npm run dev
```

1. Open the app and choose **Simulator**.
2. The balanced research preset is ready to run: EUR/USD, previous full UTC month,
   0.5% equity risk, 2h→1h→30m→15m→5m role stack, conservative fills, and a
   21-day warm-up. Usually you only need to change the instrument or dates.
   Expand **Advanced** to tune strategy, cost, sizing, and management controls.
3. Click **Run Simulation**.
4. Watch job status, candles/sec, balances, and strategy comparison update live.
5. Use **Pause compute** / **Resume compute** for the backend clock; use playback controls for the UI only.
6. Refresh the browser — jobs reload from `.cache/simulation-jobs`.
7. Select a completed trade to load bounded 1s/5s execution-detail chunks and inspect its stepped stop history.

API quick-start:

```bash
curl -s -X POST http://localhost:5XXX/api/simulations \
  -H 'content-type: application/json' \
  -d '{
    "instrument":"FX:EUR/USD",
    "from":"2026-06-01T00:00:00Z",
    "to":"2026-07-01T00:00:00Z",
    "strategies":["legacy","improved"],
    "legacyPositionManagement":{"mode":"StructureAtr","managementInterval":"5m","breakEvenActivationR":1.0,"structureTrailActivationR":1.5,"atrBufferMultiplier":0.25,"enableScaleOut":true,"enableProfitFloor":true,"enableMaximumGiveback":true,"minimumRunnerFraction":0.40},
    "improvedPositionManagement":{"mode":"StructureAtr","managementInterval":"5m","breakEvenActivationR":1.0,"structureTrailActivationR":2.0,"atrBufferMultiplier":0.25,"enableScaleOut":true,"enableProfitFloor":true,"enableMaximumGiveback":true,"minimumRunnerFraction":0.50,"preserveBracketTarget":true},
    "dailyEquityProfitTarget":3000,
    "dailyEquityGivebackActivation":2500,
    "maximumDailyEquityGiveback":750,
    "warmupDays":21,
    "strategyExecutionMode":"ParallelWorkers"
  }'
```

## CLI (same service)

The broker credential vault must be bootstrapped first; see
[`BROKER_CREDENTIAL_VAULT.md`](BROKER_CREDENTIAL_VAULT.md).

```bash
dotnet run -c Release --project BacktestRunner/BacktestRunner.csproj -- \
  --instrument FX:EUR/USD \
  --from 2026-06-01 \
  --to 2026-07-01 \
  --base-interval 1m \
  --analysis-intervals 5m,15m,30m,1h,2h \
  --warmup-days 21 \
  --strategies legacy,improved \
  --strategy-execution parallel \
  --quantity 1000 \
  --starting-balance 100000 \
  --minimum-rr 1.5 \
  --output Dashboard/public/data/backtests
```

Useful flags:

| Flag | Meaning |
| --- | --- |
| `--refresh` | Rebuild candle cache |
| `--no-cache` | Stream without writing cache |
| `--strategy-execution sequential\|parallel` | Worker hosting mode |
| `--precision-mode fast\|broker-native\|high-precision` | Execution precision preset |
| `--source oanda\|imported` | Historical data source |
| `--imported-candles PATH` | Validated 1s/5s midpoint CSV for imported mode |
| `--analysis-base-interval 1m` | Smallest analysis candle |
| `--trend-interval 1h` | Strategy trend timeframe |
| `--confirmation-interval 15m` | Strategy confirmation timeframe |
| `--entry-interval 5m` | Strategy entry timeframe |
| `--analysis-sharing shared\|independent` | Shared vs per-strategy annotators |
| `--ambiguous-policy stop-first\|target-first\|nearest-open` | Intrabar ambiguity |
| `--warmup-days N` | Pre-evaluation warm-up |
| `--trailing-comparison` | Run disabled and configured trailing for both strategies and export a comparison |
| `--legacy-trailing-mode disabled\|break-even\|structure-atr` | Legacy trailing policy |
| `--legacy-management-interval 5m` | Legacy management close interval |
| `--legacy-break-even-r / --legacy-structure-r / --legacy-atr-buffer` | Legacy thresholds |
| `--improved-trailing-mode disabled\|break-even\|structure-atr` | Improved trailing policy |
| `--improved-management-interval 5m` | Improved management close interval |
| `--improved-break-even-r / --improved-structure-r / --improved-atr-buffer` | Improved thresholds |
| `--legacy-no-scale-out / --improved-no-scale-out` | Disable staged partial exits for one strategy |
| `--legacy-no-profit-floor / --improved-no-profit-floor` | Disable the R-based locked-profit floor |
| `--legacy-no-giveback / --improved-no-giveback` | Disable MFE maximum-giveback protection |
| `--legacy-no-stagnation-reduction / --improved-no-stagnation-reduction` | Disable time-without-progress reductions |
| `--legacy-no-structure-reduction / --improved-no-structure-reduction` | Disable adverse-structure reductions |
| `--legacy-no-momentum-reduction / --improved-no-momentum-reduction` | Disable corroborated momentum-decay reductions |
| `--legacy-no-volatility-reduction / --improved-no-volatility-reduction` | Disable post-expansion volatility-exhaustion reductions |
| `--legacy-minimum-runner / --improved-minimum-runner` | Smallest fraction of initial quantity retained as the runner |
| `--legacy-risk-window-start HH:mm / --legacy-risk-window-end HH:mm` | Enable a UTC risk-window reduction (same improved-prefixed flags apply) |
| `--legacy-enable-cost-stress-reduction` | Enable spread/ATR execution-cost-stress reduction (same improved-prefixed flag applies) |
| `--daily-equity-profit-target AMOUNT` | Pause new entries after the UTC-day equity-profit target |
| `--daily-equity-giveback-activation AMOUNT` | Profit level after which daily peak-giveback protection activates |
| `--maximum-daily-equity-giveback AMOUNT` | Allowed giveback from the UTC-day equity peak before pausing new entries |

## Outputs

- Chunked progressive replay: `Dashboard/public/data/simulations/{id}/` (or configured output)
- Dashboard-compatible summary export (CLI): `Dashboard/public/data/backtests/`
- Job snapshots: `.cache/simulation-jobs/`
- Candle cache: `.cache/oanda/`
- Trailing comparison: `Dashboard/public/data/backtests/trailing-comparison.json`

Dashboard imports never accept client-provided server paths. Uploads receive opaque dataset IDs, are interval-validated, listed for reuse, expire after seven days, and can be deleted from the Simulator form.

## Offline / CI

Tests inject `BacktestRequest.InlineCandles` so no OANDA credentials are required:

```bash
dotnet test TradingHub.slnx -c Release --filter FullyQualifiedName~StreamingComparativeEngineTests
```

## Validation

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
cd Dashboard && npm ci && npm run build
```
