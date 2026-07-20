# Structural Agent Improvement — P0 Completion Report

Date: 2026-07-19

## Status

P0 is implemented and verified. P1–P3 were intentionally not started because the implementation prompt says to stop after P0 unless the user requests the next package.

## Behavior changes

- Experiment evaluation and calibration-learning backtests now default market replay capture off. Both paths expose an explicit operational opt-in that stays outside profile/content hashes. Interactive single runs still default replay on through `BacktestRequest.CaptureMarketReplay = true`.
- Replay-on runs retain at most 250 market rows per chunk and use immutable replay-only copies capped to recent swings, supply/demand zones/events, liquidity pools/events/sweeps, and price-action diagnostics. Engine analysis capacity is unchanged.
- `BacktestApplicationService` invokes restart recovery through `ISimulationJobRepository`, not only the file repository. File, Postgres, and dual-write stores mark every non-terminal orphan failed with `HostRestartedWhileRunning` and retain its output/artifact paths.
- Experiment profile groups now default to one. The Dashboard host ceiling remains two profile groups, one historical download per broker, and a CPU-sized strategy-worker pool, so higher concurrency requires an explicit manifest override.
- OANDA and Binance cache progress now reports `ReadingCache`; network activity remains `DownloadingData`. Both still map to the existing `LoadingCache` job-status enum where appropriate.

## Verification

- `dotnet test Simulator.Tests/Simulator.Tests.csproj --no-restore --filter ...` — 30 focused tests passed.
- Replay-off application-service integration test — 1 passed; the run completed with its manifest and no market chunks.
- Combined final simulator selection — 31 passed.
- `dotnet test QuantResearchRunner.Tests/QuantResearchRunner.Tests.csproj --no-restore --filter FullyQualifiedName~CalibrationTrainingPipelineTests` — 4 passed.
- `dotnet build DashboardLive/DashboardLive.csproj --no-restore --verbosity:minimal` — succeeded with 0 warnings and 0 errors; this also compiled the Postgres persistence changes.
- `git diff --check` — clean before the report/checklist update.

## Throughput

Candles/second was not measured in this code-only pass, so no speedup is claimed. Replay removal eliminates chart-grade snapshot serialization and gzip work from the default experiment path, but the prompt's ≥2× target still needs a controlled local benchmark.

## Remaining risks

- Run a real warm-cache Structural experiment to measure candles/second and peak RSS on the target machine.
- Manually inspect a replay-enabled Structural single run to confirm practical chunk sizes with production-like analysis data.
- Postgres recovery compiled successfully and is covered through the repository contract/file implementation; a database-backed restart integration test would add confidence around persisted lifecycle events.
- Startup recovery intentionally fails all non-terminal jobs because resumable simulation checkpoints do not exist yet.

## Recommended next research matrix

Use one profile group, the same instrument/date ranges, warm cache, replay off, and fixed seeds. Compare baseline analysis, supply/demand only, liquidity only, and supply/demand plus liquidity. Record candles/second, peak RSS, elapsed time, trade count, and failure/no-trade reasons. Repeat on held-out ranges before interpreting any profitability difference.
