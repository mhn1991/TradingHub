# TradingHub Indicator Calibration — Final Coding Blueprint

**Status:** Approved implementation blueprint  
**Purpose:** Build a deterministic, leakage-safe, resumable calibration system for strategy indicator thresholds by instrument and timeframe, while preserving every existing strategy default and non-calibrated behaviour.

---

## 1. Non-negotiable principles

1. Existing strategy and indicator defaults remain unchanged in code.
2. Calibration is opt-in and applied only through an explicitly pinned, approved artifact.
3. No artifact is automatically promoted to simulation, demo, or live trading.
4. Calibration searches only a curated, versioned manifest.
5. Risk limits, account protection, execution controls, broker settings, concurrency, retries, and operational settings are never calibrated.
6. Boolean feature switches are tested through ablation, not mixed into numeric coordinate descent.
7. Every search step for a walk-forward fold runs only on that fold's training data.
8. Each selected fold candidate is evaluated once on its held-out fold.
9. Internal walk-forward results are followed by one final untouched external holdout.
10. “No improvement,” “insufficient evidence,” and “unstable result” are valid outcomes.
11. Instrument-level calibration is implemented first.
12. Group-level calibration is disabled until a proper multi-instrument cohort model exists.
13. Search output is a candidate research artifact, not a live configuration.
14. Resume must produce the same decision result as an uninterrupted run.
15. Parallel execution may improve throughput but must never alter candidate selection or tie-breaking.

---

## 2. Default-value and backward-compatibility protection

All existing option defaults, constructors, factories, configuration binders, and strategy resolution paths must remain unchanged.

The calibration implementation must not:

- replace hard-coded defaults with calibrated values;
- change existing constructor defaults;
- silently load an approved or “latest” artifact;
- alter existing simulation requests that do not reference calibration;
- require calibration metadata for existing agents;
- replace successful baseline tests with calibrated tests;
- change legacy behaviour when calibration is disabled.

The configuration resolution order is:

```text
Existing code defaults
    ↓
Existing explicit request/configuration overrides
    ↓
Explicitly pinned approved calibration overlay
```

When no calibration overlay is supplied, the complete resolved options object and runtime decisions must remain equivalent to the pre-calibration implementation.

### 2.1 Required compatibility identity

Every options family used by calibration must expose or produce:

- strategy ID;
- strategy implementation version;
- options schema version;
- default-configuration hash;
- effective timeframe-topology hash.

Each calibration artifact records the baseline identity against which it was produced.

At consumption time, reject the artifact if any required compatibility identity differs. Never partially apply an incompatible artifact.

### 2.2 Required regression tests

For every calibrated strategy:

1. Capture a pre-calibration resolved-options snapshot.
2. Assert that no-overlay resolution matches the snapshot.
3. Assert existing strategy factories return the same defaults.
4. Assert existing successful simulator tests remain unchanged.
5. Assert an unapproved artifact has no effect.
6. Assert an approved but unreferenced artifact has no effect.
7. Assert removing an overlay restores the original configured behaviour.
8. Assert only explicitly declared fields are changed.
9. Assert incompatible artifacts are rejected atomically.
10. Assert Legacy Progressive remains decision-equivalent when calibration is disabled.

Calibration support is not complete until:

```text
No calibration overlay
    =
Pre-calibration strategy configuration and behaviour
```

---

## 3. Initial scope

### 3.1 First implementation scope

The first production slice supports:

- one strategy: `IndicatorConfluencePlaybook`;
- one instrument per calibration request;
- one effective timeframe topology per request;
- numeric parameter calibration;
- boolean ablation;
- internal purged walk-forward validation;
- final external holdout;
- artifact persistence;
- resume;
- CLI and API submission;
- explicit human promotion;
- explicit overlay consumption.

### 3.2 Deferred scope

Do not include in the first slice:

- broad `FX` or `CRYPTO` group artifact generation;
- automatic cohort discovery;
- all-pairs interaction search;
- automatic live activation;
- arbitrary reflection-discovered parameters;
- Bayesian or stochastic optimisation;
- Postgres run-state persistence, unless the file-backed schema has first been proven;
- calibration of risk, portfolio, broker, execution, retry, safety, or operational settings.

---

## 4. Architecture and project placement

### 4.1 Search execution

Place research orchestration under:

