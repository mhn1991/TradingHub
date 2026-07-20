# Per-Market/Per-Timeframe Indicator Calibration — Implementation Plan

**Status: draft, awaiting your approval before implementation starts.** Plan mode ended before I could route this through the normal approval gate, so this document takes its place — please review before I write any code.

## Context

Every structural playbook and the Legacy/Improved progressive strategies currently use one fixed set of indicator thresholds (`MinimumAdx`, RSI overbought/oversold bounds, ATR-based stop/target multiples, confidence floors, etc.) regardless of which instrument or timeframe they're trading. Market behavior differs by instrument and timeframe, so a threshold tuned (by hand, historically) for one context is not necessarily right for another. This session's Simulator audit already surfaced a concrete symptom: `IndicatorConfluencePlaybook` has a demonstrated negative edge (232 trades, avg R -0.47) with its current static thresholds.

The goal: build a calibration engine that searches each strategy's indicator/signal thresholds per (instrument-group, timeframe) and, where justified, per individual instrument, validates candidates strictly on held-out data, and produces a versioned, promotable artifact — never auto-live.

## Decisions already made (from our discussion)

- **Scope**: curated manifest of entry/confirmation/volatility/stop/target parameters per strategy. Risk limits, account protection, portfolio constraints, execution/broker/safety/concurrency/operational settings are explicitly out of scope — never discovered via reflection, never calibrated.
- **Booleans**: separate ablation manifest (on/off comparison), not folded into the numeric grid.
- **Search**: deterministic multi-pass coordinate descent — sensitivity screening → multi-pass descent over influential parameters only → capped small interaction-group searches (≤2 params by default, 3 requires explicit opt-in) → local refinement → walk-forward validation with fold-stability aggregation (never "last fold wins"). Multiple deterministic starting points (default/conservative/permissive).
- **Asset-class grouping**: reuse `Brokers.Models.InstrumentGroupResolver.Resolve(instrument)` (confirmed: it returns the instrument-key prefix before `:`, e.g. `FX`, `CRYPTO` — a broker/market-type grouping, not majors-vs-minors) behind a new `ICalibrationInstrumentGroupResolver` abstraction, so the search engine isn't coupled to correlation/risk-clustering semantics and the grouping can be refined later without touching the engine.
- **Fallback hierarchy**: global strategy default → instrument-group + timeframe override → instrument + timeframe override. An instrument-specific override is only created when it beats the group/timeframe configuration consistently and materially on unseen folds — never merely by training score.
- **Delivery order**: engine scaffolding → `IndicatorConfluencePlaybook` → `LiquidityBreakRetestPlaybook` → Improved progressive → Legacy progressive (last, unmodified by default) → remaining structural playbooks only after the engine/schema are proven. Each strategy ships as a complete vertical slice (manifest → search → validation → artifact → CLI/API → tests) before the next one starts.

## Architecture placement

New code lives in **`Simulator/Experiments/IndicatorCalibration/`** (new subfolder of the already-audited `Simulator/Experiments` namespace), not in `QuantResearch.Training`. Reasoning: this is fundamentally "run many backtests via the existing engine, pick the best validated one" — the same shape as `Simulator/Experiments/*`'s existing Learn→Freeze→Evaluate→Rank machinery (`SimulationExperimentTimeline`, `BacktestSimulationExperimentExecutor`, `SimulationExperimentApplicationService`, `SimulationExperimentRanker`, all read in detail during this session's audit), just with the engine itself generating candidates instead of a caller supplying them. It reuses `BacktestApplicationService`/`SimulationFactory` for individual backtest runs and the existing `Calibration/` artifact-repository pattern (`ICalibrationArtifactRepository`, `FileCalibrationArtifactRepository`, `TradingHub.Persistence.Postgres/Calibration/PostgresCalibrationArtifactRepository`) for persistence — extended with a new artifact type, not replaced.

This is distinct in kind from the existing Setup/Management/MetaModel calibrators (statistical bucket-fitting over already-computed features) and from `QuantResearch.Training`'s pipelines (which drive those). No new project reference to `QuantResearch.Training` is needed.

## Data contracts

### 1. Manifest (versioned, hand-authored, checked into the repo — never reflection-discovered)

