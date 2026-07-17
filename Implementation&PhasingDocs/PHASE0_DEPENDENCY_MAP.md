# Phase 0 — Project Dependency Map

**Purpose:** Record the project reference graph before and after Phase 0's structural changes
(deliverable 2 of the Live OANDA Multi-Agent Production Implementation Plan, §38). Confirms the
"core spine" that live trading will eventually build on is already free of any `Simulator`/
research-only coupling, and shows exactly what Phase 0 adds to it.

## Graph before Phase 0

```
Networking            (leaf)
Brokers             -> Networking
Agent               -> Brokers, ChartAnnotator
ChartAnnotator      -> Brokers
RiskManager         -> Agent, Brokers, ChartAnnotator
TradeManager        -> Brokers, ChartAnnotator
TradingJournal      -> Agent, Brokers
PortfolioManager    -> Agent, Brokers, RiskManager
ExecutionManager    -> Agent, Brokers, RiskManager, TradingJournal
TradingCore         -> Agent, Brokers, ChartAnnotator, ExecutionManager, PortfolioManager,
                        RiskManager, TradingJournal
Simulator           -> Brokers, ChartAnnotator, Agent, RiskManager, ExecutionManager,
                        TradingCore, TradingJournal, TradeManager, PortfolioManager
DashboardContracts  -> ChartAnnotator, Simulator
DashboardLive       -> Agent, Brokers, ChartAnnotator, DashboardContracts, Networking,
                        RiskManager, Simulator
QuantResearch       -> Brokers, RiskManager, TradeManager, Simulator
QuantResearchRunner -> Brokers, QuantResearch, RiskManager, Simulator, TradeManager
BacktestRunner      -> Agent, Brokers, ChartAnnotator, DashboardContracts, Simulator,
                        RiskManager, TradingCore
```

**The Simulator-free "core spine"**: `Networking → Brokers → {Agent, ChartAnnotator} →
RiskManager/TradeManager/TradingJournal → ExecutionManager → TradingCore`. None of these
projects reference `Simulator`, `QuantResearch`, `QuantResearchRunner`, or `DashboardLive` —
this is the pool any live-trading project can safely build on without pulling in research-only
or backtest-only code.

## Graph after Phase 0

Three new edges, all additive — nothing in the pre-existing graph is removed or rewired:

```
Calibration  -> Agent, RiskManager, TradeManager                      (new project)
Simulator    -> Calibration                                           (new edge)
LiveTrading  -> TradingCore, TradeManager                             (new project)
```

`Calibration` sits alongside the core spine (references only `Agent`/`RiskManager`/`TradeManager`,
none of which touch `Simulator`), so `Simulator → Calibration` is a downward reference from a
research/backtest project into a spine-adjacent utility project — the same shape as
`Simulator → RiskManager` already is. No cycle: `Calibration` never references `Simulator`.

`LiveTrading` is the first project in the new live-trading branch of the graph. It references
`TradingCore` (bringing along Agent/Brokers/ChartAnnotator/ExecutionManager/PortfolioManager/
RiskManager/TradingJournal transitively) plus `TradeManager` directly (for
`PositionManagementOptions`, used by `LiveTradingPolicyBundle.LegacyManagement`/
`ImprovedManagement`). It does **not** reference `Simulator`, `QuantResearch`,
`QuantResearchRunner`, or `DashboardLive` — matching the plan doc's target-state rule that live
trading must never depend on offline-research code.

```
                                    ┌─────────────┐
                                    │  Networking │
                                    └──────┬──────┘
                                           │
                                     ┌─────▼─────┐
                                     │  Brokers  │
                                     └──┬─────┬──┘
                              ┌─────────┘     └─────────┐
                        ┌─────▼─────┐            ┌──────▼───────┐
                        │   Agent   │            │ ChartAnnotator│
                        └──┬────┬───┘            └───────┬───────┘
                            │    └───────────┬────────────┘
                     ┌──────▼─────┐   ┌──────▼───────┐   ┌────────────────┐
                     │ RiskManager│   │ TradeManager │   │ TradingJournal  │
                     └──────┬─────┘   └──────┬───────┘   └────────┬────────┘
                            │                 │                    │
                     ┌──────▼─────────────────┴────────────────────▼──┐
                     │                ExecutionManager                 │
                     └──────────────────────┬───────────────────────────┘
                                             │
                                      ┌──────▼──────┐          ┌─────────────┐
                                      │ TradingCore │          │ Calibration │◄── Agent, RiskManager, TradeManager
                                      └──────┬──────┘          └──────┬──────┘
                                             │                        │
                                      ┌──────▼──────┐                 │
                                      │ LiveTrading │                 │
                                      └─────────────┘          ┌──────▼──────┐
                                                                │  Simulator  │ (also -> PortfolioManager)
                                                                └─────────────┘
```

(Diagram simplified — `Simulator` also references `PortfolioManager`, and `PortfolioManager`
references `Agent`/`Brokers`/`RiskManager`; omitted above for clarity since neither is touched
by Phase 0.)

## What Phase 0 deliberately does not create

No `LiveTrading.Oanda` or `LiveTradingHost` project yet — those are Phase 1+, once actual order
placement/streaming wiring against `Brokers/Oanda/*` is being built. `LiveTrading` in this phase
contains only `LiveTradingPolicyBundle` (data model + validation), proving the project can exist
in the graph in the right position without yet needing any broker-specific code.
