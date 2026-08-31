# Blueprint — 4H Statistical Trend Agent

## 1. Project Goal

Build an independent statistical trading engine that learns the historical characteristics of 4-hour trends for each symbol.

The system should answer:

- How far do bullish trends usually move?
- How far do bearish trends usually move?
- How long do bullish trends usually last?
- How long do bearish trends usually last?
- Where is the current trend within its historical price distribution?
- Where is the current trend within its historical time distribution?
- Has the trend progressed enough to justify opening a swing position?
- Is the trend statistically approaching exhaustion?

The eventual trading architecture should support:

```text
One 4H swing position
+
multiple lower-timeframe tactical trades
in the same direction as the 4H trend
```

For example:

```text
4H Trend = Bullish

Swing position:
LONG

15m tactical strategy:
BUY signals  → allowed
SELL signals → blocked
```

---

# 2. Keep This Agent Separate From the Existing ML Agent

This should be an independent quantitative agent.

The current ML system answers:

```text
1m / 5m / 15m features
        ↓
LightGBM
        ↓
BUY / HOLD / SELL probabilities
```

The new system answers:

```text
4H historical trends
        ↓
Trend statistics
        ↓
Current trend state
```

Eventually:

```text
              4H Statistical Trend Agent
                         │
                  Trend Direction
                  Trend Progress
                  Exhaustion
                         │
                         ▼
                 Trading Coordinator
                         ▲
                         │
              Lower-Timeframe ML Agent
                   1m / 5m / 15m
                         │
                   BUY/HOLD/SELL
```

The two agents should first prove their own edge independently.

---

# 3. Core Output

At runtime, the Trend Agent should produce a structure similar to:

```text
SymbolTrendState
{
    Symbol: XAUUSD

    Direction: Bullish
    Status: Confirmed

    StructuralStartTime
    StructuralStartPrice

    ConfirmationTime
    ConfirmationPrice

    CurrentMovePct
    CurrentDurationHours

    PricePercentile
    TimePercentile

    DistanceToPriceP90
    DistanceToPriceP95

    DistanceToTimeP90
    DistanceToTimeP95

    JointTrendRarity

    ExhaustionScore

    SwingAction

    TacticalBias
}
```

Example:

```text
XAUUSD

Direction            Bullish
Status               Mature

Current move         +4.2%
Duration             64 hours

Price percentile     0.47
Time percentile      0.39

Exhaustion           Low

Swing action         HOLD LONG
Tactical bias        BUY ONLY
```

---

# 4. Symbol-Specific Models

Every symbol must have its own statistical profile.

Do not assume:

```text
EURUSD trends
=
XAUUSD trends
=
BTCUSD trends
```

Instead maintain:

```text
XAUUSD Trend Profile
EURUSD Trend Profile
GBPUSD Trend Profile
BTCUSD Trend Profile
...
```

Each profile contains independent bullish and bearish distributions.

---

# 5. Bullish and Bearish Trends Must Be Separated

For each symbol create:

```text
BullTrendPopulation

BearTrendPopulation
```

because bullish and bearish behaviour may be asymmetric.

For example, XAUUSD could show:

```text
Bull trends

Median move       4.8%
Median duration   72h
```

while:

```text
Bear trends

Median move       3.1%
Median duration   44h
```

That difference itself is useful information.

---

# 6. Most Important Component: Trend Definition

Before implementing bootstrap simulation, define exactly:

```text
What starts a trend?

When does a possible trend become confirmed?

What ends a trend?
```

This is the most important research problem in the entire project.

If trend segmentation is wrong, running:

```text
10,000 bootstrap samples
```

only produces very precise statistics from incorrectly defined trends.

---

# 7. Trend Detection Must Be Causal

The detector must never use future information.

Incorrect approach:

```text
Search future candles
        ↓
Find final lowest low
        ↓
Declare that point the trend start
```

That cannot be known live.

Correct approach:

```text
Market begins moving
        ↓
possible trend detected
        ↓
additional evidence arrives
        ↓
trend becomes confirmed
```

At time `t`, only:

```text
data <= t
```

may be used.

---

# 8. Trend State Machine

Use a state machine such as:

```text
Neutral
   │
   ▼
CandidateBull / CandidateBear
   │
   ▼
ConfirmedTrend
   │
   ▼
MatureTrend
   │
   ▼
Exhaustion
   │
   ▼
TrendEnded
```

Example bullish lifecycle:

```text
Neutral

↓ bullish movement appears

CandidateBull

↓ structural confirmation

ConfirmedBull

↓ trend continues

MatureBull

↓ statistical / structural exhaustion

Exhaustion

↓ reversal / invalidation

Ended
```

---

# 9. Structural Start vs Confirmation

These must be stored separately.

Example:

```text
Trend structurally started:
5000

Trend became detectable:
5030
```

The historical trend may be measured from the structural start.

But a live strategy cannot enter before confirmation.

Therefore store:

```text
StructuralStartTime
StructuralStartPrice

ConfirmationTime
ConfirmationPrice
```

This also lets us measure how much of the trend was already consumed before confirmation.

---

# 10. Trend Detector V1

Keep V1 relatively simple.

Possible components:

```text
Price displacement

EMA slope

Higher-high / lower-low structure

Meaningful movement from a recent local extreme
```

For example, a bullish candidate might require:

```text
EMA20 slope > 0

AND

price has moved sufficiently from a recent low

AND

bullish market structure begins forming
```

The exact thresholds must remain configurable.

Do not over-optimize them in V1.

---

# 11. Historical Trend Record

Every detected historical trend becomes a `TrendRecord`.

Example:

```text
TrendRecord
{
    Symbol

    Direction

    StructuralStartTime
    StructuralStartPrice

    ConfirmationTime
    ConfirmationPrice

    EndTime
    EndPrice

    DurationBars
    DurationHours

    TotalMovePct

    MoveAfterConfirmationPct

    ATRNormalizedMove

    MFE

    MaximumRetracement

    ConfirmationDelayBars

    ConfirmationDelayPct
}
```

Additional statistics can be added later.

---

# 12. Historical Trend Library

Store all detected trends.

Conceptually:

```text
HistoricalTrendLibrary

XAUUSD
│
├── Bull
│   ├── Trend001
│   ├── Trend002
│   ├── Trend003
│   └── ...
│
└── Bear
    ├── Trend001
    ├── Trend002
    ├── Trend003
    └── ...
```

This library becomes the source population for the bootstrap engine.

---

# 13. Price Extent Distribution

For bullish trends:

```text
PriceExtent =
(TrendHigh - StructuralStartPrice)
/
StructuralStartPrice
```

For bearish trends:

```text
PriceExtent =
(StructuralStartPrice - TrendLow)
/
StructuralStartPrice
```

Store bearish movement as a positive magnitude.

Direction is stored separately.

Example:

```text
Bull trend moves:

2.1%
3.8%
4.4%
6.2%
9.7%
...
```

---

# 14. Trend Duration Distribution

For each trend:

```text
DurationHours =
EndTime - StructuralStartTime
```

Also store:

```text
DurationBars
```

On a 4H timeframe:

```text
1 bar = 4 hours
```

---

# 15. Empirical Quantiles

Do not assume trend sizes or durations follow a normal distribution.

Use empirical quantiles.

For example:

```text
P05
P10
P25
P50
P75
P90
P95
```

for both:

```text
Price Extent
Duration
```

Example:

```text
XAUUSD Bull Price Distribution

P05      1.1%
P10      1.5%
P25      2.4%
P50      4.3%
P75      6.8%
P90      9.4%
P95     11.8%
```

And separately:

```text
XAUUSD Bull Duration

P05      12h
P10      20h
P25      36h
P50      64h
P75     104h
P90     148h
P95     188h
```

---

# 16. Bootstrap Engine

Use bootstrap resampling to estimate uncertainty in these statistics.

If there are:

```text
650 historical bullish trends
```

then one bootstrap iteration:

```text
sample 650 trends
with replacement

calculate:
P05
P10
P25
P50
P75
P90
P95
```

Repeat:

```text
10,000 times
```

for bullish and bearish trends independently.

---

# 17. Why Bootstrap Is Being Used

The primary trend distribution itself comes from historical trends.

Bootstrap answers a second question:

> How uncertain are our estimates of that distribution?

For example:

```text
Estimated Bull P95 price extent:

11.7%
```

Bootstrap may show:

```text
90% confidence interval
for the P95 estimate:

10.9% → 12.8%
```

That is relatively stable.

But:

```text
7.1% → 18.5%
```

would indicate that P95 is poorly estimated.

---

# 18. Important Statistical Terminology

For trading decisions, the useful concept is mainly:

```text
Empirical / Predictive Quantiles
```

such as:

```text
5th percentile
50th percentile
95th percentile
```

because these describe where historical trends fall.

The bootstrap then produces:

```text
confidence intervals
around those estimated quantiles
```

These are two different concepts.

Example:

```text
Trend price P95      = 11.7%

90% bootstrap CI
around estimated P95 = 10.9% → 12.8%
```

---

# 19. Bootstrap Convergence Test

Do not blindly assume 10,000 is necessary or sufficient.

Compare:

```text
1,000
5,000
10,000
20,000
```

iterations.

Check whether:

```text
P50
P90
P95
```

and their confidence intervals stabilize.

If results barely change after 10,000:

```text
10,000 is sufficient.
```

---

# 20. Reproducibility

Store a deterministic bootstrap seed:

```text
BootstrapSeed
```

Every experiment should be reproducible.

---

# 21. Statistical Trend Profile

Create:

```text
SymbolTrendProfile
{
    Symbol

    BullProfile
    BearProfile
}
```

Each direction profile contains:

```text
DirectionTrendProfile
{
    SampleCount

    PriceP05
    PriceP10
    PriceP25
    PriceP50
    PriceP75
    PriceP90
    PriceP95

    TimeP05
    TimeP10
    TimeP25
    TimeP50
    TimeP75
    TimeP90
    TimeP95

    PriceQuantileConfidenceIntervals

    TimeQuantileConfidenceIntervals

    ReliabilityScore
}
```

---

# 22. Profile Reliability

A statistical profile should not automatically be trusted.

Calculate reliability based on:

```text
Number of historical trends

Bootstrap uncertainty

Quantile stability

Bull/Bear sample balance

Historical regime coverage
```

Example:

```text
XAUUSD Bull

Samples              812
Price stability      0.91
Duration stability   0.86

Overall reliability  0.89
```

A low-reliability profile could produce:

```text
NoTrade
```

or reduced exposure.

---

# 23. Current Price Percentile

Suppose the current bullish trend has moved:

```text
+4.8%
```

and historically:

```text
P25 = 2.3%
P50 = 4.5%
P75 = 6.9%
```

Then current price progress may be approximately:

```text
PricePercentile ≈ 0.53
```

Meaning:

> The current trend has moved farther than roughly 53% of comparable historical bullish trends.

---

# 24. Current Time Percentile

Suppose:

```text
Current trend age = 70h
```

while:

```text
P50 = 64h
P75 = 104h
```

Then:

```text
TimePercentile ≈ 0.55
```

Meaning:

> The trend has lasted longer than roughly 55% of comparable historical trends.

---

# 25. Model Price and Time Separately First

V1 should produce:

```text
PricePercentile
```

and:

```text
TimePercentile
```

independently.

Do not immediately compress them into one score.

We first want to measure how useful each dimension is independently.

---

# 26. Joint Price-Time Model

V2 should model the relationship between:

```text
Trend Price Extent
and
Trend Duration
```

because they are unlikely to be independent.

Conceptually:

```text
                  Duration
                     ▲
                     │       •
               •     │   •
          •          │
                     │       •
          •          │
─────────────────────┼────────────► Price Move
```

Build a joint empirical distribution:

```text
P(PriceExtent, Duration)
```

Then calculate how unusual the current trend is within that 2D space.

---

# 27. Joint Rarity

Example:

```text
Current trend:

Price percentile = 0.82
Time percentile  = 0.76
```

Independently, neither is necessarily extreme.

But historically the combination may be rare.

The joint model should produce something like:

```text
JointRarity = 0.91
```

meaning the current price-time state is already near the outer historical envelope.

That can improve exhaustion detection.

---

# 28. Swing Entry Concept

The swing trade begins only after trend confirmation.

Candidate logic:

```text
TrendStatus == Confirmed

AND

PricePercentile >= EntryThreshold
```

For example:

```text
EntryThreshold = P05
```

Then:

```text
Bull trend → Swing BUY

Bear trend → Swing SELL
```

But P05 should not be assumed to be optimal.

---

# 29. Entry Threshold Research

Test:

```text
P05
P10
P15
P20
P25
```

The trade-off is:

```text
earlier entry
        ↓
capture more trend
but
more false starts
```

versus:

```text
later entry
        ↓
more confirmation
but
less trend remaining
```

---

# 30. Price-Based and Time-Based Entry Experiments

Run these independently:

```text
Experiment A

Price confirmation only
```

```text
Experiment B

Time confirmation only
```

