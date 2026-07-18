# Supply/Demand and Price-Inferred Liquidity Implementation Report

Date: 2026-07-18

## Scope and status

Phases 1–3 were present in the working tree when this work began. This delivery completes phases 4–12 without replacing the existing swing engine, RiskManager, PortfolioManager, price-action logic, support/resistance logic, or NEoWave logic.

| Phase | Result |
|---|---|
| 4. Liquidity pool detector and lifecycle | Implemented completed-candle detection for equal highs/lows, isolated confirmed swings, range boundaries, prior completed day/week/session references, and configured round-number levels. Added deterministic merging, pruning, lifecycle state, provenance, and bounded snapshots. |
| 5. Sweep versus accepted break | Implemented mutually exclusive completed-candle classification for sweeps, accepted breaks, and unconfirmed penetrations. Events carry origin, confirmation, availability, pool lineage, direction, penetration, close distance, and optional retest evidence. |
| 6. Confluence engine | Implemented explicit bullish sell-side-sweep/demand and bearish buy-side-sweep/supply relationships. Missing or unmatched evidence creates no confluence rather than negative evidence. |
| 7. Shared snapshot/profile cache | Extended immutable chart snapshots and the existing profile-keyed engine registry. Equal profiles share analysis; different profile hashes remain isolated. Live execution fails closed if conflicting profiles are requested for the same market. |
| 8. Agent RecordOnly integration | Added typed diagnostics, evidence lineage, and exact feature-policy options. Supply/demand and liquidity default to `RecordOnly`; confluence defaults disabled. |
| 9. Dashboard/chart rendering | Added replay contracts, TypeScript models, overlay switches, lifecycle-aware zone rectangles, liquidity bands, and sweep/break markers with causal timestamps and tooltips. |
| 10. PostgreSQL persistence and attribution | Added provider-neutral async PostgreSQL upsert storage, an explicit migration for 11 structural analytics tables, a bounded single-reader writer with retry/drop metrics, source-generated JSON metadata, and a bounded outcome-attribution accumulator. Runtime DI wiring remains opt-in. |
| 11. Optional soft influence | Added bounded confidence and/or risk-reduction modes. Structural evidence can never raise the risk multiplier above 1 and cannot bypass existing risk or portfolio authority. |
| 12. Management rules | Added default-off structural stop/target geometry and entry-pinned supply/demand invalidation and accepted-liquidity-break exits. Existing equity protection remains first; structural management requires matching revisions and entry-time opt-in. |

## Implemented behavior

### Liquidity analysis

`LiquidityAnalyzer` consumes only the current completed-candle snapshot, existing confirmed swings, and ATR already available to `ChartAnnotationEngine`. Its state is per analysis engine/profile. Pool IDs and event IDs are derived from stable inputs rather than insertion order.

Default equal-level qualification requires 2–6 confirmed levels whose maximum price difference is no more than 0.10 ATR; the resulting band is capped at 0.20 ATR. Same-side, same-type overlaps merge deterministically when the configured overlap threshold is met. Opposing sides are never merged. Source pivot times and merged lineage remain visible.

A pool progresses through explicit states and is retained only within configured bounds. A sweep requires a penetration followed by a close back on the non-break side. An accepted break requires a completed close beyond the configured minimum distance plus configured holding/follow-through evidence; optional retest/displacement rules are evaluated only from completed candles. A penetration satisfying neither is recorded as unconfirmed, not forced into either classification.

Completed prior-period references become available only after their source period ends. Round numbers are produced only when an explicit positive price step is configured.

### Confluence and multi-timeframe evidence

The confluence analyzer records explicit relationships rather than synthesizing an opaque score. Bullish confluence links a sell-side sweep to a demand zone; bearish confluence links a buy-side sweep to a supply zone. It respects causal availability, configured distance/age limits, stable IDs, and bounded output.

The shared snapshot can carry evidence from the existing timeframe pipeline. The agent receives typed current evidence and lineage, while the existing strategy remains responsible for its decision. No liquidity or supply/demand component submits an order.

### Agent, risk, stops, targets, and management

The exact policy modes are `Disabled`, `RecordOnly`, `SoftConfidence`, `SoftRiskReduction`, and `SoftConfidenceAndRisk`. Defaults preserve prior trading behavior: supply/demand and liquidity are `RecordOnly`, confluence is disabled, and structural stops, targets, and management are off.