```text
Simulator/
  Experiments/
    IndicatorCalibration/
```

This area owns:

- manifests;
- budget planning;
- fold construction;
- sensitivity screening;
- coordinate descent;
- interaction search;
- local refinement;
- candidate evaluation;
- walk-forward aggregation;
- external holdout evaluation;
- experiment ledgers;
- resume orchestration.

### 4.2 Shared contracts

Place runtime-consumed contracts in a project that both simulator and live hosts can reference without making live trading depend on the whole Simulator project.

Recommended location:

```text
Calibration/
  IndicatorParameters/
```

or an equivalent shared `TradingCore`/`Calibration` project.

This shared layer owns:

- approved indicator calibration artifact;
- calibrated parameter overlay;
- compatibility identity;
- promotion event;
- overlay validation;
- strongly typed overlay application contracts.

### 4.3 Persistence

Reuse the existing calibration artifact repository pattern.

Add:

```text
CalibrationArtifactType.IndicatorParameters
```

Use:

- existing file repository pattern for artifact storage;
- existing Postgres artifact envelope if confirmed compatible;
- separate file-backed experiment ledger/run-state repository initially.

The compact approved artifact and the full research ledger must be separate.

---

## 5. Strongly typed manifest model

Do not use strings plus reflection as the main mutation mechanism.

A stable textual property path may be recorded for diagnostics and artifact readability, but application must be compiler-checked.

```csharp
public enum CalibrationParameterCategory
{
    Entry,
    Confirmation,
    Volatility,
    Stop,
    Target,
    Management
}

public enum CalibrationValueKind
{
    IndicatorLevel,
    AtrMultiple,
    Ratio,
    PercentageZeroToOne,
    PercentageZeroToHundred,
    AbsolutePrice,
    Count
}

public enum CalibrationSpacing
{
    Linear,
    Logarithmic,
    Discrete
}

public interface ICalibrationParameterDescriptor<TOptions>
{
    string ParameterId { get; }
    string DiagnosticPath { get; }
    CalibrationParameterCategory Category { get; }
    CalibrationValueKind ValueKind { get; }
    CalibrationSpacing Spacing { get; }

    decimal DefaultValue { get; }
    decimal HardMinimum { get; }
    decimal HardMaximum { get; }
    IReadOnlyList<decimal> CoarseGrid { get; }
    decimal RefinementStep { get; }

    decimal ConservativeStartingValue { get; }
    decimal PermissiveStartingValue { get; }
    int DeclaredSearchOrder { get; }

    decimal Read(TOptions options);
    TOptions Apply(TOptions options, decimal value);
    void ValidateValue(decimal value);
}
```

For nested records, the descriptor applies nested `with` expressions in compiled code.

Example:

```csharp
apply: (root, value) => root with
{
    RsiBollingerSignals = root.RsiBollingerSignals with
    {
        SomeThreshold = decimal.ToDouble(value)
    }
};
```

### 5.1 Boolean ablation descriptor

```csharp
public interface ICalibrationAblationDescriptor<TOptions>
{
    string ParameterId { get; }
    string DiagnosticPath { get; }
    bool DefaultValue { get; }

    bool Read(TOptions options);
    TOptions Apply(TOptions options, bool value);
}
```

### 5.2 Cross-parameter constraints

Use compiled constraint evaluators, not arbitrary expression strings.

```csharp
public interface ICalibrationConstraint<TOptions>
{
    string ConstraintId { get; }
    string Description { get; }
    CalibrationConstraintResult Validate(TOptions options);
}
```

```csharp
public sealed record CalibrationConstraintResult(
    bool IsValid,
    string? RejectionReason);
```

### 5.3 Versioned strategy manifest

```csharp
public interface IIndicatorCalibrationManifest<TOptions>
{
    int SchemaVersion { get; }
    string ManifestVersion { get; }
    string StrategyId { get; }
    string OptionsSchemaVersion { get; }

    IReadOnlyList<ICalibrationParameterDescriptor<TOptions>> Parameters { get; }
    IReadOnlyList<ICalibrationAblationDescriptor<TOptions>> Ablations { get; }
    IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; }
    IReadOnlyList<ICalibrationConstraint<TOptions>> Constraints { get; }

    CalibrationScoringPolicy ScoringPolicy { get; }
    CalibrationAcceptancePolicy AcceptancePolicy { get; }

    void Validate();
}
```

