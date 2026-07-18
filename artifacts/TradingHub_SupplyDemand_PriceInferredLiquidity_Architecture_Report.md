# Supply/Demand and Price-Inferred Liquidity Architecture Report

Date: 2026-07-18

The design extends `ChartAnnotationEngine` and existing execution paths. Analysis remains deterministic, causal, profile-isolated, and read-only with respect to trading. The diagrams below describe the delivered architecture.

## 1. Shared analysis pipeline

```mermaid
flowchart LR
    C[Completed candle] --> E[ChartAnnotationEngine]
    E --> S[Existing confirmed swings]
    E --> SD[SupplyDemandAnalyzer]
    S --> SD
    E --> L[LiquidityAnalyzer]
    S --> L
    SD --> CF[ConfluenceAnalyzer]
    L --> CF
    SD --> AS[Immutable AnalysisSnapshot]
    L --> AS
    CF --> AS
    AS --> A1[Agent A]
    AS --> A2[Agent B]
    AS --> D[Dashboard / persistence]
```

## 2. Supply/demand lifecycle

```mermaid
stateDiagram-v2
    [*] --> Candidate: objective base
    Candidate --> Fresh: departure confirmed / AvailableAt
    Fresh --> Tested: first valid revisit
    Tested --> Mitigated: mitigation threshold
    Fresh --> Invalidated: completed invalidating close
    Tested --> Invalidated: completed invalidating close
    Fresh --> Expired: age/state bound
    Tested --> Expired: age/state bound
    Fresh --> Merged: compatible overlap
    Tested --> Merged: compatible overlap
    Mitigated --> [*]
    Invalidated --> [*]
    Expired --> [*]
    Merged --> [*]
```

## 3. Liquidity lifecycle

```mermaid
stateDiagram-v2
    [*] --> Active: completed-candle pool confirmed
    Active --> Swept: penetration and close back
    Active --> AcceptedBreak: close beyond plus acceptance
    Active --> Expired: configured age/state bound
    Active --> Merged: compatible deterministic overlap
    Swept --> [*]
    AcceptedBreak --> [*]
    Expired --> [*]
    Merged --> [*]
```

## 4. Sweep versus accepted breakout

```mermaid
flowchart TD
    P[Completed candle penetrates active pool] --> B{Closes back on non-break side?}
    B -- Yes --> S[Sweep event]
    B -- No --> D{Close beyond minimum distance?}
    D -- No --> U[Unconfirmed penetration]
    D -- Yes --> H{Required completed hold/follow-through met?}
    H -- No --> U
    H -- Yes --> O{Optional retest/displacement rules met?}
    O -- No --> U
    O -- Yes --> A[Accepted-break event]
```

## 5. Confluence flow

```mermaid
flowchart LR
    SS[Sell-side sweep] --> BC{Nearby causal demand zone?}
    DZ[Demand zone] --> BC
    BC -- Yes --> BR[Bullish relationship]
    BS[Buy-side sweep] --> SC{Nearby causal supply zone?}
    SZ[Supply zone] --> SC
    SC -- Yes --> BE[Bearish relationship]
    BC -- No --> N[No confluence]
    SC -- No --> N
    BR --> SNAP[Bounded immutable snapshot]
    BE --> SNAP
```

## 6. Multi-timeframe flow

```mermaid
flowchart TB
    M1[M1 completed candles] --> P1[Profile-keyed M1 engine]
    M5[M5 completed candles] --> P5[Profile-keyed M5 engine]
    H1[H1 completed candles] --> PH[Profile-keyed H1 engine]
    P1 --> S[Shared market snapshot]
    P5 --> S
    PH --> S
    S --> G{Timestamp <= decision AvailableAt?}
    G -- Yes --> F[Typed timeframe evidence]
    G -- No --> X[Excluded]
    F --> A[Agent decision path]
```

## 7. Agent evidence path

```mermaid
flowchart LR
    S[AnalysisSnapshot] --> E[StructuralEvidencePolicy]
    E --> M{Configured mode}
    M --> D[Disabled]
    M --> R[RecordOnly diagnostics]
    M --> C[Bounded confidence adjustment]
    M --> K[Bounded risk reduction]
    R --> AD[AgentDecision + lineage]
    C --> AD
    K --> RB[RiskBudgetPolicy]
    RB --> AD
    AD --> PM[Existing portfolio/execution gates]
```

## 8. Stop/target path

```mermaid
flowchart TD
    I[Existing strategy geometry] --> G{Structural geometry enabled?}
    G -- No --> O[Keep existing stop/target]
    G -- Yes --> S[Eligible zone stop]
    G -- Yes --> T[Eligible zone or liquidity target]
    G -- Yes --> A[Liquidity stop avoidance]
    S --> V[Validate side, distance, reward/risk]
    T --> V
    A --> V
    V -- Valid --> U[Use adjusted geometry]
    V -- Invalid --> O
    U --> R[RiskManager / PortfolioManager authority]
    O --> R
```

## 9. Entry-pinned management lineage

```mermaid
sequenceDiagram
    participant A as Agent decision
    participant E as Execution path
    participant P as Position registry
    participant M as TradeManager
    A->>E: IDs, profile/revision, entry switches
    E->>P: persist entry-time lineage
    P->>M: managed state plus current snapshot
    M->>M: require entry switch and manager switch
    M->>M: require matching revision and causal event
    alt zone invalidated or target pool accepted-break
        M-->>P: structural exit reason
    else no valid matching evidence
        M-->>P: preserve position / existing rules
    end
```

## 10. PostgreSQL persistence flow

```mermaid
flowchart LR
    S[Immutable structural snapshots/events] --> W[Bounded StructuralAnalyticsWriter]
    W --> Q{Channel capacity}
    Q -- Available --> C[Single async consumer]
    Q -- Full --> DM[Observable dropped-record metric]
    C --> R{Parameterized upsert succeeds?}
    R -- Yes --> PG[(PostgreSQL 11 tables)]
    R -- Transient failure --> RT[Bounded retry]
    RT --> R
    R -- Exhausted --> FM[Failure metric/log]
    PG --> OA[Outcome attribution/research]
```

## Core guarantees

- Analyzer state belongs to a complete analysis-profile key; no mutable global detector state is introduced.
- Stable IDs are based on canonical source inputs and remain independent of thread scheduling.
- Origin, confirmation, and availability are distinct timestamps.
- Snapshots expose immutable, bounded collections.
- Agent policy and persistence consume snapshots; they do not mutate analysis.
- Trading remains under existing strategy, risk, portfolio, execution, and broker gates.
