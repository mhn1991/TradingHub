# Final Profit-Protection Validation

## Implemented

- Staged scale-out with immutable stage IDs and minimum runner.
- Cost-aware break-even and structure/ATR trailing.
- Profit-floor and MFE-giveback stop ratchets.
- Stagnation, structural deterioration, momentum decay, volatility exhaustion, session-risk, and execution-cost reductions.
- Reduce-only close semantics through broker contracts, OANDA mapping, and the simulated broker.
- Atomic simulated protective-order resizing after partial closes.
- Partial-exit trade records, replay events, chart markers, and performance metrics.
- Daily account-equity profit and giveback entry locks.
- Null/corrupt long-run trade-index handling in the Dashboard API.

## Logical invariants

- R always uses entry versus initial stop.
- Stops never widen.
- New management actions become eligible only from the next execution sequence.
- One pending reduction blocks duplicate reductions.
- A completed stage cannot execute twice.
- Reductions cannot breach the configured runner floor.
- Reduce-only orders cannot add or reverse exposure.
- Protective orders stay active until the close fill changes the actual position.
- Partial closes resize both stop and target to the remaining position.
- Momentum reduction requires corroboration.
- Volatility exhaustion requires a prior expansion after entry.
- Corrupt trade-feed rows do not poison subsequent polling.

## Validation executed in this environment

- Vue/TypeScript typecheck.
- Vite production build.
- Replay data validation.
- Tree-sitter parse of every C# source file.
- Source/archive exclusion checks.

The environment does not contain the .NET SDK and has no network access to install it. Therefore the actual .NET build and NUnit execution must be run after extraction:

```bash
dotnet restore TradingHub.slnx
dotnet build TradingHub.slnx -c Release
dotnet test TradingHub.slnx -c Release
```

or:

```bash
./validate.sh
```

## Live-broker limitation

The simulated broker provides deterministic atomic reduction/protective reconciliation. OANDA close requests are mapped as reduce-only, but native live stop-amendment and post-partial protective reconciliation still need broker-demo certification before enabling autonomous live execution.
