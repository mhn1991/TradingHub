# Structural confluence: simulation, experiments, and live shadow

This runbook covers the causal structural-confluence agent and its server-owned research workflow.
The strategy is research/shadow-only by default; nothing here enables real-money execution.

## Stable identities and architecture

- Catalog strategy ID: `structural-confluence`
- Strategy version: `structural-confluence-v1`
- Playbooks: `structural.liquidity-sweep-reversal`,
  `structural.supply-demand-pullback`, and `structural.liquidity-break-retest`
- Playbook version: `1.0`
- Meta-label feature schema: `tradinghub-meta-v2`
- Portable trading-policy schema: `2`

`ChartAnnotator` incrementally produces causal liquidity, supply/demand, price-action, regime, and
CCI summaries. `StructuralConfluenceAgent` builds one immutable decision-time evidence packet,
evaluates the three deterministic playbooks, and arbitrates ready candidates. It never sizes
portfolio exposure, submits orders, or mutates broker state. The simulator and live host both
construct it from the same `TradingAgentDefinition` and immutable `TradingPolicyProfile`.

Setup and decision IDs are lower-case SHA-256 identities. Setup identity includes strategy
version, instrument, playbook, structural pool/zone lineage, catalyst, trigger timestamp, and side;
decision identity adds the decision availability timestamp and version. Replaying the same causal
inputs therefore produces the same IDs.

Entry records pin the playbook, setup/decision IDs, pool/zone lineage, policy bundle ID/revision/hash,
and the exact structural management policy. Simulator and live-shadow management use that entry
snapshot, so later configuration changes cannot alter an open position. Shadow positions also
persist and recover their virtual fills, costs, stops, targets, and management audit trail.

## Single simulation

Start the server (launch profile URL `http://127.0.0.1:5180`):

Broker credentials are loaded from the encrypted PostgreSQL vault described in
[`BROKER_CREDENTIAL_VAULT.md`](BROKER_CREDENTIAL_VAULT.md).

```bash
dotnet run -c Release --project DashboardLive/DashboardLive.csproj
```

Submit the sample request:

```bash
curl --fail-with-body -X POST http://127.0.0.1:5180/api/simulations \
  -H 'content-type: application/json' \
  --data @examples/structural-confluence/single-simulation.json
```

The same strategy is selectable in Dashboard **Simulator → Single simulation**. The sample turns
automatic calibration off to make its data boundary explicit. Enable it only when enough history
exists before the requested warm-up and embargo. Direct simulations may alternatively reference
server-owned setup, management, and meta-model artifact GUIDs; client file paths and raw artifacts
are not accepted.

## Immutable experiment profiles and Dashboard

In Dashboard **Simulator → Experiments**:

1. Create a baseline structural profile, then clone it into small variants (for example CCI
   Disabled/Soft/Required or one playbook disabled). Each revision receives a content hash.
2. Select exact profile ID/revision pairs, one optional baseline, UTC learning/evaluation ranges,
   warm-up, embargo, and concurrency limits.
3. Start the experiment. Pause/resume/cancel controls call the server; the browser does not
   orchestrate profile runs.
4. Review the resolved manifest, progress, per-profile results, comparison, and warnings.

The file [`experiment-plan.json`](../examples/structural-confluence/experiment-plan.json) is an
ablation template. Replace its two example GUIDs/revisions with profiles already stored under
`.cache/simulation-profiles` (or created through `/api/simulation-profiles`).

API entry points are `POST /api/simulation-experiments`, `GET /api/simulation-experiments/{id}`,
`GET /results`, `GET /manifest`, plus `/pause`, `/resume`, and `/cancel` actions.

## Experiment CLI

The CLI uses the same application service, profile store, executor, resource governor, artifact
repository, and resumable ledger as Dashboard:

```bash
dotnet run -c Release --project QuantResearchRunner/QuantResearchRunner.csproj -- \
  experiment --plan examples/structural-confluence/experiment-plan.json
```

Resume an interrupted experiment:

```bash
dotnet run -c Release --project QuantResearchRunner/QuantResearchRunner.csproj -- \
  experiment --resume EXPERIMENT_GUID
```

Optional paths are `--profiles-directory`, `--experiments-directory`, `--artifacts-directory`,
`--jobs-directory`, and `--output`. Defaults are beneath `.cache/`; the final resolved snapshot is
written to `.cache/simulation-experiments/{experiment-id}/cli-result.json`.

### Timeline semantics

