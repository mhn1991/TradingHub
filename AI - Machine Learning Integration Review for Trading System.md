# AI / Machine Learning Integration Review for Trading System

I want you to inspect my **entire current trading/backtesting codebase** and determine which AI/ML capabilities can realistically be added without unnecessarily redesigning the system.

Do not start implementing anything yet.

First analyse the existing architecture, including:

- Market data pipeline
- OHLC/candle models
- Indicator calculation
- Strategy interfaces
- Signal generation
- Backtesting engine
- Trade execution simulation
- Position/risk management
- Multi-timeframe support, if any
- Walk-forward testing
- Optimisation framework
- Feature switches
- Calibration framework
- Logging
- Database/persistence
- Live trading architecture, if present
- Job/task execution architecture
- Existing ML-related code
- Existing statistical analysis code

Then evaluate the following AI/ML capabilities.

---

# 1. ML Strategy Signal Filter / Meta-Labeling

Priority: **VERY HIGH**

Goal:

Keep my existing rule-based strategy responsible for generating:

```text
BUY
SELL
```

Then use ML to determine:

```text
TAKE TRADE
or
SKIP TRADE
```

Example:

```text
Strategy produces BUY
        ↓
ML evaluates market context
        ↓
P(success) = 0.78
        ↓
Take trade
```

Possible model:

```text
LightGBM
XGBoost
Logistic Regression baseline
```

Potential inputs:

```text
Strategy direction

OHLC features

RSI
CCI
ATR
EMA
MACD
Bollinger Bands

Recent returns

Candle body/wicks

Volatility

Higher-timeframe indicators
```

Target could initially be:

```text
1 = profitable strategy signal
0 = unsuccessful strategy signal
```

Evaluate:

- Is the existing strategy pipeline suitable for adding a post-signal ML filter?
- Where should this layer live?
- Can strategy signals and their subsequent outcomes already be extracted from the backtester?
- How difficult would generating a training dataset be?
- What architectural changes are required?

---

# 2. Trade Quality / Confidence Score

Priority: **VERY HIGH**

Instead of only:

```text
TAKE
SKIP
```

the model returns:

```text
P(success) = 0.73
```

Example output structure:

```text
TradePrediction
{
    ProbabilityOfSuccess
    Confidence
}
```

The strategy engine could then use:

```text
P < 0.55
→ reject

P >= 0.55
→ candidate

P >= 0.70
→ high-quality candidate
```

Evaluate whether the architecture can support probability-based signals.

---

# 3. BUY / NO-TRADE / SELL Classifier

Priority: **HIGH**

Build a standalone classifier using:

```text
OHLC
+
technical indicators
```

Output:

```text
P(SELL)
P(NO_TRADE)
P(BUY)
```

Example:

```text
SELL      0.08
NO_TRADE  0.14
BUY       0.78
```

Initial model:

```text
LightGBM
```

Evaluate whether this can coexist with existing rule-based strategies rather than replacing them.

---

# 4. Multi-Timeframe ML Features

Priority: **HIGH**

Base trading timeframe could remain:

```text
1 minute
```

while providing features from:

```text
1m
5m
15m
1h
```

Example:

```text
1m_RSI14
1m_CCI20
1m_ATR

5m_RSI14
5m_EMA20Slope

15m_EMA20Vs50
15m_ATR

1h_EMA50Slope
1h_RSI14
```

Important:

Higher timeframe features must ONLY use completed candles.

For example, if predicting at 10:37:

```text
1m  → information available at 10:37
5m  → last completed 5m candle
15m → last completed 15m candle
1h  → last completed 1h candle
```

Do not introduce look-ahead bias by using partially completed future candles.

Determine:

- Whether multi-timeframe candles already exist
- Whether they can be generated from the current market data
- How features should be aligned by timestamp
- Where timeframe aggregation belongs architecturally

---

# 5. ML Market-Regime Detection

Priority: **HIGH**

Use ML to identify conditions such as:

```text
Trending
Sideways
High volatility
Low volatility
Bullish trend
Bearish trend
Mean-reverting
Breakout environment
```

Possible implementation:

```text
Clustering
or
classification
```

Potential features:

```text
ATR
ATR change
EMA slopes
EMA separation
Bollinger width
RSI distribution
Recent returns
Price range
Volatility
```