```csharp
namespace Simulator.Experiments.IndicatorCalibration;

public enum CalibrationParameterCategory { Entry, Confirmation, Volatility, Stop, Target, Management }
public enum CalibrationValueKind { Ratio, AtrMultiple, Percentage, AbsolutePrice, Count }
public enum CalibrationSpacing { Linear, Logarithmic, Discrete }

public sealed record CalibrationConstraint(string Id, string Expression, string Description);
// Expression is a small closed vocabulary evaluated against the candidate's named parameters,
// e.g. "MinimumRsiForSell < MaximumRsiForBuy", "StopAtr < TargetAtr" — not arbitrary code.

public sealed record CalibrationParameterEntry
{
    public required string ParameterId { get; init; }          // stable id, e.g. "indicator-confluence.minimum-adx"
    public required string PropertyPath { get; init; }         // "IndicatorConfluence.MinimumAdx" — reflection SET target only, never discovery
    public required CalibrationParameterCategory Category { get; init; }
    public required CalibrationValueKind ValueKind { get; init; }
    public required decimal DefaultValue { get; init; }
    public required decimal HardMinimum { get; init; }
    public required decimal HardMaximum { get; init; }
    public required IReadOnlyList<decimal> CoarseGrid { get; init; }   // explicit values, not auto-generated by default
    public required decimal RefinementStep { get; init; }
    public required CalibrationSpacing Spacing { get; init; }
    public decimal ConservativeStartingValue { get; init; }
    public decimal PermissiveStartingValue { get; init; }
    public int SearchOrder { get; init; }                       // deterministic tie-break; sensitivity screening may re-derive at runtime, but the manifest's declared order is the recorded fallback
    public string? InteractionGroupId { get; init; }
}

public sealed record CalibrationInteractionGroup
{
    public required string GroupId { get; init; }
    public required IReadOnlyList<string> ParameterIds { get; init; }  // 2 by default; 3 requires ExplicitOptIn = true
    public bool ExplicitOptIn { get; init; }
}

public sealed record CalibrationAblationEntry
{
    public required string ParameterId { get; init; }
    public required string PropertyPath { get; init; }          // bool property
    public required bool DefaultValue { get; init; }
}

public sealed record IndicatorCalibrationManifest
{
    public required int SchemaVersion { get; init; }
    public required string ManifestVersion { get; init; }        // e.g. "indicator-confluence-manifest-v1", bumped on any structural change
    public required string StrategyId { get; init; }             // TradingAgentTypeIds.Format(kind), or kind+playbookId for structural
    public required IReadOnlyList<CalibrationParameterEntry> Parameters { get; init; }
    public required IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; init; }
    public required IReadOnlyList<CalibrationAblationEntry> AblationFlags { get; init; }
    public required IReadOnlyList<CalibrationConstraint> Constraints { get; init; }

    public void Validate();  // property-path resolvability against the real options type (via a bounded, whitelisted reflection SET helper),
                              // min<=default<=max, coarse grid within bounds, interaction groups reference declared parameter ids,
                              // 3+ member groups require ExplicitOptIn, duplicate ids rejected.
}
```

Manifests are plain C# factories (e.g. `IndicatorConfluenceCalibrationManifest.V1`) returning a `IndicatorCalibrationManifest`, unit-tested directly — not loaded from external config, so they get compile-time property-path checking via a small reflection-based `Validate()` that resolves each `PropertyPath` against the real options record and fails fast on typos or renamed fields.

**Draft manifest for `IndicatorConfluenceOptions`** (first vertical slice):

| ParameterId | Path | Category | Kind | Default | Range | Coarse grid | Interaction group |
|---|---|---|---|---|---|---|---|
| `minimum-adx` | `MinimumAdx` | Entry | Ratio | 20 | 10–40 | 15,20,25,30,35 | `adx-strengthening` |
| `maximum-rsi-for-buy` | `MaximumRsiForBuy` | Confirmation | Ratio | 70 | 55–85 | 60,65,70,75,80 | `rsi-band` |
| `minimum-rsi-for-sell` | `MinimumRsiForSell` | Confirmation | Ratio | 30 | 15–45 | 20,25,30,35,40 | `rsi-band` |
| `minimum-confidence` | `MinimumConfidence` | Entry | Percentage | 55 | 40–70 | 45,50,55,60,65 | — |
| `stop-atr` | `StopAtr` | Stop | AtrMultiple | 1.5 | 0.75–3.0 | 1.0,1.25,1.5,2.0,2.5 | `stop-target` |
| `target-atr` | `TargetAtr` | Target | AtrMultiple | 3.0 | 1.5–6.0 | 2.0,2.5,3.0,4.0,5.0 | `stop-target` |