Manifests are explicit C# factories checked into the repository. Reflection must never be used to discover all numeric options.

---

## 6. Initial `IndicatorConfluencePlaybook` manifest

The first slice should include the following numeric candidates, subject to confirmation against the actual option types.

| Parameter | Category | Initial coarse grid | Interaction |
|---|---|---:|---|
| `MinimumAdx` | Entry/confirmation | 15, 20, 25, 30, 35 | ADX × strengthening |
| `MaximumRsiForBuy` | Confirmation | 60, 65, 70, 75, 80 | RSI band |
| `MinimumRsiForSell` | Confirmation | 20, 25, 30, 35, 40 | RSI band |
| `MinimumConfidence` | Entry | 45, 50, 55, 60, 65 | optional confidence pair |
| `StopAtr` | Stop | 1.0, 1.25, 1.5, 2.0, 2.5 | stop × target |
| `TargetAtr` | Target | 2.0, 2.5, 3.0, 4.0, 5.0 | stop × target |

Boolean ablations:

- `RequireTrendStrengthening`;
- `RequireSqueezeBreakout`.

Explicit interaction groups:

- `StopAtr × TargetAtr`;
- `MaximumRsiForBuy × MinimumRsiForSell`;
- `MinimumAdx × RequireTrendStrengthening`.

Do not automatically test all parameter pairs.

### 6.1 Initial constraints

At minimum:

- RSI sell threshold must remain below RSI buy threshold;
- values must remain within hard bounds;
- stop ATR must be positive;
- target ATR must be positive;
- effective target/stop relationship must satisfy the strategy’s minimum permitted reward-to-risk rule;
- percentage scale must be explicit;
- refinement values must stay inside hard bounds.

Invalid candidates are rejected before a backtest is scheduled and recorded in the research ledger.

---

## 7. Request and scope contracts

### 7.1 Instrument-only request for first slice

```csharp
public sealed record IndicatorCalibrationRequest
{
    public required string StrategyId { get; init; }
    public required string ManifestVersion { get; init; }

    public required InstrumentKey Instrument { get; init; }
    public required TimeframeTopology TimeframeTopology { get; init; }

    public required CalibrationTimeline Timeline { get; init; }
    public required int InternalFoldCount { get; init; }
    public required int RandomSeed { get; init; }

    public required CalibrationEvaluationBudget Budget { get; init; }
    public required string BaselineConfigurationHash { get; init; }
}
```

`TimeframeTopology` must include the complete effective setup:

- execution interval;
- setup interval;
- confirmation intervals;
- higher-timeframe trend intervals;
- management intervals;
- aggregation/alignment policy;
- warm-up requirements;
- stable topology hash.

### 7.2 Group scope deferred

Do not produce a group artifact from a single instrument.

A future group request must contain an explicit deterministic cohort:

```csharp
public sealed record CalibrationInstrumentCohort
{
    public required string CohortId { get; init; }
    public required string CohortVersion { get; init; }
    public required IReadOnlyList<InstrumentKey> Instruments { get; init; }
}
```

A group score must aggregate per-instrument evidence, not pooled trade volume alone.

Until that is implemented, `Scope` is always `Instrument`.

---

## 8. Timeline and leakage-safe validation

Use two nested validation levels.

```text
Complete historical request
├── Learning period
│   ├── Internal fold 1
│   │   ├── Training: complete search
│   │   └── Validation: evaluate selected candidate once
│   ├── Internal fold 2
│   │   ├── Training: complete search
│   │   └── Validation: evaluate selected candidate once
│   └── ...
└── Final external holdout
    └── Evaluate the final aggregated candidate once
```

For every internal fold, run entirely within the fold’s training range:

1. sensitivity screening;
2. runtime search-order selection;
3. multi-start coordinate descent;
4. declared interaction searches;
5. local refinement;
6. fold candidate selection.

Then evaluate that fold candidate once on the held-out fold.

The fold validation result must not alter:

- that fold’s parameter screening;
- parameter order;
- grids;
- interaction selection;
- refinement;
- selected fold candidate.

After all folds:

1. aggregate fold evidence;
2. derive one stable final parameter configuration;
3. rerun it over the complete learning period for reporting only;
4. evaluate it once on the untouched external holdout;
5. run final acceptance gates.

