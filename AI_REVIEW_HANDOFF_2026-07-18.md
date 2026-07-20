# AI review handoff — 2026-07-18

## Review objective

Review the simulation profile, Single Run, and Experiment-panel fixes made on 2026-07-18. Prioritize correctness, data-integrity guarantees, PostgreSQL migration safety, and whether the UI can still create or retain an invalid experiment profile selection.

This is a heavily dirty working tree with many pre-existing modified and untracked files. Do not assume every `git status` entry belongs to this work, and do not discard unrelated changes. Several files in this review are themselves untracked, so `git diff` alone is not sufficient; inspect the listed working-tree files directly.

## Reported failures and symptoms

1. `GET /api/simulation-profiles` returned HTTP 500 because a stored profile failed `ContentHash` verification.
2. Experiment listing failed for experiment `33661def-478a-46a5-9818-3bf0a78bc6a6` because PostgreSQL `jsonb` property reordering changed the serialized representation used by an older hash writer.
3. The Experiment panel appeared stuck at a percentage and did not expose child-agent statistics or obvious report UUIDs.
4. Single Run did not expose the new `StructuralConfluence` agent/profile path.
5. Vue variant creation raised `Failed to execute 'structuredClone' ... Object could not be cloned` because a reactive proxy was being cloned.
6. A run could enable the supply/demand-pullback playbook while supply/demand analysis was disabled. Example investigation: simulation `3c0c32f9-654a-4d50-a62e-7cf44863652b`, configuration hash `c48d87d6b20abc65b2182db10363aa3aa49b1100442e358c33eb5f6cfce294fb`. The playbook remained dormant with no structural zone, producing no trades.
7. There was no obvious way to remove a selected profile from an experiment. Archiving a selected profile also left a stale reference in the draft.
8. Recreating a profile using the name of an archived profile failed with PostgreSQL error `23505`, constraint `ix_experiment_profiles_name`, and surfaced as HTTP 500.

## Implemented changes

### 1. Execution validation for structural profiles

File: `Simulator/Experiments/Models/SimulationStrategyProfile.cs`

- Added `ValidateForExecution()` while retaining the existing storage/hash-oriented `Validate()`.
- Enforces these dependencies:
  - liquidity-sweep reversal requires liquidity analysis;
  - accepted-break retest requires liquidity analysis;
  - supply/demand pullback requires supply/demand analysis;
  - liquidity sweep with supply/demand confluence other than `Disabled` requires supply/demand analysis.
- New profiles and immutable snapshots call execution validation.
- Single Run calls execution validation when resolving a selected profile in `DashboardLive/SimulationApi.cs`.
- Persistence deserialization intentionally still calls only `Validate()`. This keeps old, hash-valid but semantically invalid profiles listable so the whole profile-list endpoint does not fail; launching them is rejected.

Tests: `Simulator.Tests/StructuralExperimentTests.cs` contains incompatible/compatible analyzer-playbook cases.

### 2. Experiment-panel variant creation

Files:

- `Dashboard/src/composables/useSimulationProfiles.ts`
- `Dashboard/src/components/simulator/experiment/ProfileVariantBuilder.vue`

Changes:

- Replaced `structuredClone` of Vue reactive data with a JSON clone helper.
- New structural profiles enable liquidity and supply/demand analysis because their default playbook configuration requires both.
- Variant creation automatically enables the analyzers required by the selected playbook and confluence mode.
- The UI explains that required analyzers are enabled automatically.

Review whether JSON cloning is safe for the current DTO (it is presently JSON-only) and whether future non-JSON values would make this brittle.

### 3. Experiment monitoring, UUIDs, and reports

Files to inspect:

- `Dashboard/src/components/simulator/experiment/ExperimentMonitor.vue`
- `Dashboard/src/composables/useSimulationExperiment.ts`
- `Dashboard/src/components/SimulationExperimentPanel.vue`
- `Dashboard/src/components/SimulatorPanel.vue`
- `Dashboard/src/App.vue`
- `Dashboard/src/types/simulation-experiments.ts`

Changes include:

- Parent experiment polling with `cache: 'no-store'`.
- Evaluation-child polling so agent balance, equity, P/L, trades, positions, win rate, profit factor, average R, drawdown, candle count, and throughput are shown similarly to Single Run.
- Learning and evaluation simulation UUIDs are displayed per profile.
- The experiment UUID is displayed with Copy and Open report actions.
- During Learning, the panel explicitly states that trading metrics begin during held-out Evaluation.

Review timer cleanup, retry behavior, stale child snapshots, whether average parent progress has the intended semantics, and whether polling multiple profiles can create excessive requests.

### 4. Single Run support for immutable profiles and StructuralConfluence

Primary files:

- `DashboardLive/SimulationApi.cs`
- `Dashboard/src/components/SimulatorPanel.vue`