Potential usage:

```text
Market regime
      ↓
Enable Strategy A

or

Disable Strategy B
```

Evaluate whether this could improve existing strategies without replacing them.

---

# 6. Strategy Selector

Priority: **HIGH if multiple strategies exist**

If the system contains multiple strategies:

```text
Mean Reversion
Trend Following
Breakout
Momentum
etc.
```

ML could learn which strategy works best under the current market regime.

Architecture:

```text
Market Features
      ↓
ML
      ↓
Strategy probability / ranking
      ↓
Choose or enable strategy
```

Example:

```text
Trend Strategy       0.72
Mean Reversion       0.18
Breakout             0.10
```

Evaluate whether the current strategy architecture allows strategies to be enabled/disabled dynamically.

---

# 7. Expected Return Regression

Priority: **HIGH**

Instead of predicting only whether a trade wins, use regression to predict something like:

```text
Expected Return
```

or preferably:

```text
Expected R multiple
```

Example:

```text
Strategy says BUY

ML prediction:

Expected return = +0.36%
```

or:

```text
Expected R = +0.74R
```

Use this as another trade filter.

Possible models:

```text
LightGBM Regressor
XGBoost Regressor
```

Evaluate whether trade outcomes currently contain enough information to generate regression targets.

---

# 8. Maximum Favourable / Adverse Excursion Prediction

Priority: **MEDIUM-HIGH**

For every candidate trade calculate historically:

```text
MFE = Maximum Favourable Excursion

MAE = Maximum Adverse Excursion
```

ML could predict:

```text
Expected MFE
Expected MAE
```

Example:

```text
Expected MFE = 2.1 ATR
Expected MAE = 0.7 ATR
```

This could eventually help optimise:

```text
Take Profit
Stop Loss
Trade filtering
```

Evaluate whether the backtester already records enough candle-by-candle trade information to calculate MFE/MAE.

---

# 9. ML-Assisted Stop Loss

Priority: **MEDIUM**

Instead of always:

```text
SL = 1 ATR
```

eventually predict an appropriate stop based on market context.

Potential output:

```text
Recommended Stop Distance = 0.8 ATR
```

Do NOT recommend implementing this before the basic prediction system is validated.

Evaluate only architectural feasibility.

---

# 10. ML-Assisted Take Profit

Priority: **MEDIUM**

Similar to stop-loss prediction.

Possible target:

```text
expected favourable excursion
```

Example:

```text
TP = 1.7 ATR
```

Again, evaluate feasibility but do not prioritise it over entry filtering.

---

# 11. Dynamic Confidence Threshold

Priority: **MEDIUM-HIGH**

Instead of always using:

```text
P(success) >= 0.70
```

the threshold could vary according to:

```text
Market regime
Volatility
Trading costs
Strategy
Timeframe
```

Example:

```text
High volatility:
threshold = 0.78

Normal:
threshold = 0.65
```

Determine whether this should initially be implemented as configuration/optimisation rather than another ML model.

---

# 12. Probability Calibration

Priority: **VERY HIGH**

If the model outputs:

```text
P(success) = 0.70
```

I want that probability to be meaningful.

Evaluate support for:

```text
Platt scaling
Isotonic regression
Other calibration methods
```

Metrics:

```text
Brier Score
Calibration curve
Expected Calibration Error
```

Determine how this can integrate with any existing calibration functionality in the project.

---

# 13. Feature Engineering Engine

Priority: **VERY HIGH**

Determine whether a dedicated ML feature layer should exist.

Example:

```text
IMarketFeatureProvider
```

or:

```text
IFeatureCalculator
```

Potential features:

### OHLC

```text
Return1
Return3
Return5
Return10

Range

BodyPercentage

UpperWickPercentage

LowerWickPercentage

PositionInRange
```

### Trend

```text
CloseVsEMA20
EMA5VsEMA20
EMA20VsEMA50
EMA20Slope
EMA50Slope
```

### Momentum

```text
RSI
RSIChange

CCI
CCIChange

MACDHistogram
MACDHistogramChange
```

### Volatility

```text
ATR
ATRPercentage

BollingerWidth
```

All features should be reproducible identically during:

```text
training
backtesting
live trading
```

Avoid separate implementations.

---

# 14. Cross-Timeframe Features