The final holdout is never used for parameter selection or refinement.

---

## 9. Search algorithm

### 9.1 Phase 0 — baseline evaluation

For every fold and the final external holdout:

- evaluate the existing effective configuration;
- record its full configuration hash;
- retain all current explicit overrides;
- never substitute manifest defaults for the actual configured baseline.

The calibrated candidate competes against the real existing configuration.

### 9.2 Phase 1 — sensitivity screening

For each numeric parameter:

1. hold all other values at the current starting configuration;
2. evaluate the explicit coarse grid;
3. calculate influence and stability;
4. classify the parameter as:
   - influential;
   - negligible;
   - unstable;
   - invalid due to insufficient evidence.

Record the reason for every excluded parameter.

Runtime ordering may place the most influential parameters first. The final order must be deterministic, with manifest order as the fallback.

### 9.3 Phase 2 — multi-start coordinate descent

Run from deterministic starts:

1. current effective/default configuration;
2. conservative manifest start;
3. permissive manifest start.

For each start:

- process influential parameters in deterministic order;
- evaluate 5–7 values per parameter;
- hold all other parameters at the current best;
- apply eligibility gates before scoring;
- accept a change only if it exceeds the minimum material-improvement threshold;
- run 2–4 passes;
- stop when a complete pass makes no accepted change or improvement falls below the convergence threshold.

### 9.4 Phase 3 — declared interaction search

Search only explicit interaction groups.

Default:

- two dimensions maximum;
- three dimensions require explicit opt-in;
- every group has a hard combination cap;
- boolean ablation may form one dimension.

Seed interaction search around coordinate-descent winners.

No automatic all-pairs search.

### 9.5 Phase 4 — local refinement

For each winning numeric value:

- generate a narrow bounded grid around the winner;
- use the descriptor’s refinement step;
- reject out-of-range candidates before scheduling;
- preserve deterministic candidate ordering;
- prefer a stable local plateau over an isolated spike.

### 9.6 Phase 5 — fold candidate selection

Apply:

1. eligibility gates;
2. absolute quality floor;
3. material improvement over baseline;
4. deterministic tie-breaking.

Fold result states include:

- `Improved`;
- `NoImprovement`;
- `InsufficientEvidence`;
- `Unstable`;
- `FailedAbsoluteQualityGate`;
- `BudgetRejected`;
- `FailedDataQuality`;
- `Cancelled`.

### 9.7 Phase 6 — cross-fold aggregation

Do not use the last fold’s winner and do not blindly average numeric values.

For each parameter:

1. collect acceptable values or acceptable plateau ranges per fold;
2. calculate cross-fold support;
3. identify stable overlapping ranges;
4. choose the value with the highest support;
5. prefer the centre of a stable plateau;
6. prefer the existing value when evidence is weak;
7. prefer conservative geometry when still tied.

If folds disagree materially, classify the parameter or entire candidate as unstable and retain the existing value.

### 9.8 Phase 7 — final external holdout

Evaluate exactly once:

- baseline configuration;
- final aggregated candidate.

The candidate must satisfy both:

- internal walk-forward acceptance;
- external holdout acceptance.

No further search occurs after viewing the holdout.

---

## 10. Scoring and acceptance contracts

Define these contracts before the first real calibration run.

```csharp
public sealed record CalibrationScoringPolicy
{
    public required string PolicyVersion { get; init; }
    public required string ObjectiveId { get; init; }

    public required int MinimumTradesPerFold { get; init; }
    public required decimal MaximumDrawdownR { get; init; }
    public required decimal MinimumMedianExpectancyR { get; init; }
    public required decimal MinimumProfitFactor { get; init; }
    public required decimal DrawdownPenaltyWeight { get; init; }
    public required decimal TurnoverPenaltyWeight { get; init; }
}
```

```csharp
public sealed record CalibrationAcceptancePolicy
{
    public required string PolicyVersion { get; init; }

    public required decimal MinimumImprovementOverBaseline { get; init; }
    public required decimal MinimumAcceptableFoldPercent { get; init; }
    public required decimal MaximumTrainValidationDegradation { get; init; }
    public required decimal MinimumExternalHoldoutExpectancyR { get; init; }
    public required decimal MaximumExternalHoldoutDrawdownR { get; init; }
    public required int MinimumExternalHoldoutTrades { get; init; }
    public required decimal MinimumPlateauSupport { get; init; }
}
```

