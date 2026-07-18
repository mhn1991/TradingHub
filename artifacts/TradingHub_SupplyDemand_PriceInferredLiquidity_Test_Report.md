# Supply/Demand and Price-Inferred Liquidity Test Report

Date: 2026-07-18

## Result summary

| Scope | Result |
|---|---|
| Simulator and shared-library regression suite | **Passed: 768/768** |
| Focused liquidity regression after final deduplication change | **Passed: 5/5** |
| Focused structural evidence, management, and persistence tests | **Passed: 11/11** |
| Focused liquidity and confluence tests | **Passed: 9/9** |
| Relevant live coordination, registry, and management tests | **Passed: 15/15** |
| Dashboard production build | **Passed** (`vue-tsc --noEmit`, Vite build; 55 modules) |
| TradingCore build | **Passed**, 0 warnings, 0 errors |
| Simulator build | **Passed**, 0 warnings, 0 errors |
| LiveTradingHost build | **Passed**, 0 warnings, 0 errors |
| Full LiveTrading.Tests suite | **106 passed, 2 failed, 108 total**; failures recorded below |
| Patch hygiene | **Passed** (`git diff --check`) |

## Added automated coverage

### Supply/demand

Existing phase 1–3 tests cover contracts, deterministic IDs, objective bases/departures, causal confirmation, invalidation, freshness/mitigation, merging, replay stability, symmetry, and bounded state.

### Liquidity and events

- Equal highs do not become visible before completed-candle confirmation.
- Sweep and accepted-break classifications are distinct.
- Accepted breaks require completed follow-through/holding evidence.
- Previous-day reference levels become available only after the day completes.
- Replay produces stable IDs and stable state.
- Contract/profile validation and serialization remain deterministic.

### Confluence

- Sell-side sweep plus demand produces bullish confluence.
- Buy-side sweep plus supply produces bearish confluence.
- Zone-only and sweep-only input remains neutral.
- Relationship output is causal, deterministic, and bounded.

### Agent, risk, and management

- RecordOnly leaves decisions and risk unchanged while emitting diagnostics.
- Missing evidence is neutral.
- Structural risk multipliers cannot exceed 1.
- Entry-pinned configuration and revision matching are required.
- Zone invalidation and accepted target-pool break exits are default-off.
- Equity-protection ordering remains ahead of structural management.

### Persistence and attribution

- Table routing covers all 11 structural entity groups.
- The bounded writer reports dropped records rather than silently discarding them.
- Outcome accumulation is bounded and deterministic.

## Commands exercised

```text
dotnet build TradingCore/TradingCore.csproj --no-restore
dotnet build Simulator/Simulator.csproj --no-restore
dotnet build LiveTradingHost/LiveTradingHost.csproj --no-restore --verbosity:minimal
dotnet test Simulator.Tests/Simulator.Tests.csproj --no-restore
dotnet test Simulator.Tests/Simulator.Tests.csproj --no-restore --filter FullyQualifiedName~LiquidityAnalyzerTests --verbosity:minimal
dotnet test LiveTrading.Tests/LiveTrading.Tests.csproj --no-restore
dotnet test LiveTrading.Tests/LiveTrading.Tests.csproj --no-restore --filter <relevant live coordinator/registry/management tests>
npm run build
git diff --check
```

## Full live-suite exceptions

1. `AcceleratedSoakTests.FiveMarkets_ManySyntheticM1Candles_NoDuplicatesNoGapsBoundedChannelsMonotonicSequence` exceeded its fixed 30-second test timeout at line 150. This is a timing/soak failure; the relevant modified live tests pass.
2. `LiveExecutionActivationGuardTests.HostRejectsBrokerWritesOutsideOandaPractice` asserts source text containing the obsolete `Brokers.Models.BrokerEnvironment.Demo` namespace, while the unchanged host correctly uses `Brokers.Abstractions.BrokerEnvironment.Demo`.

Neither failing test's production source was changed as part of this implementation. They remain open in the issue register and must be understood before treating the entire live suite as green.

## Test conclusion

The implemented phase 4–12 paths and the full simulator regression suite pass. The delivery does not claim universal acceptance because the broader live suite retains two baseline failures, and no purged held-out walk-forward study or production-scale performance benchmark was performed.