Priority: **HIGH**

Possible examples:

```text
RSI_1m - RSI_15m

CCI_1m - CCI_15m

ATR_1m / ATR_1h

EMA trend agreement

Number of bullish timeframes

Number of bearish timeframes
```

Example:

```text
BullishTimeframeCount = 3
BearishTimeframeCount = 1
```

Evaluate where these should be calculated.

---

# 15. Feature Importance

Priority: **HIGH**

Support:

```text
LightGBM feature importance
```

and preferably:

```text
SHAP
```

I want to know things such as:

```text
ATR14            18%
EMA20Slope       14%
CCI20            12%
RSI14             9%
15mTrend          8%
```

Determine how feature-importance results should be stored and exposed.

---

# 16. Feature Ablation Testing

Priority: **VERY HIGH**

I want the research system to automatically test:

```text
All features
```

versus:

```text
Without RSI

Without CCI

Without MACD

Without EMA features

Without higher timeframe features
```

Then compare:

```text
PnL
Profit Factor
Sharpe
Max Drawdown
Precision
Recall
Number of Trades
```

Determine whether my existing experimentation framework can support this.

---

# 17. Model Ablation

Priority: **HIGH**

Compare simple and complex models using exactly the same dataset.

For example:

```text
Logistic Regression

Random Forest

LightGBM

XGBoost
```

LightGBM/XGBoost should only be accepted if they outperform the simple baseline out-of-sample.

---

# 18. Walk-Forward ML Training

Priority: **CRITICAL**

Do NOT use random train/test splitting.

The system must support:

```text
Train → Validation → Test
```

chronologically.

Example:

```text
Train:
Jan-Mar

Validation:
April

Test:
May
```

then:

```text
Train:
Feb-Apr

Validation:
May

Test:
June
```

Evaluate how this can integrate into my existing walk-forward framework.

---

# 19. Leakage Prevention

Priority: **CRITICAL**

Inspect the entire proposed ML architecture for:

```text
Look-ahead bias
Target leakage
Future indicator values
Future candles
Incorrect timeframe alignment
Scaler leakage
Normalization leakage
Training/test overlap
Overlapping target windows
```

Create explicit safeguards against these.

---

# 20. Purging / Embargo for Time-Series ML

Priority: **HIGH**

Because adjacent training/test samples may share future target information, investigate whether the project should support:

```text
Purged validation
Embargo periods
```

particularly when the prediction horizon spans multiple candles.

Explain whether this is necessary for the current strategy/backtesting setup.

---

# 21. Model Persistence

Priority: **HIGH**

Determine how trained models should be:

```text
saved
versioned
loaded
associated with experiments
associated with instruments
associated with timeframes
```

Example metadata:

```text
ModelVersion

TrainingStart
TrainingEnd

Instrument

Timeframe

FeatureSetVersion

Hyperparameters

PerformanceMetrics
```

---

# 22. Dataset Versioning

Priority: **HIGH**

ML experiment results are meaningless if we cannot reproduce the exact training data.

Evaluate introducing:

```text
DatasetVersion

FeatureSetVersion

LabelDefinitionVersion

ModelVersion
```

---

# 23. Experiment Reproducibility

Priority: **VERY HIGH**

Every ML experiment should record:

```text
Model
Hyperparameters

Feature list

Feature parameters

Target definition

Prediction horizon

Training period

Validation period

Test period

Random seed where applicable

Probability threshold

Trading costs

Result metrics
```

Determine whether my existing database/logging architecture can support this.

---

# 24. Model Training Pipeline

Priority: **VERY HIGH**

Evaluate an architecture similar to:

```text
Market Data
     ↓
Feature Engine
     ↓
Label Generator
     ↓
Dataset Builder
     ↓
Train / Validation split
     ↓
Model Trainer
     ↓
Calibration
     ↓
Model Evaluator
     ↓
Backtest
     ↓
Experiment Results
```

Identify which of these pieces already exist.

---

# 25. Separate Training From Inference

Priority: **HIGH**

The live trading engine should NOT train models during normal signal evaluation.

Preferred architecture:

```text
Offline / research:

Data
 ↓
Training
 ↓
Model File
```

Then:

```text
Live:

New Candle
 ↓
Feature Calculation
 ↓
Load trained model
 ↓
Inference
 ↓
Signal
```