Selection logic:

```text
Candidate must pass:
    data-quality gate
    minimum trades
    maximum drawdown
    absolute quality floor
    fold-consistency requirement
    train-to-validation degradation limit
    material baseline improvement
    external holdout gate

Then rank by:
    configured risk-adjusted objective

Tie-break:
    1. existing/default value
    2. fewer overrides
    3. centre of stable plateau
    4. more conservative risk geometry
    5. deterministic parameter and numeric order
```

A candidate moving expectancy from `-0.47R` to `-0.20R` is improved relative to baseline but must still fail the absolute-quality gate.

### 10.1 Selection-bias protection

At minimum:

- use the final untouched holdout;
- require material improvement;
- require stable fold support;
- require neighbouring values to remain acceptable;
- penalise additional overrides;
- record total evaluated-candidate count;
- optionally raise the promotion hurdle as candidate count increases.

Bootstrap confidence intervals or deflated-Sharpe-style controls can be added later.

---

## 11. Evaluation budget

```csharp
public sealed record CalibrationEvaluationBudget
{
    public int WarningEvaluationCount { get; init; }
    public int MaximumEvaluationCount { get; init; }
    public int MaximumEvaluationsPerFold { get; init; }
    public int MaximumInteractionCombinationsPerGroup { get; init; }
    public TimeSpan HardRuntimeLimit { get; init; }
    public CalibrationBudgetOverflowPolicy OverflowPolicy { get; init; }
}
```

Preview before execution must show:

- baseline evaluations;
- sensitivity evaluations;
- starting-point evaluations;
- maximum coordinate passes;
- interaction evaluations;
- refinement evaluations;
- internal-fold multiplier;
- external holdout evaluations;
- estimated cache hits;
- expected uncached backtests;
- estimated candle evaluations;
- estimated duration range;
- hard runtime limit.

Overflow behaviour must be deterministic and explicitly recorded:

- reject;
- reduce refinement;
- reduce starting points;
- skip lower-priority interaction groups.

Never silently truncate.

Before selecting default limits, calculate the intended worst case. A budget of 400 evaluations per fold may be too small for six parameters, three starts, and four passes.

---

## 12. Complete candidate identity and cache

Use a canonical immutable identity.

```csharp
public sealed record BacktestEvaluationIdentity
{
    public required string StrategyId { get; init; }
    public required string StrategyImplementationHash { get; init; }
    public required string EffectiveAgentDefinitionHash { get; init; }
    public required string CompleteOptionsHash { get; init; }
    public required string FeatureSwitchHash { get; init; }

    public required string Instrument { get; init; }
    public required string CandleDataHash { get; init; }
    public required string PriceComponent { get; init; }

    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required DateTimeOffset WarmupStart { get; init; }

    public required string TimeframeTopologyHash { get; init; }
    public required string ExecutionModelVersion { get; init; }
    public required string BrokerCostModelHash { get; init; }
    public required string DataQualityPolicyVersion { get; init; }

    public required int RandomSeed { get; init; }
}
```

The cache key is a canonical hash of this entire identity.

Do not reuse results when strategy code, execution assumptions, spreads, commission, slippage, price component, warm-up, timeframes, data quality, or feature switches differ.

---

## 13. Research ledger and compact artifact

### 13.1 Full experiment ledger

The ledger records:

- request;
- budget preview;
- complete evaluation identity;
- all candidates;
- all invalid-candidate rejections;
- all eligibility failures;
- accepted changes;
- parameter order;
- starting point;
- stage;
- fold;
- scores;
- metrics;
- cache hits;
- cancellation;
- resume steps;
- timestamps;
- deterministic step IDs.

This ledger may be large and is not loaded by live trading.

### 13.2 Compact candidate artifact