The API resolves an optional profile ID/revision, validates it for execution, resolves its calibration policy/artifacts, and maps `StructuralConfluence` into a backtest request. The dashboard maps the agent kind to the `structural-confluence` strategy identifier.

Review that request fields which must remain run-specific (broker, input source, cache flags, date range) correctly override profile fields, while immutable strategy/analysis/management settings continue to come from the selected revision.

### 5. Legacy experiment hash migration

File: `TradingHub.Persistence.Postgres/Simulation/PostgresSimulationExperimentRepository.cs`

- New writes hash a canonical JSON representation.
- `MarkInterruptedAsync()` detects rows written by the affected pre-canonical runtime writer.
- Before replacing a legacy hash, it validates the relational envelope, timeline/parallelism/ranking structures, run identity sets, and embedded profile hashes.
- Rows that fail those checks still throw instead of bypassing integrity verification.

This area deserves a security/integrity-focused review. In particular, confirm that the legacy acceptance criteria are strict enough and that canonicalization is deterministic for all JSON values used by experiment snapshots.

### 6. Removing and archiving profiles in the panel

Files:

- `Dashboard/src/components/simulator/experiment/StrategyProfileTable.vue`
- `Dashboard/src/components/SimulationExperimentPanel.vue`

Changes:

- A selected row now shows an explicit **Remove from experiment** action.
- The storage action is labelled **Archive stored profile** to distinguish it from draft deselection.
- After a successful archive, every draft reference to that profile ID is removed so an invisible stale selection cannot be launched.

Review baseline behavior when the removed profile was the baseline, and whether the UI should automatically choose another baseline or require an explicit choice.

### 7. Reusing names belonging to archived profiles

Files:

- `DBManager.Postgres/Simulation/SimulationEntities.cs`
- `TradingHub.Persistence.Postgres/Simulation/PostgresSimulationStrategyProfileStore.cs`
- `DashboardLive/SimulationProfileApi.cs`
- `DBManager.Postgres/Migrations/20260718212323_Phase12ActiveSimulationProfileNames.cs`
- `DBManager.Postgres/Migrations/20260718212323_Phase12ActiveSimulationProfileNames.Designer.cs`
- `DBManager.Postgres/Migrations/TradingHubDbContextModelSnapshot.cs`

Changes:

- The unique profile-name index is now partial: `archived_at IS NULL`.
- Creation performs an active-name pre-check for a useful error message.
- A race that still reaches PostgreSQL unique violation `23505` is translated to `InvalidOperationException` and HTTP 409 rather than HTTP 500.
- Clone creation now also maps `InvalidOperationException` to HTTP 409.

Review points:

- Verify the partial-index predicate matches snake-case schema naming.
- Verify exception matching is robust for the Npgsql/EF exception shape.
- Decide whether name uniqueness should be case-sensitive; current PostgreSQL behavior is case-sensitive.
- The migration `Down()` can fail after an archived and active profile acquire the same name, because restoring global uniqueness is then impossible without a data policy. Flag this if reversible migrations are required.

### 8. Replay chunk out-of-memory, UI freeze, and low-throughput failure

Files:

- `DashboardLive/SimulationApi.cs`
- `Dashboard/src/components/SimulatorPanel.vue`
- `Simulator/Replay/ChunkedReplayWriter.cs`
- `Simulator/Models/BacktestConfiguration.cs`
- `Simulator/Engine/StreamingComparativeEngine.cs`
- `Simulator/Services/BacktestApplicationService.cs`
- `QuantResearch.Training/Pipeline/CalibrationTrainingPipeline.cs`
- `Simulator.Tests/StreamingComparativeEngineTests.cs`

The replay chunk endpoints previously decompressed each `.json.gz` file into a complete
`JsonElement` DOM and then serialized that DOM again. Under memory pressure this failed in
`JsonSerializer.DeserializeAsync()` with `Array dimensions exceeded supported range`.

The deeper producer-side problem was confirmed from a live calibration artifact: one 5,000-row
market replay chunk was 617 MB even after gzip compression. Every candle repeated a rich analysis
snapshot containing as many as 500 swing points. Internal calibration runs do not consume chart
replay, so producing, retaining, compressing, and then downloading those snapshots wasted memory,
CPU, disk, and browser work while depressing candles/second.

Changes:

- Market and execution-detail chunk endpoints now serve the existing gzip representation as
  streamed `application/json` with `Content-Encoding: gzip`; browser `fetch().json()` remains
  compatible and the API no longer materializes the chunk.
- The filtered/bounded `/api/simulations/{id}/replay` endpoint now uses
  `DeserializeAsyncEnumerable<JsonElement>()`, so it parses one array element at a time instead
  of loading an entire chunk before applying its 5,000-row response limit.
- Market chunk IDs now use the same strict character validation as execution-detail chunk IDs,
  while retaining support for an optional `.json.gz` suffix.
