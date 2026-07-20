# TradingHub conversation summary

**Date:** 2026-07-18

This document summarizes the design discussion and engineering work from the session. It is not a formal design spec.

---

## 1. Chart: supply/demand and liquidity not visible

### Symptoms
- Supply/demand and liquidity layers were toggled on in the dashboard but nothing drew on the chart.

### Root causes
1. **Detection defaulted off**  
   `SupplyDemandCalculationProfile.Enabled` and `LiquidityCalculationProfile.Enabled` default to `false` (same pattern as NeoWave/regime: off for backtest identity).  
   Chart code only renders when `snapshot.isEnabled` is true.

2. **Dashboard options only enabled NeoWave/regime**  
   `WorkspaceAnalysis.CreateOptions()`, `DashboardExporter`, and simulator annotation options did not enable S/D or liquidity.

3. **Stale sample replay**  
   Default replay file `Dashboard/public/data/sample-replay.json` had **no** `supplyDemand` / `liquidity` fields (pre-feature data).

### Fixes
- Enabled S/D, liquidity, and S/D–liquidity confluence in:
  - `DashboardLive/WorkspaceAnalysis.cs`
  - `DashboardExporter/Program.cs`
  - `DashboardLive/SimulationApi.cs`
- Regenerated sample replay with detection on.
- Improved liquidity chart rendering (band + centre line) and null-safe event handling in `AnalysisChart.vue` / `styles.css`.

**Note:** S/D zones are sparse by design (base + strong departure). Liquidity pools appear more often. Overlays use the **analysis frame** at the viewport edge (typically latest).

---

## 2. Live demo: “Connecting workspace data” / “Unexpected end of JSON input”

### Symptoms
- OANDA (demo) workspace stuck connecting.
- Browser error while indicators/warmup ran: `Unexpected end of JSON input`.

### Root causes
1. **Payload explosion** after enabling S/D + liquidity  
   Each frame carried large retained pools/events. Warmup of hundreds/thousands of frames produced multi‑MB (or larger) SSE snapshots. EventSource / proxies truncated JSON mid-parse.

2. **Bulk SSE update after empty WarmingUp snapshot**  
   First event often had 0 frames; the next update dumped the entire warmup history. An early projector bug kept full structure on **every** frame in that batch.

3. **Binance warmup published every candle**  
   Flooded SSE during indicator calculation.

4. **Old Release process**  
   Running DashboardLive binary did not include projector fixes until rebuild/restart.

### Fixes
- Added `DashboardLive/LiveSseFrameProjector.cs`:
  - Cap snapshot frames (150).
  - Historical frames: candles/indicators/regime only.
  - Latest frame only: capped active S/D zones and liquidity pools; no event rings.
- OANDA **HTTP snapshot** endpoint: `GET /api/workspaces/oanda/snapshot`.
- Dashboard flow: load snapshot over HTTP, then SSE with `updatesOnly=1` for small deltas (`App.vue`).
- Binance catch-up: publish once after warmup, not per candle.
- Reduced default warmup/capacity in `appsettings.json` (e.g. 250 / 500).
- Safer JSON read helpers and clearer truncation errors on the client.

**Operational note:** DashboardLive must run the **new** build on port `5180` with OANDA env vars from `.env.integration` (or equivalent). Vite proxies `/api` → `127.0.0.1:5180`.

---

## 3. Does supply/demand generate signals?

### Answer
**No — not by default, and not as a standalone entry engine.**

| Layer | Default | Effect |
|---|---|---|
| Chart detector | On for dashboard paths | Draws zones |
| Agent evidence mode | `RecordOnly` | Diagnostics / reason codes only |
| Soft confidence / risk | Off unless mode is soft | Optional nudge; risk multiplier ≤ 1 |
| Structural stops / targets / management | Off | Opt-in, entry-pinned when enabled |

Progressive strategy entries still come from **trend → confirmation → entry TF price-action/setup**, then soft overlays (regime, NeoWave, value location, S&D/liquidity, etc.).

Modes: `Disabled` | `RecordOnly` | `SoftConfidence` | `SoftRiskReduction` | `SoftConfidenceAndRisk`.

---

## 4. Design discussion: multi-discipline “signals with confidence”

### Disciplines already present
- **Price Action** — timing / trigger  
- **NeoWave** — narrative / context  
- **Supply & Demand** — location / invalidation  
- **Liquidity** — catalyst, magnets, sweeps vs accepted breaks  
- **Indicators** (CCI, RSI, BB, ATR, ER, ADX, volume, …) — stretch, regime, corroboration  

### Agreed direction (design only — not fully implemented as a composer)
- Each discipline should emit **typed evidence + confidence**, not independent order signals.
- A **confluence composer** should form candidates; existing agent/risk/portfolio remain authority.
- Role split:
  - NeoWave → context  
  - S&D → location  
  - Liquidity → catalyst / target  
  - PA → trigger  
  - Indicators → state corroboration (e.g. **CCI extremes help liquidity sweep quality**)  
- Missing evidence stays **neutral**; conflict **damps** rather than reverse-signals by default.
- Soft influence first; hard filters only after research.

### Indicator pairing examples
- Liquidity + CCI: stretch into raid / exhaustion after sweep  
- S&D + CCI/RSI: quality of zone touch  
- PA + RSI/BB: trigger quality  
- NeoWave + ADX/ER: whether the wave story fits the regime  

---

## 5. How existing “ML” / calibration fits

### What the codebase actually has
Not deep learning. Statistical layers:

1. **Confidence calibration** (`ConfidenceCalibrator`) — reliability / Brier / expected R by score bucket  
2. **Setup calibration** — which confidence/regime slices are trustworthy  
3. **Meta-model** (`CalibratedSetupMetaModel`) — take / skip / **reduce risk only** (never increase above base) via historical win-rate / expected-R buckets  
4. **Management calibration** — post-entry behaviour cohorts  
5. **Validation** — walk-forward, embargo, Monte Carlo in QuantResearch  

### Intended role
```text
Detectors → evidence
Rules → candidate setup
Calibration / meta-model → should we take it, and how hard? (≤ base risk)
Risk / portfolio / execution → final authority
```

ML should **filter and size**, not replace structure or invent entries from raw prices alone. Future feature vectors should log S&D, liquidity, PA, NeoWave, and indicator fields for research.

---

## 6. Key files touched this session

| Area | Files |
|---|---|
| Enable chart S/D & liquidity | `DashboardLive/WorkspaceAnalysis.cs`, `DashboardExporter/Program.cs`, `DashboardLive/SimulationApi.cs` |
| Chart UI | `Dashboard/src/components/AnalysisChart.vue`, `Dashboard/src/styles.css` |
| Live transport | `DashboardLive/LiveSseFrameProjector.cs`, `DashboardLive/Program.cs`, `DashboardLive/BinanceLiveAnalysisService.cs`, `DashboardLive/appsettings.json`, `Dashboard/src/App.vue` |

---

## 7. Open product directions (discussed, not fully built)

1. Explicit **EvidencePacket** model for all disciplines + indicators.  
2. **Confluence composer** (location + trigger required; catalyst/state boost confidence).  
3. Research logging for meta-model features including S&D/liquidity/CCI.  
4. Optional soft modes for S&D/liquidity after held-out validation.  
5. Optional hard confluence filters only if expectancy is proven.  
6. Keep structural management **entry-pinned and opt-in**.

---

## 8. One-line takeaway

> **Structure and indicators observe; progressive setup decides; calibration may veto or shrink risk; live charts must ship thin payloads so the browser can actually load them.**
