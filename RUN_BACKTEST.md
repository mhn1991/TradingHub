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
2. Set instrument, dates, strategies, costs, warm-up, sequential/parallel mode.
3. Click **Run Simulation**.
4. Watch job status, candles/sec, balances, and strategy comparison update live.
5. Use **Pause compute** / **Resume compute** for the backend clock; use playback controls for the UI only.
6. Refresh the browser — jobs reload from `.cache/simulation-jobs`.

API quick-start:

```bash
curl -s -X POST http://localhost:5XXX/api/simulations \
  -H 'content-type: application/json' \
  -d '{
    "instrument":"FX:GBP/JPY",
    "from":"2025-01-01T00:00:00Z",
    "to":"2025-02-01T00:00:00Z",
    "strategies":["legacy","improved"],
    "warmupDays":5,
    "strategyExecutionMode":"ParallelWorkers"
  }'
```

## CLI (same service)

```bash
export Oanda__AccountId='YOUR_PRACTICE_ACCOUNT_ID'
export Oanda__AccessToken='YOUR_PRACTICE_ACCESS_TOKEN'

dotnet run -c Release --project BacktestRunner/BacktestRunner.csproj -- \
  --instrument FX:GBP/JPY \
  --from 2025-01-01 \
  --to 2026-01-01 \
  --base-interval 1m \
  --analysis-intervals 5m,15m,1h \
  --warmup-days 45 \
  --strategies legacy,improved \
  --strategy-execution parallel \
  --strategy-worker task \
  --quantity 1000 \
  --starting-balance 100000 \
  --minimum-rr 1.5 \
  --seed 12345 \
  --output Dashboard/public/data/backtests
```

Useful flags:

| Flag | Meaning |
| --- | --- |
| `--refresh` | Rebuild candle cache |
| `--no-cache` | Stream without writing cache |
| `--strategy-execution sequential\|parallel` | Worker hosting mode |
| `--strategy-worker task\|thread` | Task vs dedicated thread |
| `--analysis-sharing shared\|independent` | Shared vs per-strategy annotators |
| `--ambiguous-policy stop-first\|target-first\|nearest-open` | Intrabar ambiguity |
| `--warmup-days N` | Pre-evaluation warm-up |

## Outputs

- Chunked progressive replay: `Dashboard/public/data/simulations/{id}/` (or configured output)
- Dashboard-compatible summary export (CLI): `Dashboard/public/data/backtests/`
- Job snapshots: `.cache/simulation-jobs/`
- Candle cache: `.cache/oanda/`

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
