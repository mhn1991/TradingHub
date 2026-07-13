# TradingHub analysis dashboard

This Vue dashboard replays the actual output of `ChartAnnotationEngine`. It shows:

- Candles, volume, Bollinger Bands, and RSI.
- Swing pivots at their historical pivot time, revealed only at `confirmedAt`.
- Support/resistance zones, RANSAC trendlines, and detected channels.
- Confidence contributions, indicator warm-up, replay continuity, and analysis timing.
- Independent 5-minute, 15-minute, and 1-hour replay series.
- Persistent workspaces with a broker, account environment, asset pair, and timeframe.
- Live Binance Spot pair discovery with closed-candle snapshots for each supported timeframe.

## Run locally

From `Dashboard`:

```bash
npm install
dotnet build ../DashboardExporter/DashboardExporter.csproj -c Release
npm run data:generate
npm run data:validate
npm run dev
```

Open `http://localhost:5173`. Use Space to play or pause, and the left/right arrow keys to step through candles.

## Run with live public data

The live service is read-only. It uses Binance's public market-data REST endpoint to warm the indicators and its public kline WebSocket for newly closed candles; it does not load API keys or expose trading endpoints.

Run the backend from the repository root:

```bash
dotnet run --project DashboardLive/DashboardLive.csproj
```

Then either run `npm run dev` from `Dashboard` and select **Live**, or build the Vue app with `npm run build` and open `http://127.0.0.1:5180` directly. Vite proxies `/api` to the backend during development.

The default feed is `BTCUSDT` at `1m`. That configured selection uses the long-lived WebSocket feed. Other Binance workspace pairs and timeframes use cached REST snapshots and refresh after closed candles, so changing a workspace does not create an unbounded number of sockets. Change `DashboardLive/appsettings.json` or override the `LiveFeed` configuration with environment variables to move the streaming selection. Indicators are updated only from closed candles; forming-candle updates never enter `ChartAnnotationEngine`.

Workspaces are saved in browser storage. The environment badge is deliberately explicit: the simulator is **Demo**, Binance is **Live · Read only**, and account-backed brokers remain marked **Setup required** until their backend connection is configured. Standard OANDA symbols are discovered and mapped automatically; IG still requires EPIC mappings. No trading credentials are sent to the Vue application.

### Connect an OANDA practice workspace

OANDA credentials stay in the backend process. Do not add a token to the Vue environment or commit it to `appsettings.json`. Configure the practice account through environment variables before starting `DashboardLive`:

```bash
export Oanda__Enabled=true
export Oanda__Environment=Demo
export Oanda__AccountId='your-practice-account-id'
export Oanda__AccessToken='your-personal-access-token'
dotnet run --project DashboardLive/DashboardLive.csproj
```

The backend discovers the instruments enabled for that account, warms RSI/Bollinger/annotations from historical OANDA candles, and then keeps closed candles current from the practice pricing stream. Account balances, positions, and pending orders are available in the OANDA workspace.

Practice order execution has a separate backend safety gate and is off by default. Enable it only when you intentionally want the dashboard to submit demo orders:

```bash
export Oanda__AllowDemoOrders=true
```

The backend refuses this setting for a Live OANDA environment. The UI also requires local arming and a confirmation for every create or cancel operation. Order mutations are never automatically retried.

## Production build

```bash
npm run build
```

The static application is written to `Dashboard/dist`.

## Loading another replay

Use **Import replay** to open another schema-version-1 JSON file. The checked-in sample is generated from a deterministic regime-change fixture by `DashboardExporter`; future simulator or live-market exporters can produce the same contract.