```csharp
public sealed record IndicatorCalibrationArtifact
{
    public required int SchemaVersion { get; init; }
    public required string CalibrationId { get; init; }

    public required string StrategyId { get; init; }
    public required string StrategyImplementationVersion { get; init; }
    public required string OptionsSchemaVersion { get; init; }
    public required string ManifestVersion { get; init; }

    public required string Scope { get; init; } // Instrument in first release
    public required string Instrument { get; init; }

    public required string TimeframeTopologyHash { get; init; }
    public required string CandleDataIdentityHash { get; init; }

    public required string BaselineConfigurationHash { get; init; }
    public required string ResolvedCandidateConfigurationHash { get; init; }

    public required IReadOnlyList<CalibratedParameterOverride> Overrides { get; init; }
    public required IReadOnlyDictionary<string, bool> AblationOverrides { get; init; }

    public required CalibrationEvidenceSummary Evidence { get; init; }
    public required CalibrationOutcome Outcome { get; init; }

    public required string ExperimentLedgerId { get; init; }
    public required string ExperimentLedgerChecksum { get; init; }

    public required CalibrationPromotionStatus PromotionStatus { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

Store only differences from the effective baseline as overrides, but also store the complete resolved candidate hash.

### 13.3 Artifact outcomes

```csharp
public enum CalibrationOutcome
{
    Improved,
    NoImprovement,
    InsufficientEvidence,
    UnstableAcrossFolds,
    FailedAbsoluteQualityGate,
    FailedExternalHoldout,
    BudgetRejected,
    FailedDataQuality,
    Cancelled,
    Failed
}
```

Only `Improved` artifacts may be eligible for approval.

---

## 14. Resume and deterministic execution

Persist incremental state after every completed deterministic step.

Example step ID:

```text
fold:{foldId}:stage:{stage}:start:{startId}:parameter:{parameterId}:candidate:{candidateIndex}
```

Resume rules:

- reload the original immutable request;
- validate code/manifest/data compatibility;
- regenerate the same ordered step plan;
- skip completed steps;
- reuse valid cache entries;
- reject resume when compatibility identity changed;
- continue from the first incomplete step.

A resumed run must produce the same:

- winning configuration;
- accepted/rejected decisions;
- fold aggregation;
- artifact decision fields.

Wall-clock timestamps may differ and are excluded from byte-for-byte decision comparison.

Parallel candidate execution must use bounded concurrency and deterministic reduction after all candidates in the current deterministic batch complete.

---

## 15. Overlay application and runtime consumption

### 15.1 Explicit pinning only

Extend the agent/strategy assignment with an optional artifact ID:

```csharp
public string? IndicatorCalibrationArtifactId { get; init; }
```

No global “latest approved” lookup is allowed in the runtime decision path.

### 15.2 Consumption checks

Before applying:

- artifact is approved;
- artifact outcome is `Improved`;
- strategy ID matches;
- implementation version matches;
- options schema matches;
- manifest version is supported;
- instrument matches;
- timeframe topology matches;
- baseline configuration hash matches;
- artifact checksum is valid.

On any mismatch:

- reject the complete overlay;
- retain the original resolved configuration;
- report a clear error;
- never partially apply.

### 15.3 Strongly typed overlay applier

Use the same typed descriptors or a generated typed overlay adapter. Do not rely on unrestricted reflection.

Resolution:

```text
Resolve normal defaults and explicit configuration
    ↓
Validate explicitly pinned approved artifact
    ↓
Apply only declared overrides
    ↓
Run normal options validation
    ↓
Produce final immutable options object
```

---

## 16. Promotion and rollback

Approval must be an immutable audit event, not merely a mutable status flip.

```csharp
public sealed record CalibrationPromotionEvent
{
    public required string EventId { get; init; }
    public required string ArtifactId { get; init; }
    public required string ArtifactChecksum { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public required string TargetEnvironment { get; init; }
    public required string ReviewNotes { get; init; }
    public string? ReplacesArtifactId { get; init; }
    public string? RollbackArtifactId { get; init; }
}
```

Promotion flow:

1. calibration completes;
2. artifact remains pending review;
3. user reviews fold and holdout evidence;
4. compatibility is revalidated;
5. explicit approval event is recorded;
6. an assignment may explicitly reference the artifact;
7. rollback removes or replaces the assignment’s artifact ID.

---

## 17. API and CLI surface

### 17.1 Application service

```csharp
public interface IIndicatorCalibrationApplicationService
{
    Task<CalibrationBudgetPreview> PreviewAsync(
        IndicatorCalibrationRequest request,
        CancellationToken cancellationToken);

    Task<IndicatorCalibrationRunSummary> StartAsync(
        IndicatorCalibrationRequest request,
        CancellationToken cancellationToken);