Ablation flags: `require-trend-strengthening` (`RequireTrendStrengthening`), `require-squeeze-breakout` (`RequireSqueezeBreakout`).
Interaction groups: `stop-target` = {stop-atr, target-atr} (2 params, default-enabled); `rsi-band` = {maximum-rsi-for-buy, minimum-rsi-for-sell} (2 params, default-enabled); `adx-strengthening` pairs `minimum-adx` with the `require-trend-strengthening` ablation flag (treated as a 2-value dimension in the interaction search, not the coordinate-descent grid).
Constraints: `MinimumRsiForSell < MaximumRsiForBuy` (mirrors the existing `IndicatorConfluenceOptions.Validate()` invariant), `StopAtr > 0 && TargetAtr / StopAtr >= root.MinimumRewardRisk` (mirrors reward:risk gating already enforced elsewhere in this codebase).

The `LiquidityBreakRetestPlaybook` manifest (second slice) follows the same shape over `MinimumAdx`, `MinimumEfficiencyRatio`, `MinimumDisplacementAtr`, `MaximumRetestDistanceAtr` — drafted in detail when that slice starts, not enumerated fully here.

### 2. Instrument grouping abstraction

```csharp
namespace Simulator.Experiments.IndicatorCalibration;

public interface ICalibrationInstrumentGroupResolver
{
    string ResolverVersion { get; }              // recorded on every artifact
    string Resolve(InstrumentKey instrument);
}

public sealed class DefaultCalibrationInstrumentGroupResolver : ICalibrationInstrumentGroupResolver
{
    public string ResolverVersion => "instrument-group-resolver-v1";
    public string Resolve(InstrumentKey instrument) => Brokers.Models.InstrumentGroupResolver.Resolve(instrument);
}
```

### 3. Experiment request / run state (mirrors `SimulationExperimentRequest`/`SimulationExperimentManifest` shape from the audited `Simulator/Experiments/*`)

```csharp
public sealed record IndicatorCalibrationRequest
{
    public required string StrategyId { get; init; }
    public required string ManifestVersion { get; init; }
    public required InstrumentKey Instrument { get; init; }          // instrument-level run
    public string? InstrumentGroupOverride { get; init; }            // null => resolved via ICalibrationInstrumentGroupResolver
    public required BarInterval ExecutionInterval { get; init; }
    public required BarInterval AnalysisBaseInterval { get; init; }
    public required SimulationExperimentTimeline Timeline { get; init; }  // reused as-is: LearningFrom/To, EvaluationFrom/To, EmbargoDays
    public int InternalFolds { get; init; } = 5;                     // mirrors CalibrationExperimentPolicy.InternalFolds default
    public required int RandomSeed { get; init; }                    // recorded even though today's search is fully deterministic
    public CalibrationEvaluationBudget Budget { get; init; } = new();
}

public sealed record CalibrationEvaluationBudget
{
    public int MaximumEvaluationsPerFold { get; init; } = 400;
    public int MaximumInteractionCombinationsPerGroup { get; init; } = 49;   // 7x7 ceiling for a 2-param group
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromHours(6);
    public CalibrationBudgetOverflowPolicy OnOverflow { get; init; } = CalibrationBudgetOverflowPolicy.Reject;
}

public enum CalibrationBudgetOverflowPolicy { Reject, ReduceRefinement, ReduceStartingPoints, SkipLowerPriorityInteractions }
```

`IndicatorCalibrationRunState` (the resumable/persisted execution record, one per fold × starting point) records every reproducibility field called out below, plus a `Stage` enum (`SensitivityScreening → CoordinateDescent → InteractionSearch → LocalRefinement → WalkForwardValidation → Aggregating → Completed/Failed`) and the list of already-evaluated candidates (for resume — see below).

### 4. Artifact schema (new `CalibrationArtifactType.IndicatorParameters`, extends existing `Calibration/` types)

