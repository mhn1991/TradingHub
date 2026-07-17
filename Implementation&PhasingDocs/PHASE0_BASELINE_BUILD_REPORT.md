# Phase 0 — Baseline Build/Test Report

**Purpose:** Establish the "before" snapshot for the Live OANDA Multi-Agent Production
Implementation Plan's Phase 0 (baseline and contracts). Every later stage in this phase must
leave these numbers unchanged (build clean, same pass/fail/skip counts per test project).

**Commit:** `c34a8ef932726c2ffb379575c0d039e0770b9419` (branch `main`)
**Date:** 2026-07-16T15:29:24Z
**Environment:** `dotnet` SDK targeting `net10.0`, Linux 6.17.0-40-generic

Note: the working tree at this commit already contains uncommitted, in-progress changes from
earlier sessions (§14-17 QuantResearch/calibration/meta-labeling work, Agent decision-quality
pass, etc. — see `git status`). This baseline reflects the actual current working tree, since
that is the codebase Phase 0 is extending, not a clean checkout of `HEAD`.

## Commands run

```
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx --no-restore --configuration Release
dotnet test TradingHub.slnx --no-build --no-restore --configuration Release
```

(Test breakdown below re-run per-project via `dotnet test <project>.csproj` for a clean
per-assembly count — same binaries, same results, clearer attribution.)

## Restore

```
Determining projects to restore...
All projects are up-to-date for restore.
```

## Build

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.26
```

All 22 projects in `TradingHub.slnx` built successfully:
Networking, Brokers, ChartAnnotator, Brokers.IntegrationTests, TradeManager, Agent,
TradingJournal, RiskManager, PortfolioManager, ExecutionManager, TradingCore, Simulator,
DashboardContracts, QuantResearch, Simulator.AotSmoke, DashboardExporter, BacktestRunner,
QuantResearchRunner, DashboardLive, QuantResearchRunner.Tests, TradingHub.UnitTests,
Simulator.Tests.

## Test results (per project)

| Test project | Passed | Failed | Skipped | Total | Duration |
|---|---|---|---|---|---|
| `Simulator.Tests` | 665 | 0 | 0 | 665 | ~19 s |
| `QuantResearchRunner.Tests` | 31 | 0 | 0 | 31 | ~5 s |
| `TradingHub.UnitTests` | 60 | 0 | 0 | 60 | ~737 ms |
| `Brokers.IntegrationTests` | 0 | 0 | 13 | 13 | ~27 ms |

**Total: 756 passed, 0 failed, 13 skipped, 769 total.**

`Brokers.IntegrationTests` is skipped in full — these are network-dependent tests against real
broker endpoints (OANDA practice pricing stream, candle multi-timeframe reads, open
orders/positions reads) and are expected to skip in this sandboxed environment with no network
access to the broker. This is pre-existing, unrelated to Phase 0.

## Acceptance bar for the rest of Phase 0

Every stage from here on must reproduce exactly:
- Build: 0 warnings, 0 errors, all 22 (+ new Phase 0 projects, additively) projects succeed.
- `Simulator.Tests`: 665/665 passed, 0 failed, 0 skipped (no regressions — new tests may raise
  this number, nothing may lower the passed count or add failures).
- `QuantResearchRunner.Tests`: 31/31 passed.
- `TradingHub.UnitTests`: 60/60 passed.
- `Brokers.IntegrationTests`: 13 skipped is expected and acceptable (network-gated).
