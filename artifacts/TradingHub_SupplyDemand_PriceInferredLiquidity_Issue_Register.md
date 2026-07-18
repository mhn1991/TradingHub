# Supply/Demand and Price-Inferred Liquidity Issue Register

Date: 2026-07-18

| ID | Severity | Status | Issue | Consequence / recommended action |
|---|---|---|---|---|
| SDLI-001 | Medium | Open | Prior-session liquidity currently uses the prior completed local day as the conservative session boundary; named intraday trading sessions are not modeled separately. | Add explicit timezone/DST-aware session calendars and causal tests before relying on London/New York/Asia session labels. |
| SDLI-002 | Medium | Open | Round-number liquidity requires an explicitly configured positive step. There is no broker/instrument-metadata step resolver. | Configure per instrument or add a validated metadata adapter; do not infer unsafe increments from symbol strings. |
| SDLI-003 | High | Open, fails closed | Live orchestration does not dispatch multiple analysis profiles for the same market. Conflicting profiles are rejected. | Implement profile-keyed live fan-out and parity/concurrency tests if simultaneous per-market profiles are required. |
| SDLI-004 | Medium | Open, opt-in | PostgreSQL migration, store, and bounded writer exist but are not registered automatically in a runtime DI composition root. | Apply the migration, configure an approved `DbDataSource`, register lifecycle/metrics explicitly, and verify recovery in shadow before enabling. |
| SDLI-005 | High | Open | No purged held-out walk-forward proof has been produced for soft influence, structural geometry, or management exits. | Retain RecordOnly/default-off settings until leakage-safe research demonstrates benefit and acceptable drawdown/turnover. |
| SDLI-006 | Low | Open | Dashboard overlays are switchable, but exact evidence-policy and management switches are supplied through the existing options/API contract rather than dedicated dashboard controls. | Add authenticated, auditable UI controls only if operationally required; retain safe defaults. |
| SDLI-007 | Medium | Open, baseline test | The full live suite's five-market accelerated soak reaches its fixed 30-second timeout. | Profile and stabilize the test/environment, then rerun repeatedly before relying on the full live gate. |
| SDLI-008 | Low | Open, baseline test | A live activation-guard test searches for an obsolete `Brokers.Models` namespace while the unchanged host uses `Brokers.Abstractions`. | Update the brittle source-text assertion to verify behavior or the current namespace. |
| SDLI-009 | Medium | Open | No release-mode throughput, tail-latency, allocation, database backpressure, or multi-day soak baseline was produced. | Establish explicit budgets and measure representative instruments/timeframes in Practice shadow. |
| SDLI-010 | Medium | By design | Price-inferred pools are derived from candles and confirmed pivots, not venue order books or broker depth. | Label them as inferred liquidity everywhere and never interpret them as observed resting orders. |

## Release posture

The code is appropriate for controlled RecordOnly OANDA Practice shadow evaluation after the live baseline failures are explicitly accepted or fixed. It is not cleared by this report for production, hard entry filtering, risk increases, or automatic structural management.