All four boundaries are UTC half-open ranges: learning is `[learningFrom, learningTo)` and held-out
evaluation is `[evaluationFrom, evaluationTo)`. They cannot overlap. The gap from `learningTo` to
`evaluationFrom` must be at least `embargoDays`. Training and evaluation warm-up streams begin
before their respective scored ranges, but warm-up bars never become scored samples.

The planner combines requested warm-up with analysis, strategy, and management interval needs and
persists the requested/resolved start plus the dominant reason. If `availableDataFrom` cannot
satisfy that minimum, validation fails. `allowInsufficientWarmup: true` is diagnostic only and
marks the result ineligible for promotion-quality research.

### Resource limits

`maxProfileGroups` bounds concurrent profile backtests, `maxTotalStrategyWorkers` bounds aggregate
strategy workers, and `maxHistoricalDownloadsPerBroker` serializes or limits broker downloads.
`estimatedMemoryBudgetBytes` is a plan-level rejection guard, not a promise to reserve memory.
Start with the sample limits (2 groups, 4 workers, 1 broker download) and lower them on small hosts.
The CLI runs one experiment dispatcher; DashboardLive additionally enforces its configured global
experiment queue/governor limits.

## Calibration and approval boundary

Experiment calibration modes are:

- `Disabled`: no calibration artifact is applied.
- `ReuseSpecifiedArtifacts`: only the listed immutable artifact GUIDs are used.
- `TrainFreshAndUseForHeldOutEvaluation`: train inside the learning boundary, then use that fresh
  bundle for the held-out evaluation.
- `TrainFreshPendingReviewOnly`: train a candidate but do not apply it to the evaluation.

Freshly trained output is never live-approved automatically. It is stored as `PendingReview` and
remains isolated by profile revision. A human must review it and call
`POST /api/calibration-candidates/{id}/approve` with `approvedBy`; approval creates an immutable
`ApprovedForDemo` policy but does not activate any live host. Rejection is separately audited.

Meta-label v2 adds playbook identity; CCI state/value/momentum/relationship; liquidity quality,
sweep penetration, reclaim strength and touch count; supply/demand quality, touch count,
penetration and liquidity confluence; and target/invalidation distances. Calibrated lookup falls
back hierarchically from the most specific reliable cohort. A v1/hash-mismatched artifact is
rejected with a retraining requirement; it is not silently interpreted as v2.

## Live shadow

The safe defaults in [`live-shadow.override.template.json`](../examples/structural-confluence/live-shadow.override.template.json)
keep every broker-write switch off. The template intentionally contains an invalid placeholder
policy and must not be started unchanged:

1. Copy the exact `ApprovedForDemo` `TradingPolicyProfile` object returned by the policy-profile
   API into `LivePolicyBundles.structural-eurusd-approved.profile`; do not edit its hash-bearing
   policy fields.
2. Ensure all referenced calibration artifacts exist under the configured `LiveCalibration`
   repository.
3. Copy the completed template to `LiveTradingHost/appsettings.Development.json`, provide OANDA
   demo credentials through environment variables, and start `LiveTradingHost`.

```bash
dotnet run -c Release --project LiveTradingHost/LiveTradingHost.csproj
```

Shadow candidates create broker-independent paper fills and bounded paper positions only. Quotes
and closed candles drive deterministic stops, targets, costs, financing, and the same
structure-based management logic used in simulation. The status API and Dashboard expose open/
closed counts, P/L, recovery state, last playbook, and last error. Shadow mode never calls broker
order placement. Keep `LiveExecution.BrokerWritesEnabled` and `AutomaticExecutionEnabled` false.

## Promotion criteria and limitations

Do not promote on win rate alone. Require positive out-of-sample expected R after costs, acceptable
drawdown, walk-forward stability, no single-instrument dependence, reasonable samples per
playbook, calibrated confidence/Brier performance, shadow/simulator parity, no unexplained missed
or duplicate setups, persistence/safety soak tests, and explicit human approval.

The allowed progression is:

```text
Simulator research → held-out walk-forward → continuous Shadow
→ ManualApproval Demo → limited Automatic Demo
```

Known boundaries:

- no real-money activation, pyramiding, neural direction model, or broker order-book liquidity
  claim is implemented;
- live shadow uses configured slippage/cost assumptions and available quotes/candles, not a broker
  matching engine;
- historical fills remain midpoint plus configured spread/slippage unless a suitable imported
  high-precision dataset is supplied;
- experiment/profile/artifact stores and live-shadow checkpoints are file-backed; PostgreSQL
  migration is outside this scope;
- full `DBManager.Tests` validation requires a running Docker daemon for Testcontainers;
- promotion remains an evidence and human-approval decision, never an automatic experiment result.