    Task<IndicatorCalibrationRunDetails?> GetAsync(
        string calibrationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<IndicatorCalibrationRunSummary>> ListAsync(
        CancellationToken cancellationToken);

    Task PauseAsync(string calibrationId, CancellationToken cancellationToken);
    Task ResumeAsync(string calibrationId, CancellationToken cancellationToken);
    Task CancelAsync(string calibrationId, CancellationToken cancellationToken);

    Task<CalibrationPromotionEvent> ApproveAsync(
        IndicatorCalibrationApprovalRequest request,
        CancellationToken cancellationToken);

    Task RejectAsync(
        IndicatorCalibrationRejectionRequest request,
        CancellationToken cancellationToken);
}
```

### 17.2 API routes

```text
POST   /api/indicator-calibrations/preview
POST   /api/indicator-calibrations
GET    /api/indicator-calibrations
GET    /api/indicator-calibrations/{id}
POST   /api/indicator-calibrations/{id}/pause
POST   /api/indicator-calibrations/{id}/resume
POST   /api/indicator-calibrations/{id}/cancel
POST   /api/indicator-calibrations/{id}/approve
POST   /api/indicator-calibrations/{id}/reject
GET    /api/indicator-calibrations/{id}/artifact
GET    /api/indicator-calibrations/{id}/ledger-summary
```

### 17.3 CLI

Commands should support:

```text
indicator-calibration preview
indicator-calibration start
indicator-calibration status
indicator-calibration list
indicator-calibration pause
indicator-calibration resume
indicator-calibration cancel
indicator-calibration approve
indicator-calibration reject
indicator-calibration artifact
```

The CLI prints the budget preview before submission. Non-interactive automation must require an explicit `--accept-budget` or equivalent flag.

---

## 18. Testing strategy

### 18.1 Unit tests

- manifest validation;
- typed parameter application;
- nested options application;
- cross-parameter constraints;
- budget estimator;
- overflow policies;
- sensitivity classification;
- deterministic parameter ordering;
- coordinate-descent convergence;
- multi-start comparison;
- interaction caps;
- local refinement bounds;
- plateau selection;
- cross-fold aggregation;
- insufficient-evidence decisions;
- absolute-quality gate;
- baseline tie preference;
- cache identity;
- deterministic reduction;
- artifact compatibility;
- approval checks.

### 18.2 Leakage tests

Create synthetic data/objectives where validation has a deliberately different optimum.

Assert:

- validation values cannot change fold training search;
- changing a held-out fold does not change its training-selected candidate;
- final holdout is never accessed before the final candidate is frozen;
- search results differ only when training data changes.

### 18.3 Resume tests

Interrupt during:

- sensitivity;
- coordinate descent;
- interaction search;
- local refinement;
- validation;
- external holdout.

Assert resumed decision outputs match uninterrupted outputs.

### 18.4 Regression tests

- no overlay produces existing options;
- no overlay preserves existing decisions;
- existing successful simulator tests remain;
- approved but unreferenced artifact has no effect;
- legacy strategy remains unchanged;
- incompatible artifact is rejected atomically.

### 18.5 End-to-end first-slice test

Using cached or inline deterministic candles:

1. preview budget;
2. start Indicator Confluence calibration;
3. execute internal folds;
4. aggregate stable values;
5. execute external holdout;
6. persist ledger;
7. persist pending artifact;
8. approve explicitly;
9. pin artifact to a simulation request;
10. verify only declared fields changed;
11. remove artifact;
12. verify baseline behaviour returns.

---

## 19. Coding phases and gates

### Phase 1 — baseline freeze and compatibility

Implement:

- pre-calibration option snapshots;
- default configuration hashes;
- options schema versions;
- timeframe topology hash;
- no-overlay regression tests.

**Gate:** no existing default or successful baseline test changes.

### Phase 2 — shared contracts

Implement:

- typed parameter descriptors;
- typed ablation descriptors;
- constraints;
- scoring and acceptance policies;
- compact artifact;
- promotion event;
- compatibility validator.

**Gate:** contracts compile without Simulator dependencies in live-consumed code.

### Phase 3 — persistence and identities

Implement:

- experiment ledger;
- run state;
- complete evaluation identity;
- candidate cache;
- artifact repository extension;
- checksum and corruption handling.

**Gate:** deterministic identity and resume repository tests pass.

### Phase 4 — synthetic search engine

Implement:

- budget estimator;
- sensitivity;
- multi-start coordinate descent;
- interaction search;
- refinement;
- fold candidate selection;
- stable cross-fold aggregation.

Use synthetic objective functions first.

**Gate:** deterministic, bounded, resumable search tests pass.

### Phase 5 — leakage-safe orchestration

Implement:

- internal fold planner;
- embargo/purge rules;
- complete search inside each fold’s train period;
- held-out fold evaluation;
- final external holdout.

**Gate:** leakage tests pass.

### Phase 6 — Indicator Confluence vertical slice

Implement:

- concrete manifest;
- constraints;
- scoring policy;
- backtest candidate adapter;
- artifact creation;
- API;
- CLI;
- budget preview;
- end-to-end test.

**Gate:** real cached-data run completes without changing baseline behaviour.

### Phase 7 — explicit promotion and overlay

Implement:

- approval audit event;
- explicit assignment pinning;
- compatibility checks;
- typed overlay application;
- rollback.

**Gate:** unapproved/unreferenced/incompatible artifacts cannot alter runtime behaviour.

### Phase 8 — second strategy

Implement `LiquidityBreakRetestPlaybook` only after reviewing the first real artifact and confirming the schema is sufficient.

### Later order

1. Improved Progressive;
2. Legacy Progressive;
3. remaining structural playbooks;
4. proper multi-instrument calibration cohorts;
5. group-level hierarchy.

---

## 20. Acceptance criteria for first production release

The first release is accepted only when all of the following are true:

### Compatibility

- Existing option defaults are unchanged.
- Existing successful tests still pass without calibration.
- No-overlay decisions remain equivalent.
- Legacy behaviour is unchanged.

### Correctness

- Search runs fully inside each fold’s training period.
- Validation data does not influence candidate selection.
- Final external holdout is untouched until the candidate is frozen.
- Invalid candidates are rejected before backtesting.
- Absolute quality gates prevent “less negative” candidates from promotion.

### Determinism

- Same request and identity produce the same winning candidate.
- Resume produces the same decision output.
- Different bounded-concurrency levels do not change the winner.
- Tie-breaking is total and deterministic.

### Safety

- No auto-promotion.
- No automatic latest-artifact lookup.
- Only explicitly pinned approved artifacts apply.
- Compatibility mismatch rejects the full overlay.
- Removing an overlay restores baseline behaviour.

### Research quality

- Baseline is evaluated in every fold and holdout.
- Minimum trade and evidence rules are enforced.
- Fold instability is reported rather than hidden.
- Plateau stability is preferred over isolated maxima.
- Final holdout must pass.
- “No improvement” and “insufficient evidence” are normal successful run outcomes.

### Operations

- Budget preview is available before execution.
- Hard budget limits are enforced.
- Search progress can be paused, resumed, and cancelled.
- Artifact and experiment ledger are inspectable through CLI/API.
- Candidate cache identity includes all execution-affecting inputs.

---

## 21. Definition of done for each later strategy

A strategy is calibration-enabled only when it has:

- explicit versioned manifest;
- typed descriptors;
- ablations;
- constraints;
- scoring policy;
- acceptance policy;
- interaction definitions;
- no-overlay regression snapshot;
- complete end-to-end calibration test;
- resume test;
- holdout test;
- artifact compatibility test;
- explicit overlay test;
- existing behaviour unchanged by default.

Do not add multiple strategies in parallel before the generic engine and first vertical slice are reviewed.

---

## 22. Final instruction to the coding agent

Implement this blueprint phase by phase.

Do not alter existing strategy defaults or existing non-calibrated behaviour. Do not broaden calibration scope through reflection. Do not introduce group-level artifacts from single-instrument results. Do not use held-out data to guide any search step. Do not auto-promote an artifact.

At the end of each coding phase:

1. run targeted tests;
2. run the existing regression suite;
3. record changed files;
4. record migration changes;
5. record remaining assumptions;
6. stop if a non-negotiable invariant cannot be satisfied.

The first meaningful milestone is not “the optimiser runs.” It is:

> An instrument-specific Indicator Confluence calibration can run deterministically through internal walk-forward folds and a final untouched holdout, persist a reviewable candidate artifact, and be explicitly applied as an overlay without changing any existing default or non-calibrated strategy behaviour.
