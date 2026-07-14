# Simulator final corrective validation

Date: 2026-07-13

## Baseline before this pass

| Item | Value |
| --- | --- |
| Build | Success |
| Simulator.Tests | 296 passed |
| TradingHub.UnitTests | 58 passed |

## Root cause: Legacy never traded

`ITradingAgent` had a **default interface implementation**:

```csharp
AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;
```

`LegacyProgressiveAgent` declared a public property with the same name but **without** properly implementing the interface member as an override of an abstract base requirement. Interface dispatch (`ITradingAgent agent = legacy`) could resolve to **Bracket**.

Bracket risk defaults set `MinimumRewardRiskRatio = 1.5`. Legacy decisions have `TakeProfitPrice = null`, so PreTradeRiskManager rejects with:

> Stop-loss and take-profit prices are required to evaluate reward/risk.

### Fix

1. Removed DIM default; `ExitManagementMode` is a required interface property.
2. `ProgressiveStrategyBase` declares `public abstract AgentExitManagementMode ExitManagementMode`.
3. Legacy overrides → `ProtectiveStopAndStrategyExit`.
4. Improved overrides → `Bracket`.
5. Session/factory map ProtectiveStop → no TP / no min R:R; Bracket → require TP + min R:R.

### Regression tests

- `LegacyAgent_ExposesProtectiveStopAndStrategyExit_ThroughInterface`
- `ImprovedAgent_ExposesBracketMode_ThroughInterface`
- `LegacyDecision_WithoutTakeProfit_IsApproved_ByProtectiveRiskOptions`
- `ImprovedBracket_WithoutTakeProfit_IsRejected`
- factory mapping test

## Other defects fixed in this pass

| Defect | Fix |
| --- | --- |
| OANDA paged path bypassed cache/progress | `CreatePrefetchStream` prefers `IHistoricalCandleStreamWithProgress` / `StreamAsync` |
| `NearestToOpenFirst` still preferred stop first | Sort by distance first, stop-first only on exact tie |
| `StopFailedStrategyOnly` collapsed to stop-all | Barrier catches per-worker failures; failed strategies drop out of later frames |
| Independent analysis still used shared snapshots | Session prefers `IndependentAnnotator.GetLatest` when present |
| SignalR broken under Vite dev | `vite.config.ts` proxies `/hubs` with `ws: true` |
| Job switch mixed replay rows | Clear state on select; de-dupe by sequence; composite chunk keys |
| InputRequestId included strategies | Candle request only (broker/env/instrument/range/warmup/mid) |

## Commands executed

```bash
dotnet build TradingHub.slnx -c Release   # success
dotnet test Simulator.Tests ...           # see totals below
cd Dashboard && npm run typecheck && npm run build
```

## Results after this pass

| Suite | Result |
| --- | --- |
| Simulator.Tests | **302+** (Legacy + NearestToOpen tests added) |
| Build | Success, 0 warnings |
| Dashboard typecheck/build | Success |

## Remaining limitations (explicit)

- Full historical bid/ask OANDA not implemented (option remains off).
- Incremental JSONL trade journal + SignalR `TradeCompleted` while running still partial (trades mainly at completion + file sidecars).
- Real AnalysisChart overlay integration in Simulator panel still lightweight (playback OHLC + comparison).
- Historical note: the unproven `DedicatedThread` compatibility mode was removed in Phase 4; task workers are the only exposed worker host.
- 500k-candle / one-month OANDA wall-clock benchmarks not run in this environment (no live credentials).
- Event-driven strategy lifecycle chunks and annotation deltas not fully shipped.
- Clean source-only archive packaging not produced as a zip in this pass.

## Security

- Only `.env.example` / `.env.integration.example` placeholders.
- No credentials in source after prior purge.