Evaluate whether the current process boundaries support this cleanly.

---

# 26. Non-Blocking Live ML Inference

Priority: **HIGH**

The live system must not block candle processing because of ML.

Inspect whether prediction can safely run inside the existing async/task architecture.

Determine:

- Expected inference cost
- Whether background workers are required
- Whether prediction ordering matters
- How failures/timeouts should be handled

LightGBM inference should generally be fast enough that heavy infrastructure may not be necessary.

---

# 27. Model Drift Detection

Priority: **MEDIUM-HIGH**

Eventually track whether model performance changes over time.

Examples:

```text
Prediction distribution changes

Feature distribution changes

BUY precision falls

SELL precision falls

Calibration degrades

PnL deteriorates
```

Possible output:

```text
Model healthy

Model warning

Retraining recommended
```

Evaluate what statistics should be recorded now to make this possible later.

---

# 28. Automated Retraining

Priority: **LATER**

Eventually allow something like:

```text
Train every month
```

or:

```text
Retrain when drift threshold exceeded
```

Do NOT recommend implementing automatic retraining in the first ML version.

Evaluate architectural readiness only.

---

# 29. Ensemble Models

Priority: **LATER**

Possible future architecture:

```text
LightGBM
      ┐
XGBoost
      ├─> Ensemble
Logistic
      ┘
```

or:

```text
1m model
5m model
15m model
      ↓
Meta model
```

Evaluate whether the architecture should be designed to allow this later without implementing it now.

---

# 30. Separate Model Per Strategy

Priority: **MEDIUM**

Compare:

```text
One global trade-quality model
```

versus:

```text
Model A → Strategy A

Model B → Strategy B

Model C → Strategy C
```

Recommend which matches the current codebase best.

---

# 31. Global Model With Strategy ID

Priority: **MEDIUM**

Alternative:

```text
StrategyId
```

becomes an ML feature.

Example:

```text
StrategyId = MeanReversion
```

alongside market conditions.

The model can learn different behaviour for different strategies.

Compare this against separate models.

---

# 32. Instrument-Specific vs Global Models

Priority: **MEDIUM-HIGH**

Determine whether the architecture should support:

```text
EURUSD model
GBPUSD model
BTCUSD model
```

versus a global model trained across instruments.

If global models are possible, ensure price features are appropriately normalized.

---

# 33. Time-of-Day Features

Priority: **MEDIUM-HIGH**

Potential features:

```text
Hour
DayOfWeek

London session
New York session
Asian session

MinutesSinceSessionOpen
```

Do not encode future knowledge.

Evaluate whether these could help without substantially complicating V1.

---

# 34. Trading Cost Awareness

Priority: **HIGH**

The ML target should ideally know whether expected movement is large enough to overcome:

```text
Spread
Commission
Slippage
```

A prediction that expects:

```text
+0.01%
```

may be worthless after costs.

Evaluate whether costs should be included in:

```text
label generation
trade outcome
backtest evaluation
```

---

# 35. Class Imbalance Handling

Priority: **HIGH**

For:

```text
BUY
SELL
NO_TRADE
```

the classes may be unbalanced.

Evaluate:

```text
class weights
sampling
threshold adjustment
```

Do not rely on accuracy alone.

---

# 36. ML Evaluation Metrics

Priority: **CRITICAL**

For classification record:

```text
Precision per class

Recall per class

F1

Macro F1

PR-AUC

ROC-AUC where appropriate

Confusion matrix

Brier score

Calibration
```

But trading performance remains the final objective.

---

# 37. Trading Evaluation

Priority: **CRITICAL**

Compare every ML-enhanced strategy against its original baseline.

Example:

```text
                  BASE      BASE + ML

Trades
Win Rate
Net PnL
Profit Factor
Expectancy
Sharpe
Sortino
Max Drawdown
Average Winner
Average Loser
```

ML should not be accepted merely because classification accuracy improves.

---

# 38. Statistical Significance / Stability

Priority: **HIGH**

Determine whether the existing research framework can evaluate:

```text
Bootstrap

Monte Carlo

Confidence intervals

Trade resampling

Walk-forward stability
```

I want to avoid concluding that ML helps based on one unusually profitable test period.

---

# 39. Strategy + ML A/B Testing

Priority: **VERY HIGH**

