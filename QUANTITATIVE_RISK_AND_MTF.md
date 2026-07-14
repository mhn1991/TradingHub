# Quantitative risk, role-based MTF, and multi-speed management

## Entry pipeline

The progressive agents use distinct timeframe roles rather than requiring every chart to emit an identical signal:

```text
Primary trend       hard directional gate
Secondary trend     optional soft context; strong opposing structure can veto
Setup interval(s)   opportunity/setup evidence
Confirmation(s)     configurable N-of-M consensus
Entry interval      precise trigger
```

The Dashboard default stack is `2h → 1h → 30m → 15m → 5m`. Secondary-trend alignment defaults to zero, so it is soft context by default; enabling a positive minimum makes it a required consensus layer.

All intervals are completed-candle snapshots. They must be unique and ordered. Orders created from a frame remain eligible no earlier than the next execution candle.

## Position sizing

Opening orders pass through:

```text
Agent decision
  → PositionSizer
  → PreTradeRiskManager
  → ExecutionCoordinator
  → broker
```

Supported modes:

- `FixedFractionalRisk`: risk a percentage of current equity.
- `FixedCashRisk`: risk a fixed amount in account currency.
- `FixedQuantity`: manual/backward-compatible mode.

Risk-based sizing uses the original entry-to-stop distance, estimated round-trip costs, quote-to-account conversion, available account margin, a one-position margin cap, broker quantity step, and optional maximum quantity. Quantities are rounded down so the risk budget is not exceeded.

Default research settings:

```text
Risk per trade                 0.50% equity
Maximum account margin usage  30%
Maximum one-position margin   10%
```

A missing currency conversion rejects risk-based sizing instead of inventing a conversion. The streamed single-instrument simulator defaults its account currency to the instrument quote currency unless the request explicitly supplies another supported conversion.

## Multi-speed position management

Management is role-based rather than running the same algorithm on an arbitrary interval list:

```text
Every execution candle  break-even, fixed-R scale-out, profit floor, MFE giveback
Fast structure          local swing/zone/channel response
Main structure          primary trailing, stagnation, momentum/volatility reductions
Thesis / runner         higher-timeframe structural invalidation
```

Default intervals are `5m fast`, `15m main`, and `1h thesis`. Deterministic priority is:

1. already-filled broker stop/target;
2. thesis/full exit;
3. main management;
4. fast management;
5. execution-frame mechanical protection.

Structural layers also consider mechanical stop candidates, preventing a structural recommendation from hiding a tighter break-even or profit floor on the same frame. Stops never widen, reduction stages run once, pending reductions block duplicates, and protective quantities are reconciled after partial fills.

## Deliberate remaining boundary

Comparative runs still use isolated accounts per strategy. That is correct for strategy comparison but is not yet a shared multi-instrument portfolio allocator. A later portfolio mode should add combined portfolio heat, currency buckets, correlation clusters, shared margin reservation, and capital ranking across simultaneous signals.