```text
Experiment C

Price AND Time confirmation
```

Possibly later:

```text
Price OR Time
```

Do not decide beforehand which is best.

Let walk-forward testing answer it.

---

# 31. Swing Profit Logic

This system should not rely primarily on:

```text
fixed 1:2 R:R
```

Instead:

```text
Risk
→ structural invalidation

Profit
→ statistical trend continuation
```

That means an exceptional trend could produce:

```text
3R
5R
8R
```

without being forcibly closed at a predefined fixed reward target.

---

# 32. Swing Exit Concept

Use:

```text
Price percentile
Time percentile
Trend structure
```

to detect exhaustion.

For example:

```text
PricePercentile >= 0.75
→ begin monitoring more aggressively

PricePercentile >= 0.90
→ profit protection

PricePercentile >= 0.95
→ strong exhaustion zone
```

But P95 must NOT mean:

```text
guaranteed reversal
```

It only means:

> The trend is larger than approximately 95% of comparable historical trends.

---

# 33. Tail Trends

Some trends will naturally exceed historical P95.

Therefore:

```text
P95 = exhaustion zone
```

not:

```text
P95 = automatic hard exit
```

This distinction matters because the largest trends may generate a large portion of system profit.

---

# 34. Time-Based Exhaustion

Price alone is insufficient.

Example:

```text
Price percentile = 0.67
Time percentile  = 0.97
```

Price still appears to have room.

But the trend has already lasted longer than almost all historical trends.

That should increase exhaustion risk.

Similarly:

```text
Price percentile = 0.96
Time percentile  = 0.38
```

means:

> The trend has travelled an unusually large distance very quickly.

That may also indicate instability/exhaustion.

---

# 35. Exhaustion State

Create:

```text
TrendExhaustionState
{
    PricePercentile

    TimePercentile

    JointRarity

    StructuralWeakness

    ExhaustionScore
}
```

Possible states:

```text
Low
Moderate
High
Extreme
```

Initially retain the raw values too so the score does not hide information.

---

# 36. Swing Position State Machine

Use:

```text
OPEN
 │
 ▼
NORMAL_HOLD
 │
 ▼
PROFIT_PROTECTION
 │
 ▼
TRAIL
 │
 ▼
EXIT
```

This is better than:

```text
P95 reached
→ close immediately
```

---

# 37. Swing Stop Loss

A stop still exists.

But preferably define it through:

```text
trend invalidation
```

rather than fixed reward/risk geometry.

For a bullish trend:

```text
structural low broken

OR

trend detector transitions to bearish/neutral
```

could invalidate the trade.

For bearish trends, use the opposite.

---

# 38. Trend Capture Ratio

A critical metric for this project:

```text
TrendCaptureRatio =
CapturedSwingMove
/
AvailableTrendMoveAfterEntry
```

Example:

```text
Remaining move after entry = 8%

Swing captured             = 5.6%

TrendCaptureRatio          = 70%
```

A system with a lower win rate but high trend capture may be very valuable.

---

# 39. Entry Efficiency

Measure:

```text
EntryDelay =
TrendMoveConsumedBeforeEntry
/
TotalTrendMove
```

Example:

```text
Total trend       = 10%
Move before entry = 1.5%

EntryDelay = 15%
```

Lower is generally better, provided false trend entries do not increase excessively.

---

# 40. Exit Giveback

Measure:

```text
ExitGiveback =
MaximumFavourableExcursion
-
RealizedProfit
```

Example:

```text
MFE        = +8.2%
Exit       = +6.5%

Giveback   = 1.7%
```

This is especially important for evaluating the P90/P95 exit logic.

---

# 41. Tactical Trading Layer

Once a 4H trend is confirmed:

```text
4H Bull
```

the lower-timeframe tactical system can be restricted to:

```text
BUY only
```

and when:

```text
4H Bear
```

allow:

```text
SELL only
```

Initial implementation:

```text
4H Bull

15m BUY  → allowed
15m SELL → ignored
```

---

# 42. Lower-Timeframe Integration

Eventually:

```text
4H Trend Agent
       │
       ▼
Direction Gate
       │
       ▼
15m / 5m / 1m ML Agent
       │
       ▼
Tactical Trade
```

Example:

```text
4H trend:

Bullish
Price percentile = 0.32
Time percentile  = 0.26
Exhaustion       = Low
```

Lower timeframe:

```text
P(BUY) = 0.78
```