The framework should make it easy to compare:

```text
Strategy
```

against:

```text
Strategy + ML Filter
```

using the same:

```text
market data
period
spread
slippage
risk settings
```

---

# 40. Explainability Per Trade

Priority: **MEDIUM**

Eventually I would like to inspect a prediction such as:

```text
Strategy = BUY

P(success) = 0.78

Main positive factors:

15m trend bullish
ATR normal
CCI recovering
EMA slope positive

Negative factors:

1m RSI overbought
```

Evaluate whether SHAP or another method can support this without making live inference too complicated.

---

# 41. ML Decision Logging

Priority: **HIGH**

For every ML-evaluated trade record:

```text
Timestamp

Strategy

Direction

Model Version

Features

Probability

Threshold

Decision

Actual Outcome
```

This will be useful for debugging and future training.

Determine where this should live in the current persistence system.

---

# 42. Offline Replay

Priority: **HIGH**

I should be able to replay historical data through a saved model and reproduce predictions.

Evaluate whether the current backtest architecture supports this.

---

# 43. Shadow Mode

Priority: **HIGH before live use**

When eventually running live, allow ML to make predictions without affecting actual trades.

Example:

```text
Strategy says BUY

ML says SKIP

Actual system:
still follows original strategy

Log:
"ML would have skipped"
```

This makes it possible to validate ML against live data before giving it control.

Determine how difficult this is with the current architecture.

---

# 44. Anomaly / Outlier Detection

Priority: **LOW-MEDIUM**

Potentially detect unusual environments such as:

```text
Extreme volatility

Flash move

Data corruption

Abnormal spread

Unusual candle behaviour
```

Possible use:

```text
disable trading temporarily
```

Evaluate whether simple deterministic rules would be preferable to ML here.

---

# 45. Unsupervised Market Clustering

Priority: **LATER**

Potential models:

```text
K-Means
Gaussian Mixture
HDBSCAN
```

Potential goal:

Automatically discover market regimes.

Do not prioritise this over supervised meta-labeling.

---

# 46. LSTM / GRU Sequence Models

Priority: **LATER**

Evaluate whether sequence models could eventually consume:

```text
last 100 candles
```

directly.

Do NOT recommend them for the first implementation unless there is a clear architectural or performance reason.

The first baseline should use tabular models.

---

# 47. Transformer Models

Priority: **VERY LATE**

Evaluate architecture compatibility only.

Do not recommend implementing Transformers simply because they are more sophisticated.

They must beat simpler approaches out-of-sample to justify the complexity.

---

# 48. Reinforcement Learning

Priority: **NOT FOR V1**

Evaluate whether there is any legitimate future use, but do NOT recommend RL as the starting architecture.

Potential issues include:

```text
overfitting
unstable training
reward design
simulator mismatch
large data requirements
difficult debugging
```

---

# 49. AI-Generated Strategy Discovery

Priority: **LOW / RESEARCH**

Potential future feature:

AI proposes candidate rules such as:

```text
RSI condition
+
EMA trend
+
ATR regime
```

The research engine then independently backtests them.

The AI must never be allowed to claim profitability without proper out-of-sample validation.

Evaluate whether the existing experiment framework could eventually support generated strategy hypotheses.

---

# 50. AI Research Assistant

Priority: **MEDIUM**

Potentially use an LLM to analyse completed experiments.

Input:

```text
walk-forward results
feature importance
ablation results
Monte Carlo
calibration
trade statistics
```

Output:

```text
Which features helped

Where model degraded

Which regimes performed badly

What experiment should be run next
```

This should be a research assistant, NOT the component that executes trades.

---

# Recommended Implementation Priority

After examining the actual repository, tell me whether you agree with this order:

## Phase 1 — Infrastructure

```text
Feature Engine

Dataset Builder

Label Generator

Model Interfaces

Experiment Metadata

Leakage Protection
```

## Phase 2 — First ML Baseline

```text
Logistic Regression

LightGBM

Strategy Meta-Label Filter
```

## Phase 3 — Evaluation

```text
Walk Forward

Probability Calibration

Ablation

Baseline vs ML comparison

Trading metrics
```

## Phase 4 — Multi-Timeframe

```text
1m
5m
15m
1h

Cross-timeframe features
```

