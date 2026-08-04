# TradingHub — instructions for Claude Code

## Keep PROJECT_STATE.md current

`PROJECT_STATE.md` (repo root) is the living source of truth for what's implemented, what's
broken/missing, current financial/quant validation results, and the roadmap. It exists because
this repo has ~55 other markdown files (audit reports, blueprints, implementation prompts) that
are point-in-time snapshots, some now stale or superseded.

**Whenever you touch a file in this repo — code, config, or docs — check whether
`PROJECT_STATE.md` needs updating, and update it in the same session if so.** Concretely:

- Fixed a bug, changed behavior, or finished a feature in a subsystem `PROJECT_STATE.md` §2
  describes? Update that entry.
- Ran a backtest, walk-forward validation, calibration, or any other quant experiment with a real
  result? Update §3 and add a one-line entry to the session log at the bottom.
- Found that something in an older `.md` file (blueprint, audit report, etc.) no longer matches
  reality? Correct it in `PROJECT_STATE.md` with an explicit note (don't silently trust an old doc,
  and don't rewrite the old doc unless asked — `PROJECT_STATE.md` is the override layer, not a
  replacement for the historical record).
- Closed or discovered a governance/packaging gap (§4)? Update it.
- Made an architectural decision worth remembering? Note it, with the reasoning, not just the
  outcome — future sessions (and future you) need the "why."

Keep entries evidence-based: cite file:line, a test result, or a concrete command output. If you
haven't verified something, say so explicitly rather than assuming an existing doc is current — a
memory or doc claim that a file/function "exists" is not the same as confirming it still does.

Do not let this turn into a second sprawling document. Prefer editing existing sections over
appending; keep the session log short-lived (fold entries into the numbered sections above once
they're no longer "recent").

## Repo-specific notes learned the hard way this session

- **Checking PostgreSQL state**: a plain `psql \dt` only lists the `public` schema and will
  incorrectly report "no tables." This repo's real tables live in `analytics`, `config`,
  `decision`, `execution`, `management`, `operations`, `reference`, `research`, `risk`, `runtime`,
  `security`, `simulation`. Query `pg_tables` across all non-system schemas instead.
- **A running .NET process is unaffected by rebuilding its source/DLL on disk.** Linux keeps a
  running process's already-loaded assembly available via its open file handle even after the
  file on disk is replaced. This is safe to rely on when iterating on code while a long-running
  backtest/simulation is active — but it also means you must rebuild *and restart* to actually
  test a change, not just rebuild.
- **`ChartAnnotator/Liquidity/LiquidityAnalyzer.cs` and `SupplyDemand` pool-lifecycle code are
  unusually bug-prone.** Two distinct subtle correctness bugs have been found in this exact area
  across different sessions (a FIFO-queue trimming bug; a merge-mutation index-sync bug). The
  existing unit test suite passed identically with and without the second bug present — **unit
  tests alone are not sufficient verification for changes here.** Any future change to pool
  creation/merge/trim logic needs a real-data trade-count A/B comparison (same window, old code
  vs. new code) before being trusted.
- **`/tmp` is on a filesystem that can and did fill to 100%** during a long-running experiment
  (a single scenario generated 58GB of market-replay/execution-detail output). Put scratch/temp
  work for anything long-running or data-heavy on `/mnt/storage` instead (1.9TB free at last
  check), not `/tmp`.
