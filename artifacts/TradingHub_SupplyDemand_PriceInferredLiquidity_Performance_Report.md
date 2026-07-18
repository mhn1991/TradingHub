# Supply/Demand and Price-Inferred Liquidity Performance Report

Date: 2026-07-18

## Design characteristics

- Analysis is incremental per completed candle and reuses the existing confirmed-swing snapshot.
- Pools, zones, events, sweeps, confluence relationships, and attribution samples have configurable maximum counts.
- Old/terminal state is pruned deterministically.
- Candidate searches operate over these bounded collections; no unbounded order-book or tick history was introduced.
- Immutable snapshots can be shared by compatible Agents, avoiding duplicate analysis for identical profile keys.
- Different profile keys own different state, avoiding locks and cross-profile contamination.
- PostgreSQL writes are outside the analysis/decision path through a bounded channel and one async consumer.
- Persistence backpressure is observable through enqueue/drop/retry/failure metrics.

## Observed verification timings

These are development-machine observations, not formal benchmarks:

| Workload | Observation |
|---|---|
| Full simulator/shared regression suite | 768 tests passed in approximately 16 seconds |
| Focused liquidity suite | 5 tests passed in 80 ms test time; approximately 13 seconds including project build/startup |
| Relevant live tests | 15 tests passed in 187 ms test time |
| LiveTradingHost build | Completed in approximately 15.5 seconds |
| Dashboard production build | Completed in approximately 30.1 seconds; 55 modules transformed |

The full live suite also exposed a pre-existing accelerated-soak test that reached its 30-second timeout. This means the delivery makes no broad latency or throughput claim for that workload.

## Complexity and allocation risks

- Same-side overlap and confluence matching are bounded pairwise scans. Their worst-case cost is controlled by profile maxima, so raising those maxima materially increases per-candle work.
- Immutable publication allocates snapshot collections; sharing a profile reduces duplicated allocations across Agents.
- JSON persistence payloads are serialized off the trading decision path, but high event rates can fill the bounded channel and produce reported drops.
- Supporting multiple live profiles for one market would multiply detector state and compute; that path currently fails closed instead.

## Operational recommendations

1. Keep default collection limits until representative five-market soak and allocation measurements are green.
2. Monitor analysis duration, snapshot sizes, writer queue depth, dropped records, retry count, and database latency.
3. Size PostgreSQL writer capacity from measured shadow traffic, not by removing its bound.
4. Benchmark each additional analysis profile because profiles do not share state unless their full keys match.
5. Keep RecordOnly and structural management off during measurement so performance tests do not also change trading behavior.
6. Add a repeatable release-mode benchmark and repair/stabilize the live accelerated-soak test before production consideration.

## Conclusion

The delivered design is bounded and suitable for controlled shadow measurement. Functional test timings show no immediate regression in the simulator path, but formal throughput, tail-latency, allocation, database, and long-duration soak targets have not been established.