## Phase 5 — Additional Predictions

```text
Expected Return

MFE

MAE
```

## Phase 6 — Advanced Strategy Intelligence

```text
Market regime detection

Strategy selector

Multiple models

Ensemble models
```

## Phase 7 — Advanced Research

```text
LSTM

Transformers

Automatic retraining

Drift detection

AI research assistant
```

---

# Architecture I Currently Prefer

Evaluate whether my existing project can reasonably evolve toward:

```text
                    Market Data
                         │
                         ▼
                  Indicator Engine
                         │
                         ▼
                    Feature Engine
                         │
             ┌───────────┴───────────┐
             │                       │
             ▼                       ▼
       Existing Strategy        ML Features
             │                       │
             ▼                       │
       Candidate Signal              │
             │                       │
             └───────────┬───────────┘
                         ▼
                    ML Meta Model
                       LightGBM
                         │
                         ▼
                   P(success)
                         │
                         ▼
                 Confidence Filter
                         │
                 ┌───────┴────────┐
                 ▼                ▼
               SKIP              TAKE
                                   │
                                   ▼
                            Risk Management
                                   │
                                   ▼
                              Trade Engine
```

The existing strategy should remain usable without ML.

I want the architecture to support:

```text
ML Enabled = true
```

or:

```text
ML Enabled = false
```

without changing strategy behaviour.

---

# Important Design Requirement

Avoid tightly coupling LightGBM or any particular ML library to the trading engine.

Prefer an abstraction such as:

```text
ITradingModel
{
    Predict(features)
}
```

or equivalent for the language/framework used in this repository.

Then implementations could eventually be:

```text
LightGbmTradingModel

XGBoostTradingModel

LogisticTradingModel

OnnxTradingModel
```

The trading engine should not care which model is underneath.

---

# What I Want From You

After inspecting the repository, produce a report with the following sections.

## A. Existing Architecture

Explain what parts already exist that can support ML.

## B. Missing Infrastructure

List what needs to be added.

## C. AI/ML Opportunities

For every item above classify it as:

```text
READY NOW

SMALL CHANGE

MODERATE CHANGE

MAJOR CHANGE

NOT RECOMMENDED
```

## D. Integration Points

Reference the actual:

```text
projects
folders
classes
interfaces
methods
```

where each capability should integrate.

Do not give generic architecture advice when you can reference actual code.

## E. Difficulty

Score each feature:

```text
1 = trivial
2 = easy
3 = moderate
4 = difficult
5 = major architectural work
```

## F. Expected Value

Score:

```text
1 = unlikely to provide much value

5 = potentially very valuable
```

## G. Risk of Overfitting

Score:

```text
LOW
MEDIUM
HIGH
VERY HIGH
```

## H. Data Requirements

Explain what historical data is required.

## I. Leakage Risks

Identify specific leakage risks based on the actual implementation.

## J. Recommended First Implementation

Tell me exactly which ONE ML feature should be implemented first.

My current preference is:

```text
Existing Strategy
       ↓
LightGBM Meta-Label Filter
       ↓
TAKE / SKIP
```

but challenge this if the repository suggests a better starting point.

## K. Proposed Classes / Interfaces

Show the architecture you would introduce.

For example:

```text
IMLFeatureProvider
ITradingModel
IModelTrainer
ILabelGenerator
IDatasetBuilder
IModelEvaluator
IModelRepository
```

But reuse existing abstractions wherever possible rather than creating unnecessary new ones.

## L. Concrete Implementation Roadmap

Give me implementation stages such as:

```text
Step 1
Step 2
Step 3
...
```

Each step should be small enough to implement and test independently.

## M. Files That Need Modification

Produce a table:

| File/Class | Change | Reason |
|---|---|---|

## N. New Files Required

Produce:

| New File/Class | Responsibility |
|---|---|

## O. Tests Required

Include:

```text
unit tests

integration tests

look-ahead/leakage tests

dataset tests

model tests

backtest comparison tests

determinism/reproducibility tests
```

## P. Final Recommendation

At the end give me a ranked list:

```text
1. Implement now
2. Implement next
3. Useful later
4. Experimental
5. Do not implement yet
```

Most importantly:

**Base your conclusions on the actual repository rather than on assumptions about how a trading system normally works.**

Do not modify any code until the assessment is complete.