```csharp
// Calibration/CalibrationArtifactModels.cs — add to the existing enum:
public enum CalibrationArtifactType { Setup, Management, MetaModel, IndicatorParameters }

// New file: Calibration/IndicatorParameterCalibrationArtifact.cs
public sealed record CalibratedParameterValue
{
    public required string ParameterId { get; init; }
    public required decimal Value { get; init; }
    public required decimal DefaultValue { get; init; }
    public required string SelectionStage { get; init; }        // which phase produced the final value
    public required decimal TrainingScore { get; init; }
    public required decimal ValidationScoreMedian { get; init; }  // across folds — never "last fold"
    public required decimal FoldConsistencyPercent { get; init; }
}

public sealed record IndicatorCalibrationArtifact
{
    public required int SchemaVersion { get; init; }
    public required string CalibrationId { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ManifestVersion { get; init; }
    public required string CodeRevision { get; init; }           // git SHA, resolved at build/run time — see "reproducibility" below
    public required string Scope { get; init; }                  // "Group" | "Instrument"
    public required string? InstrumentGroup { get; init; }
    public required string InstrumentGroupResolverVersion { get; init; }
    public required string? Instrument { get; init; }            // null for group-level artifacts
    public required string ExecutionInterval { get; init; }
    public required string AnalysisBaseInterval { get; init; }
    public required IReadOnlyList<CalibrationFoldSummary> Folds { get; init; }   // reused type
    public required TimeSpan Embargo { get; init; }
    public required string CandleDataHash { get; init; }         // ties to StreamingCandleCache/StreamingCacheWriter content hash, already audited this session
    public required IReadOnlyList<CalibratedParameterValue> Parameters { get; init; }
    public required IReadOnlyDictionary<string, bool> AblationDecisions { get; init; }
    public required decimal BaselineTrainingScore { get; init; }
    public required decimal BaselineValidationScore { get; init; }
    public required bool ImprovedOverBaseline { get; init; }     // "no improvement" is a valid, expected, recorded outcome
    public required IReadOnlyList<RejectedCandidateRecord> RejectedCandidates { get; init; }  // every rejected change + reason
    public required string SearchAlgorithmVersion { get; init; }
    public required int RandomSeed { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public void Validate();
}

public sealed record RejectedCandidateRecord(
    string ParameterId, decimal AttemptedValue, string Stage, string RejectionReason);
```

`ICalibrationArtifactRepository` gains `StoreIndicatorParametersAsync`/`GetIndicatorParametersAsync`, following the exact pattern already used for Setup/Management/MetaModel in `FileCalibrationArtifactRepository` (content-hash envelope, atomic tmp-then-move, quarantine-on-corrupt) and `PostgresCalibrationArtifactRepository` (new EF migration adding the artifact-type case, no schema redesign — the existing envelope table is already type-polymorphic). Promotion status reuses the existing `CalibrationPromotionStatus` enum (`PendingReview → Approved/Rejected/Superseded`) — **calibration never auto-promotes**; an artifact is always `PendingReview` until an explicit approval step, mirroring the existing `CalibrationBundleCandidate`/`ICalibrationBundleApprovalStore` human-approval workflow already audited this session.

## Algorithm (5 phases, as specified)

Implemented as small, independently testable components under `Simulator/Experiments/IndicatorCalibration/`:

1. **`EvaluationBudgetEstimator`** — computes and logs the expected evaluation count (sensitivity + coordinate descent + interactions + refinement + baseline/validation, per fold, per starting point) *before* any backtest runs. Enforces `CalibrationEvaluationBudget`; on overflow applies `OnOverflow` policy deterministically (never silent truncation) and records what was reduced in the artifact.
2. **`SensitivityScreener`** — for each manifest parameter, holds all others at the starting-point baseline, runs the coarse grid, scores each candidate (see Scoring below), and flags negligible/unstable parameters for exclusion from coordinate descent. Determines the runtime search order (most→least influential) as a fallback to the manifest's declared `SearchOrder`; the order actually used is recorded on the artifact.
3. **`CoordinateDescentEngine`** — 2–4 passes over the influential-only parameter set in the resolved order; each pass sweeps one parameter's coarse grid holding the current best values for all others; stops early when a full pass produces no material change. Runs once per starting point (default / conservative / permissive), producing up to 3 candidate configurations to compare.
4. **`InteractionSearchEngine`** — for each declared `CalibrationInteractionGroup` (2-param by default, 3-param only if `ExplicitOptIn`), runs a small joint grid seeded around each starting point's coordinate-descent result, capped by `MaximumInteractionCombinationsPerGroup`.
5. **`LocalRefiner`** — narrows the grid around each winning value using `RefinementStep`, one more small pass.
6. **`WalkForwardAggregator`** — re-evaluates the finalist candidate(s) across `InternalFolds` purged folds (reusing `SimulationExperimentTimeline`'s embargo-gap discipline per fold) and computes: median/robust risk-adjusted score, drawdown compliance, minimum trade count, percentage of acceptable folds, parameter-selection stability across folds, degradation vs. training, and performance vs. the broader fallback (group/default) configuration. Selection happens only on training folds; the held-out fold's result is never fed back into that fold's own selection.
7. **`AcceptanceGate`** — applies the eligibility gates (min trades, max drawdown, data-quality validity, min fold consistency) then the tie-break order (existing/default → simpler/fewer overrides → plateau center → more conservative → deterministic property ordering). Baseline (existing config) is always evaluated and compared against; "no improvement" is accepted and recorded, not treated as a failure.
8. **`CandidateCache`** — content-addressed cache keyed on `(effective full options record, candle data hash, evaluation window)`; a `BacktestSimulationExperimentExecutor`-style single-instrument backtest run is the unit of work, dispatched through a bounded-concurrency scheduler (mirrors `SimulationResourceGovernor`, already audited this session) with deterministic reduction — concurrency changes throughput, never which candidate wins.

**Scoring**: eligibility gates first (min trades, max drawdown, data quality, fold consistency), then rank by a configured risk-adjusted objective (default: median R-multiple-based expectancy, drawdown-penalized — exact formula finalized in the manifest, not hardcoded into the engine, so different strategies can weight differently without an engine change), then the tie-break order above.

## Reproducibility & resume

Every `IndicatorCalibrationRunState` records: strategy id + `StrategyVersion`, `ManifestVersion`, `CodeRevision` (git SHA — new small `GitRevisionResolver` utility reading `Assembly.GetExecutingAssembly()`'s informational version or shelling `git rev-parse HEAD` at startup, cached process-wide), instrument + resolved group + resolver version, execution/analysis intervals, candle-data hash (reusing the existing `StreamingCandleCache`/manifest content-hash mechanism), walk-forward windows, warmup boundaries (via the already-audited `AnalysisWarmupPlanner`), starting configuration(s), resolved parameter search order, coarse/refinement grids actually used, the scoring formula identifier, constraints, random seed, and every accepted *and* rejected candidate with its reason.

Resume: the run state persists incrementally (same atomic-write pattern as `FileSimulationJobRepository`/`FileCalibrationExperimentRepository`, one record per completed phase-step) keyed by a deterministic step id (`stage:parameter:candidate-index:fold`). On resume, already-completed steps are skipped by re-deriving the same step ids from the same recorded inputs and consulting the `CandidateCache`; a resumed run produces the same result as an uninterrupted one because nothing in the algorithm depends on wall-clock time or non-recorded randomness.

## Decision-time consumption

`BacktestRequest.ResolveAgentDefinition(strategyType, definitionOverride, optionsOverride)` (`Simulator/Models/BacktestConfiguration.cs:612`) is the single shared resolution path used by both simulation and live policy promotion (per its own doc comment — a hard invariant not to bypass). Add an optional `IndicatorCalibrationOverlay` parameter: when present, after resolving the base `TradingAgentDefinition`, apply the hierarchy (default already baked in → group+timeframe artifact if present → instrument+timeframe artifact if present, only if `Scope=Instrument` and it beat the group artifact) by setting each calibrated parameter's `PropertyPath` onto the resolved options record via `with`-expression reflection (the same bounded, whitelisted path-resolution helper the manifest's own `Validate()` uses — never arbitrary property access). `StrategyInstrumentAssignment` gains an optional `IndicatorCalibrationArtifactId`/`IndicatorCalibrationArtifactIds` field so a request can pin specific approved artifacts, mirroring `SetupCalibrationArtifact`/`ManagementCalibrationArtifact`/`MetaModelArtifact`'s existing per-assignment wiring. **Never automatic** — an artifact only takes effect when explicitly referenced by id, matching "calibration must never auto-promote into live trading."

## Persistence changes

- `Calibration/CalibrationArtifactModels.cs`: add `IndicatorParameters` to `CalibrationArtifactType`.
- `Calibration/IndicatorParameterCalibrationArtifact.cs`: new file (artifact + `CalibratedParameterValue` + `RejectedCandidateRecord`, as above).
- `Calibration/ICalibrationArtifactRepository.cs`: add `StoreIndicatorParametersAsync`/`GetIndicatorParametersAsync`.
- `Calibration/FileCalibrationArtifactRepository.cs`: implement the two new methods (same envelope/hash/quarantine pattern as the three existing ones — no structural change to the file format).
- `TradingHub.Persistence.Postgres/Calibration/PostgresCalibrationArtifactRepository.cs`: implement the two new methods; new EF Core migration (the existing artifact table is already type-polymorphic per the audit of this area, so this should be an additive migration, not a redesign — confirmed during implementation, not assumed).
- New `Simulator/Experiments/IndicatorCalibration/Persistence/` for the resumable run-state store (file-backed first, following `FileSimulationExperimentRepository`'s exact pattern; Postgres backing deferred until after the first vertical slice proves the shape, consistent with "engine/schema validated before extending further").

## CLI / API surface

- New `IIndicatorCalibrationApplicationService` (mirrors `ISimulationExperimentApplicationService`, already audited): `StartAsync(IndicatorCalibrationRequest)`, `GetAsync(id)`, `ListAsync`, `PauseAsync`/`ResumeAsync`/`CancelAsync` (reusing `AsyncPauseGate`, already audited), plus `GetEvaluationBudgetPreviewAsync(request)` for the upfront estimate.
- `DashboardLive` endpoint group `POST/GET /api/indicator-calibrations` following the exact route/job-lifecycle shape already used for `/api/simulations` (`BacktestApplicationService`) and `/api/experiments`, both audited this session.
- CLI: new command in `TradingHub.AdminCli` (or `QuantResearchRunner`, whichever already hosts calibration-adjacent commands — confirmed during implementation) to submit a request, poll status, and print the budget preview before confirming a run.
- Explicit **promotion** command/endpoint, separate from calibration submission, that flips `PromotionStatus` to `Approved` — never automatic.

## Testing strategy

- Deterministic unit tests per component (`EvaluationBudgetEstimator`, `SensitivityScreener`, `CoordinateDescentEngine`, `InteractionSearchEngine`, `LocalRefiner`, `WalkForwardAggregator`, `AcceptanceGate`, `CandidateCache`) using small synthetic scoring functions (no real backtests) — proves the algorithm's control flow, tie-breaking, budget enforcement, and resume/replay determinism in milliseconds.
- Manifest `Validate()` tests per strategy (property-path resolvability, bounds, interaction-group/constraint consistency).
- One representative end-to-end test per landed strategy: a small synthetic instrument/date-range backtest (matching `Simulator.Tests`' existing `InlineCandles`/`EnumerablePagedCandleSource` fixtures, already used throughout that project) run through the *whole* pipeline (screening → descent → interaction → refinement → walk-forward → artifact), asserting the artifact is produced, promotion status starts `PendingReview`, and a resumed run (killed mid-way, restarted) matches an uninterrupted run byte-for-byte on the decision fields.
- Regression test confirming `ResolveAgentDefinition` without an overlay produces byte-identical output to today (no behavior change for every existing caller that doesn't opt in).

## Rollout order & acceptance criteria

1. **Engine scaffolding** (manifest model, budget estimator, instrument-group abstraction, artifact schema, repository extension, resumable run-state store). *Acceptance*: all component unit tests pass; a manifest can be authored and validated; budget preview computes correctly for a hand-built manifest; no strategy behavior changes yet (nothing consumes an overlay).
2. **`IndicatorConfluencePlaybook`** full vertical slice. *Acceptance*: manifest above lands and validates; one end-to-end synthetic test produces an artifact; `ResolveAgentDefinition` overlay wiring is in place and covered by the no-overlay-regression test; CLI/API can submit/poll/promote a real run against cached OANDA data for at least one FX instrument and one timeframe; the artifact's `ImprovedOverBaseline` field is inspected manually against the known negative-edge result before calling this slice done.
3. **`LiquidityBreakRetestPlaybook`** vertical slice, same acceptance shape, manifest drafted at that point.
4. **Improved progressive strategy** vertical slice.
5. **Legacy progressive strategy** vertical slice — explicitly last; legacy defaults/behavior must be provably unchanged unless calibration is explicitly enabled for it.
6. **Remaining structural playbooks** (`LiquiditySweepReversalPlaybook`, `SupplyDemandPullbackPlaybook`) only after step 2's artifact schema and CLI/API have been exercised against real data and you've reviewed at least one promoted result.

Each step is a separate, reviewable unit of work — I will not move to the next step without checking in.

## Open items I'll confirm during implementation, not blocking approval

- Exact scoring-formula default (median expectancy, drawdown-penalized) — will draft a concrete formula in the manifest for step 2 and show it to you before it's load-bearing.
- Whether the Postgres `PostgresCalibrationArtifactRepository` migration is truly additive or needs schema changes — verified once I'm in that file, not assumed here.
- CLI host project (`TradingHub.AdminCli` vs. `QuantResearchRunner`) — confirmed by checking which already exposes calibration-adjacent commands.