Result:

```text
Take tactical BUY
```

---

# 43. Tactical Trading Should Change as the 4H Trend Matures

For example:

```text
4H Trend Progress < P75
→ normal tactical trading

P75 → P90
→ higher confidence requirement

>P90
→ very selective / potentially no new tactical entries
```

This should later be tested rather than hard-coded permanently.

---

# 44. Combined Trading Model

During one bullish 4H trend:

```text
Swing LONG
```

may remain open throughout most of the trend.

Inside it:

```text
Tactical LONG #1

Tactical LONG #2

Tactical LONG #3

Tactical LONG #4
...
```

So total profit comes from:

```text
trend capture
+
repeated lower-timeframe opportunities
```

This is one of the core advantages of the architecture.

---

# 45. Combined Risk Management

Swing and tactical positions must not be treated as unrelated risks.

Example:

```text
Maximum allowed XAUUSD exposure = 2R
```

If:

```text
Swing position risk = 1R
```

then all tactical positions combined may only use:

```text
1R additional risk
```

unless portfolio rules explicitly allow more.

---

# 46. Portfolio Risk Layer

Eventually monitor:

```text
Symbol exposure

Direction exposure

Currency exposure

Correlation exposure

Swing exposure

Tactical exposure

Portfolio drawdown
```

For example:

```text
EURUSD Long
GBPUSD Long
XAUUSD Long
```

may share USD exposure even though they are different symbols.

---

# 47. Data Pipeline

```text
Historical Data / OANDA
          │
          ▼
      4H Candles
          │
          ▼
   Causal Trend Detector
          │
          ▼
      Trend Segmenter
          │
          ▼
    Historical Trend Library
          │
          ▼
     Bootstrap Engine
          │
          ▼
    Symbol Trend Profile
          │
          ▼
 Current Trend State Estimator
          │
          ▼
     Swing Decision Engine
```

---

# 48. Walk-Forward Training

Historical distributions must also be strictly walk-forward.

Example:

```text
Window 1

Historical trends:
2021 → 2023

Build bootstrap profile

Freeze profile

Test:
2024 Q1
```

Then:

```text
Window 2

Historical trends:
2021 → 2024 Q1

Build new profile

Test:
2024 Q2
```

The test period must never influence:

```text
trend statistics
bootstrap quantiles
entry thresholds
exit thresholds
```

before it is evaluated.

---

# 49. Important Leakage Rule

Do not:

```text
segment the entire 2021–2026 dataset
using future-confirmed pivots
```

and then split those trends into train/test.

The detector itself must be causal.

Otherwise future information can leak into trend boundaries.

---

# 50. Initial Configuration

Example:

```text
TrendAgentConfig
{
    BaseTimeframe = 4H

    BootstrapIterations = 10000

    Quantiles =
    [
        0.05,
        0.10,
        0.25,
        0.50,
        0.75,
        0.90,
        0.95
    ]

    MinimumTrendSamples = ...

    EntryPercentile = ...

    ProtectionPercentile = ...

    ExhaustionPercentile = ...
}
```

All important parameters should be configurable.

---

# 51. Suggested Project Structure

```text
TrendStatistics
│
├── Data
│   ├── Candle
│   └── CandleAggregator
│
├── Detection
│   ├── ITrendDetector
│   ├── TrendDetector
│   └── TrendStateMachine
│
├── Segmentation
│   ├── TrendRecord
│   └── TrendSegmenter
│
├── Statistics
│   ├── QuantileCalculator
│   ├── BootstrapEngine
│   ├── BootstrapResult
│   └── JointDistribution
│
├── Profiles
│   ├── SymbolTrendProfile
│   ├── DirectionTrendProfile
│   └── ProfileRepository
│
├── Runtime
│   ├── TrendStateEstimator
│   ├── TrendProgressEstimator
│   └── ExhaustionEstimator
│
├── Trading
│   ├── SwingSignalGenerator
│   ├── SwingPositionManager
│   └── DirectionGate
│
└── Evaluation
    ├── TrendBacktester
    ├── WalkForwardRunner
    └── TrendMetrics
```

---

# 52. Suggested Interfaces

```text
ITrendDetector
```

Conceptually:

```text
Detect(history)
→ TrendState
```

---

```text
ITrendSegmenter
```

```text
Segment(candles)
→ IEnumerable<TrendRecord>
```

---

```text
IBootstrapEngine
```

