# TradingHub analysis dashboard

This Vue dashboard replays the actual output of `ChartAnnotationEngine`. It shows:

- Candles, volume, Bollinger Bands, RSI, and ATR.
- Bollinger squeeze/narrowing/widening/expansion context with normalized bandwidth and historical percentile.
- RSI momentum plus regular/hidden divergence and same-direction convergence, revealed only after the underlying swing is confirmed.
- ATR normalized volatility regime, percentile, and contraction/expansion direction.
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

## Chart navigation and annotations

- Drag the chart horizontally to move through the known candle history without changing replay time.
- Use the mouse wheel over the chart to zoom in or out.
- **Older**, **Newer**, **Latest**, and **Fit** provide keyboard-accessible viewport controls.
- The viewport never displays candles after the selected replay frame, so historical inspection does not introduce look-ahead.
- Bollinger squeeze and expansion periods are shaded; squeeze-release candles have a vertical marker.
- RSI regular/hidden divergence and convergence are drawn on both price and RSI panels.
- Normalized ATR has its own panel, with low/high volatility regimes shaded.
- Multiple support/resistance fits are retained across structure regimes. Their confirmed
  pivot span is solid, endpoints are marked, and the projection to the chart edge is dashed.
- Channels use distinct boundary pairs and follow the same confirmed/projected treatment.
- Price zones are clipped overlays and do not change the candle price scale.
- Each derived layer can be enabled or disabled independently from the chart header.


## Run a historical dual-strategy backtest

From the repository root, set your OANDA practice credentials and run the new CLI:

```bash
export Oanda__AccountId='YOUR_PRACTICE_ACCOUNT_ID'
export Oanda__AccessToken='YOUR_PRACTICE_ACCESS_TOKEN'

dotnet run --configuration Release --project BacktestRunner/BacktestRunner.csproj -- \
  --instrument FX:GBP/JPY \
  --from 2025-07-13 \
  --to 2026-07-13 \
  --execution-interval 1m
```

Then start this Vue application with `npm ci && npm run dev`. The Simulator workspace loads the generated manifest automatically and provides a strategy selector, comparison cards, chart trade overlays, and the complete trade journal. See `../RUN_BACKTEST.md` for all options.

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