- Internal calibration/training requests set a non-semantic, `[JsonIgnore]` operational switch to
  skip market replay capture. Trades, execution detail, artifacts, and progress are still emitted;
  ordinary single runs and held-out evaluation still capture chart replay. Excluding the switch
  from JSON also prevents it from changing immutable configuration hashes.
- Captured market replay chunks are capped at 250 rows even when an old profile requests 5,000,
  and replay-only analysis snapshots retain the latest 100 swings. Engine analysis remains full.
- The dashboard now keeps at most 750 chart rows, loads only the newest chunk tail (`take=250`),
  prevents overlapping replay requests, and stops/clears single-run replay polling in Experiment
  mode.
- Interactive tail requests reject historical compressed chunks larger than 64 MiB with HTTP 413
  rather than scanning a known oversized artifact. The unfiltered raw gzip download remains
  available for report/export workflows.

Review HTTP behavior through any production reverse proxy, especially preservation of
`Content-Encoding` and `Vary`. Also consider an integration test with a replay chunk large enough
to prove bounded server memory; no such endpoint test currently exists.

## Verification already completed

- `dotnet test Simulator.Tests/Simulator.Tests.csproj --no-restore --filter "FullyQualifiedName~StructuralExperimentTests" --verbosity minimal`
  - Passed: 19/19.
- `npm run build` from `Dashboard/`
  - `vue-tsc --noEmit && vite build` passed.
- `dotnet build DashboardLive/DashboardLive.csproj --no-restore --verbosity minimal`
  - Passed with 0 warnings and 0 errors after the profile-name and replay-streaming fixes.
- `dotnet test Simulator.Tests/Simulator.Tests.csproj --no-restore --filter "FullyQualifiedName~Application_Service_Can_Complete_Without_Capturing_Market_Replay" --verbosity minimal`
  - Passed: 1/1; verifies completion artifacts and trades remain while no market replay chunk is written.
- `npm run typecheck` from `Dashboard/`
  - Passed after the replay polling/retention changes.
- `git diff --check`
  - Passed for tracked diffs. Remember that several relevant files are untracked.

No full live end-to-end experiment was completed after these changes. The original simulation and existing profile revisions are immutable and are not retroactively repaired.

## Required local database action

The new migration was generated but not applied because `TRADINGHUB_MIGRATOR_CONNECTION` was not present in the coding shell.

One-time migration command:

```bash
TRADINGHUB_MIGRATOR_CONNECTION='Host=localhost;Port=5432;Database=tradinghub;Username=postgres;Password=REDACTED' \
dotnet ef database update \
  --project DBManager.Postgres \
  --startup-project DBManager.Postgres
```

Using the PostgreSQL superuser is acceptable for this one-time local DDL migration, but the application should not run with superuser credentials. Do not place a real password in this document or commit one to Git.

## Known limitations and unresolved risks

1. The original profile `ContentHash does not match the resolved profile` failure has not been solved by silently accepting mismatches. That is intentional: blindly bypassing it would weaken immutable-profile integrity. If it still reproduces, design a similarly constrained legacy migration or quarantine strategy and add persistence integration tests.
2. There is no PostgreSQL integration test yet proving archived-name reuse and active-name conflict behavior.
3. There are no component tests for remove/archive selection behavior or experiment polling cleanup.
4. Existing invalid profiles remain immutable and listable but cannot launch. Users must create a corrected variant/revision and a new experiment/run.
5. A failed learning/calibration phase should prevent held-out evaluation/trading from using an invalid or missing artifact. Review the experiment state transitions to ensure failure cannot silently fall through to live evaluation.
6. The revised replay responses have been build-verified but not exercised through the live dev server or a reverse proxy after restart. Historical chunks over 64 MiB cannot be displayed interactively, although their raw gzip representation can still be downloaded.
7. At diagnosis time, four old `Dashboard.Live` process trees were suspended (`T` state), including two child processes retaining about 19 GB RSS each; no process was listening on port 5180. They were not terminated because they may contain in-memory jobs. The new binaries and memory fixes require gracefully stopping those processes and restarting the dashboard.

## Suggested review sequence

1. Inspect `SimulationStrategyProfile.ValidateForExecution()` and its tests.
2. Trace Experiment and Single Run profile resolution through `DashboardLive/SimulationApi.cs` and the experiment application service.
3. Review canonical hash generation and the constrained legacy migration.
4. Review the partial unique-index migration and conflict translation.
5. Review Vue variant cloning, automatic analyzer enabling, profile removal, and polling lifecycle.
6. Restart the dashboard, run a fresh calibration, and confirm that it creates no `market/chunk-*.json.gz`, memory remains bounded, and throughput recovers.
7. Exercise a normal single-run replay chunk through the live server and verify tail loading plus streaming/proxy headers.
8. Add focused tests for any issue found; avoid unrelated refactors in this dirty working tree.