Missing, stale, contradictory, or unavailable structural evidence is neutral. Soft confidence adjustments are bounded by explicit tuning. Soft risk adjustments feed `RiskBudgetPolicy` through a multiplier clamped to `[0, 1]`; they cannot increase risk. RiskManager and PortfolioManager retain final authority.

Optional structural geometry can propose a nearer valid stop, a zone/liquidity target, or liquidity stop avoidance, then recalculates reward/risk before use. Trade management retains entry IDs, profile/revision lineage, and the switches that were enabled at entry. A later configuration change cannot silently opt an existing position into structural management.

### Simulator/live parity and observability

The simulator and live orchestration propagate the same structural multiplier, diagnostic fields, lineage, and entry-time management flags through their existing execution paths. Shared analyzers, policies, risk calculation, and trade-management rules are common code. Their surrounding scheduling and broker orchestration remain environment-specific.

The dashboard displays structural state without changing it. PostgreSQL persistence is outside the decision path: the bounded writer has one consumer, records retry and drop counts, and uses idempotent upserts. It is intentionally not enabled automatically in a runtime composition root.

## Safety posture

- This is price-inferred liquidity, not actual order-book or venue liquidity.
- No hard entry filter was enabled.
- No subsystem was given order-placement authority.
- Structural evidence cannot increase risk.
- Historical records distinguish origin, confirmation, and availability.
- Profile state is isolated, snapshot collections are immutable, and IDs are deterministic.
- Structural position management is off until held-out evidence justifies enabling it.

Known gaps and operational constraints are recorded in the accompanying issue register. In particular, named intraday-session semantics, broker-derived round-number increments, multi-profile live dispatch, runtime persistence registration, and held-out validation remain follow-up work.

## Direct answers

1. **Are zones and pools causal?** Yes. They use completed candles and confirmed, already-available inputs; state transitions occur only when the determining candle is complete.
2. **Can anything appear before `AvailableAt`?** No. Origin and confirmation may be earlier, but publication and downstream use are gated by `AvailableAt`.
3. **Are existing swings reused?** Yes. Liquidity consumes the confirmed swing snapshot produced by the existing `ChartAnnotationEngine`; it does not install a second swing detector.
4. **How are equal levels defined?** By default, 2–6 confirmed same-side pivots whose maximum separation is at most 0.10 ATR, with a pool band no wider than 0.20 ATR. Both values are profile parameters.
5. **How is sweep distinguished from breakout?** A sweep penetrates a pool and closes back on the non-break side. An accepted breakout closes beyond the minimum distance and supplies the configured completed-candle hold/follow-through evidence, with optional retest/displacement checks. Anything between is unconfirmed.
6. **How are zones invalidated and pools consumed?** Zones use explicit fresh, tested/mitigated, invalidated, expired, or merged lifecycle states. Pools use active, swept, accepted-break, expired, or merged terminal states; the event and source lineage explain the transition.
7. **How are overlaps merged?** Only compatible same-side/type candidates meeting the configured overlap ratio merge, in deterministic order, while retaining source IDs/times. Opposing sides do not merge.
8. **Do these complement support/resistance, price action, and NEoWave?** Yes. They are separate additive evidence and management inputs; they neither replace nor weaken those systems.
9. **Is uncertainty neutral?** Yes. Missing, stale, contradictory, or unconfirmed evidence contributes no negative assumption and no hidden score.
10. **Can risk increase?** No. The structural multiplier is clamped to `[0, 1]`, and existing RiskManager and PortfolioManager limits remain authoritative.
11. **Can either subsystem place trades?** No. They publish immutable analysis and diagnostics only.
12. **Are open trades entry-pinned?** Yes. Structural IDs, profile/revision lineage, and entry-time management switches propagate into simulated and live trade state; management also requires a matching revision.
13. **Can multiple Agents share snapshots?** Yes, when their complete analysis-profile keys match. Different profiles get isolated engines; the current live path rejects conflicting profiles for one market instead of sharing incompatible state.
14. **Are simulator and live equivalent?** They use the same detector, evidence, risk, lineage, and management code for the supported single-profile configuration. They are not claimed to be byte-identical because scheduling, persistence, and broker orchestration differ, and live multi-profile dispatch is not implemented.
15. **Is it safe for OANDA Practice shadow testing?** Conditionally: use Practice plus shadow/RecordOnly mode, leave structural management off, apply and validate persistence separately, and resolve or explicitly accept the two recorded baseline live-test issues first. It is not approved here for production or for autonomous enablement of soft/management modes.