```text
BuildDistribution(
    IReadOnlyList<TrendRecord> trends,
    int iterations)
```

---

```text
ITrendProfileBuilder
```

```text
BuildProfile(
    symbol,
    direction,
    historicalTrends)
```

---

```text
ITrendStateEstimator
```

```text
Estimate(
    currentTrend,
    symbolTrendProfile)
```

---

# 53. Phase 1 — Causal Trend Detector

Build:

```text
TrendDetector

TrendStateMachine

TrendRecord generation
```

Outputs:

```text
Number of detected trends

Bull / Bear counts

Trend start

Trend confirmation

Trend end

Price move

Duration

Confirmation delay
```

Do not implement trading yet.

---

# 54. Phase 1 Acceptance Criteria

The detector should show:

```text
No look-ahead

Reasonable trend segmentation

Reasonable number of trends

Limited overlapping/duplicate trends

Acceptable confirmation delay
```

Manually inspect a sample of historical trends visually.

If segmentation looks wrong:

```text
STOP
```

and fix it before continuing.

---

# 55. Phase 2 — Historical Trend Library

Generate:

```text
Bull trend dataset

Bear trend dataset
```

Report:

```text
Sample counts

Median move

Mean move

P05/P50/P90/P95 move

Median duration

P05/P50/P90/P95 duration

Largest trends

Longest trends
```

---

# 56. Phase 3 — Bootstrap Distribution Engine

Run:

```text
10,000 bootstrap iterations
```

for each:

```text
Symbol × Direction
```

Produce:

```text
Price quantiles

Duration quantiles

90% confidence interval
for each estimated quantile

Reliability diagnostics
```

---

# 57. Phase 4 — Swing Entry Research

Test entry thresholds:

```text
P05
P10
P15
P20
P25
```

Measure:

```text
Number of trades

Win rate

Profit factor

Net PnL

Max drawdown

Entry delay

Trend capture

False trend rate
```

---

# 58. Phase 5 — Swing Exit Research

Test:

```text
P75 protection

P90 protection

P95 exhaustion
```

alongside structural trend invalidation.

Measure:

```text
Trend capture ratio

MFE

Realized return

Exit giveback

Average R

Profit factor
```

---

# 59. Phase 6 — Joint Price-Time Model

Build:

```text
Price × Duration distribution
```

and compare:

```text
Price only
Time only
Price + Time
Joint distribution
```

for predicting trend continuation/exhaustion.

---

# 60. Phase 7 — Regime Conditioning

Only after the unconditional baseline is proven.

Potential regimes:

```text
Low volatility

Normal volatility

High volatility
```

or:

```text
Trending macro environment

Range environment

Regime transition
```

Then profiles may become:

```text
XAUUSD
Bull
High volatility
```

versus:

```text
XAUUSD
Bull
Low volatility
```

Do not start here because it fragments the sample population.

---

# 61. Phase 8 — Multi-Symbol Expansion

After validating XAUUSD, test:

```text
EURUSD
GBPUSD
USDJPY
BTCUSD
...
```

Do not assume parameters transfer automatically.

Each symbol gets its own trend population and statistical profile.

---

# 62. Phase 9 — Lower-Timeframe Tactical Integration

Connect the proven Trend Agent to the existing ML system:

```text
4H statistical trend
        ↓
direction + exhaustion state
        ↓
15m/5m/1m LightGBM
        ↓
same-direction tactical entries
```

The 4H agent becomes a directional/regime gate.

---

# 63. Phase 10 — Combined Portfolio Risk

Manage:

```text
Swing risk

Tactical risk

Symbol exposure

Portfolio exposure
```

as one system.

---

# 64. Backtest Metrics

Standard metrics:

```text
Trades

Win rate

Net PnL

Profit Factor

Average R

Median R

Max Drawdown

Sharpe

Sortino

Average hold duration
```

Trend-specific metrics:

```text
Trend Capture Ratio

Entry Delay %

Exit Giveback

False Trend Rate

Average Trend Remaining at Entry

Average Price Percentile at Entry

Average Price Percentile at Exit

Average Time Percentile at Entry

Average Time Percentile at Exit
```

---

# 65. Statistical Diagnostics

For every symbol and direction report:

```text
Trend sample count

Price distribution

Duration distribution

Skewness

Tail behaviour

Outliers

Price-duration correlation

Bootstrap uncertainty

Quantile stability
```

Trend distributions are likely to be:

```text
skewed
fat-tailed
non-Gaussian
```

so empirical methods should be preferred.

---

# 66. Do Not Use Mean ± Standard Deviation as the Main Model

Avoid assuming:

```text
TrendMove ~ Normal Distribution
```

and then using:

```text
mean ± 1.64σ
```

as the 90% range.

Prefer:

```text
empirical quantiles
+
bootstrap uncertainty
```

because this requires far fewer distributional assumptions.

---

# 67. Outliers

Do not automatically delete extreme trends.

A 20% trend may be rare but genuine.

Since large tail trends may contribute heavily to profitability, removing them automatically could destroy the behaviour we are trying to model.

For research, compare:

```text
Raw distribution
```

with:

```text
Winsorized diagnostic distribution
```

but do not modify production data without evidence.

---

# 68. V1 Should Not Include

Do not add these initially:

```text
LightGBM

LSTM

Reinforcement Learning

News

Sentiment

Order Book

Macro event data
```

This project first needs to answer:

> Does the statistical structure of 4H historical trends itself provide a tradable edge?

---

# 69. Definition of Success

Do not call the system successful merely because:

```text
Net PnL > 0
```

It should demonstrate:

```text
Profit Factor > 1 after realistic costs

Positive majority of walk-forward windows

Acceptable worst window

Acceptable max drawdown

No single regime creates nearly all profit

Stable historical quantiles

Adequate trend sample count

Reasonable bootstrap uncertainty

Meaningful Trend Capture Ratio

Reasonable Entry Delay

Bull/Bear behaviour validated separately
```

---

# 70. Initial Research Experiment

Start with:

```text
XAUUSD
4H
several years of historical data
```

Run only the causal Trend Detector.

Produce:

```text
Detected trends        ?

Bull trends            ?
Bear trends            ?

Median Bull Move       ?
Median Bear Move       ?

Median Bull Duration   ?
Median Bear Duration   ?

Bull P05/P50/P90/P95 price

Bear P05/P50/P90/P95 price

Bull P05/P50/P90/P95 duration

Bear P05/P50/P90/P95 duration
```

Also produce a sample list of detected trends for manual inspection.

Bootstrap should not begin until this segmentation is considered reasonable.

---

# 71. Recommended Development Order

```text
V0
Causal 4H Trend Detector
```

↓

```text
V1
Historical Bull/Bear Trend Library
```

↓

```text
V2
Empirical Price + Duration distributions
```

↓

```text
V3
10,000-run Bootstrap Engine
+
90% uncertainty intervals
```

↓

```text
V4
Swing Entry Research
P05 → P25
```

↓

```text
V5
Swing Exit / P75-P95 Exhaustion Research
```

↓

```text
V6
Joint Price × Time Distribution
```

↓

```text
V7
Walk-forward Swing Strategy
```

↓

```text
V8
Regime-conditioned Trend Profiles
```

↓

```text
V9
Lower-timeframe ML integration
```

↓

```text
V10
Combined Swing + Tactical Portfolio
```

---

# 72. Final Target Architecture

```text
                            MARKET DATA
                                 │
              ┌──────────────────┴──────────────────┐
              │                                     │
             4H                                15m / 5m / 1m
              │                                     │
              ▼                                     ▼
     Statistical Trend Agent                    ML Agent
              │                                     │
       Trend direction                       BUY/HOLD/SELL
       Price percentile                           probability
       Time percentile                              │
       Joint rarity                                 │
       Exhaustion                                   │
              │                                     │
              └──────────────────┬──────────────────┘
                                 ▼
                         Trading Coordinator
                                 │
                   ┌─────────────┴─────────────┐
                   ▼                           ▼
             Swing Position              Tactical Trades
                   │                           │
                   └─────────────┬─────────────┘
                                 ▼
                           Risk Manager
                                 │
                                 ▼
                              Execution
```

---

# Core Philosophy

The 4H Statistical Trend Agent answers three questions:

```text
1. Has a trend genuinely started?
```

```text
2. Where is the current trend relative to
the historical price and time behaviour
of this specific symbol?
```

```text
3. Is enough statistical continuation
potential still available to justify
holding or entering?
```

The lower-timeframe ML system eventually answers a fourth question:

```text
4. Where are the best tactical entries
inside that larger trend?
```

The final system therefore combines two different potential sources of edge:

```text
Statistical Trend Edge
+
Machine-Learning Entry Edge
```

They should be validated independently before being combined.