# Comparative OANDA backtesting (streamed)

## Strategies

- `LegacyProgressiveAgent` — 1h → 15m → 5m entry, protective ATR stop, reverse-setup or stop exit.
- `ImprovedProgressiveAgent` — same scoped entry flow with structural stop/target and minimum R:R.

Each strategy runs in an isolated `StrategySimulationSession` (account, orders, positions, risk, journal) against the **same** immutable `MarketFrame` stream.

## Architecture highlights

- Dashboard-first jobs via `IBacktestApplicationService` (shared with CLI).
- Streamed 1m candles (OANDA paging + compressed cache + bounded prefetch).
- Shared annotation snapshots by default; optional independent engines.
- Sequential or parallel strategy workers with a per-frame barrier.
- Warm-up before evaluation start.
- Chunked market/strategy replay under `simulations/{id}/`.
- File-backed job repository for refresh-safe progress.

See `ARCHITECTURE_SIMULATOR.md` and `RUN_BACKTEST.md`.
