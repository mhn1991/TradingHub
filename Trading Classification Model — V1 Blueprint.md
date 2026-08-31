# Trading Classification Model — V1 Blueprint

## 1. Objective

Build a machine-learning classifier that uses only:

- Open
- High
- Low
- Close
- Technical indicators calculated from OHLC

to predict one of three outcomes:

```text
SELL
NO_TRADE
BUY
```

The model should output probabilities rather than only a final class.

Example:

```text
SELL      0.08
NO_TRADE  0.17
BUY       0.75
```

The trading engine then decides whether confidence is high enough to enter a trade.

---

# 2. Overall Architecture

```text
Historical OHLC
      │
      ▼
Feature Calculation
      │
      ├── Returns
      ├── Candle features
      ├── RSI
      ├── CCI
      ├── ATR
      ├── EMA
      ├── MACD
      ├── Bollinger Bands
      └── Momentum
      │
      ▼
Feature Dataset
      │
      ▼
Target / Label Generation
      │
      ├── SELL
      ├── NO_TRADE
      └── BUY
      │
      ▼
Walk-Forward Training
      │
      ▼
LightGBM Classifier
      │
      ▼
Probability Output
      │
      ▼
Confidence Filter
      │
      ▼
Trading Signal
```

---

# 3. Input Data

Minimum candle structure:

```text
Timestamp
Open
High
Low
Close
```

Example:

```text
2026-01-01 10:00
Open  = 1.17420
High  = 1.17465
Low   = 1.17390
Close = 1.17450
```

Do not use future candle information when calculating features.

Everything available for candle `t` must have been known at or before candle `t`.

---

# 4. Raw OHLC Features

Do not give the model only raw prices.

Absolute prices often provide little useful information because the price level changes over time.

Create normalized features.

## Returns

```text
return_1
return_3
return_5
return_10
return_20
```

Example:

```text
return_5 =
(Close[t] - Close[t-5])
/
Close[t-5]
```

---

# 5. Candle Structure Features

Calculate characteristics of every candle.

## Candle Range

```text
range = High - Low
```

## Body Size

```text
body = abs(Close - Open)
```

## Body Percentage

```text
body_pct =
abs(Close - Open)
/
(High - Low)
```

## Upper Wick

```text
upper_wick =
High - max(Open, Close)
```

## Lower Wick

```text
lower_wick =
min(Open, Close) - Low
```

## Wick Percentages

```text
upper_wick_pct = upper_wick / range
lower_wick_pct = lower_wick / range
```

## Candle Direction

```text
Close > Open → +1
Close < Open → -1
Close = Open → 0
```

These features help the model understand the structure of the candle without manually defining candlestick patterns.

---

# 6. Multi-Candle Price Features

The model should also know what has happened recently.

Add features such as:

```text
highest_high_5
lowest_low_5

highest_high_10
lowest_low_10

highest_high_20
lowest_low_20
```

Then normalize the current close relative to those ranges:

```text
position_in_range_20 =
(Close - lowest_low_20)
/
(highest_high_20 - lowest_low_20)
```

Example:

```text
0.0 = bottom of range
0.5 = middle
1.0 = top of range
```

This can be very useful.

---

# 7. Recommended Indicators

Start with a relatively small indicator set.

Do not initially calculate 100 different indicators.

## Trend

```text
EMA 5
EMA 10
EMA 20
EMA 50
EMA 100
```

But avoid feeding only the EMA price itself.

Calculate relationships such as:

```text
close_vs_ema20
ema5_vs_ema20
ema20_vs_ema50
```

Example:

```text
close_vs_ema20 =
(Close - EMA20)
/
EMA20
```

---

## EMA Slope

Calculate whether an EMA is rising or falling.

```text
ema20_slope_1
ema20_slope_5

ema50_slope_1
ema50_slope_5
```

Example:

```text
ema20_slope_5 =
(EMA20[t] - EMA20[t-5])
/
EMA20[t-5]
```

---

## RSI

Recommended:

```text
RSI 7
RSI 14
RSI 21
```

Also calculate change:

```text
rsi14_change_1
rsi14_change_5
```

This distinguishes:

```text
RSI = 30 and falling
```

from:

```text
RSI = 30 and recovering
```

---

## CCI

Recommended:

```text
CCI 14
CCI 20
CCI 50
```

Also:

```text
cci20_change_1
cci20_change_5
```

---

## ATR

Recommended:

```text
ATR 14
ATR 20
```

Normalize ATR:

```text
atr_pct = ATR14 / Close
```

This gives the model information about volatility.

---

## MACD

Use:

```text
MACD
MACD Signal
MACD Histogram
```

The histogram is particularly useful.

Also calculate:

```text
macd_histogram_change
```

---

## Bollinger Bands

Use:

```text
BB Upper
BB Middle
BB Lower
```

But calculate normalized features:

```text
bb_position
bb_width
```

For example:

```text
bb_position =
(Close - BB_Lower)
/
(BB_Upper - BB_Lower)
```

---

# 8. Initial Feature Set

The first version could contain roughly 40–60 features.

Example:

```text
# Price Returns

return_1
return_3
return_5
return_10
return_20

# Candle

body_pct
upper_wick_pct
lower_wick_pct
range_pct
candle_direction

# Price Range

position_range_5
position_range_10
position_range_20
position_range_50

# Trend

close_vs_ema5
close_vs_ema10
close_vs_ema20
close_vs_ema50

ema5_vs_ema20
ema10_vs_ema20
ema20_vs_ema50

ema20_slope_1
ema20_slope_5
ema50_slope_5

# RSI

rsi7
rsi14
rsi21

rsi14_change_1
rsi14_change_5

# CCI

cci14
cci20
cci50

cci20_change_1
cci20_change_5

# ATR

atr14_pct
atr20_pct

# MACD

macd
macd_signal
macd_histogram
macd_histogram_change

# Bollinger

bb_position
bb_width
```

Start here.

Do not add more features until this baseline has been tested.

---

# 9. Target Generation

Target generation is one of the most important parts of the system.

Do not simply use:

```text
next candle green → BUY
next candle red   → SELL
```

That contains too much noise.

Instead predict whether price makes a meaningful move over the next `N` candles.

Example:

```text
Prediction Horizon = 10 candles
```

Calculate:

```text
future_return =
(Close[t + 10] - Close[t])
/
Close[t]
```

---

# 10. Three-Class Labels

Use:

```text
0 = SELL
1 = NO_TRADE
2 = BUY
```

For example:

```text
future_return > +threshold
    → BUY

future_return < -threshold
    → SELL

otherwise
    → NO_TRADE
```

Avoid using an arbitrary fixed threshold initially.

Use volatility.

---

# 11. ATR-Based Labels

A better target is:

```text
threshold =
ATR[t] × multiplier
```

For example:

```text
multiplier = 0.75
```

Then:

```text
future_price_change > +0.75 × ATR
    → BUY

future_price_change < -0.75 × ATR
    → SELL

otherwise
    → NO_TRADE
```

This automatically adapts to market volatility.

During low volatility:

```text
smaller move required
```

During high volatility:

```text
larger move required
```

---

# 12. Better Target — Maximum Excursion

Later, improve the target by looking at the maximum movement during the prediction window rather than only the final close.

For the next 10 candles calculate:

```text
future_high =
max(High[t+1 ... t+10])

future_low =
min(Low[t+1 ... t+10])
```

Then:

```text
up_move =
future_high - Close[t]

down_move =
Close[t] - future_low
```

This lets the target detect whether a meaningful opportunity occurred even if price subsequently retraced.

For V1, however, start with future close.

---

# 13. Model

Primary model:

```text
LightGBM Multiclass Classifier
```

Classes:

```text
SELL
NO_TRADE
BUY
```

Output:

```text
P(SELL)
P(NO_TRADE)
P(BUY)
```

Example:

```text
SELL      0.05
NO_TRADE  0.15
BUY       0.80
```

---

# 14. Baseline Model

Also train:

```text
Multinomial Logistic Regression
```

Do not expect it to outperform LightGBM.

Its job is to provide a baseline.

If sophisticated ML cannot outperform logistic regression, investigate the features and target before adding more complex models.

---

# 15. Dataset Split

Never randomly shuffle trading data.

Do not do:

```text
Random 80% train
Random 20% test
```

That destroys the temporal structure.

Use chronological splitting.

Example:

```text
TRAIN
──────────────
January
February
March
April

VALIDATION
──────
May

TEST
──────
June
```

---

# 16. Walk-Forward Training

The final evaluation should use walk-forward testing.

Example:

```text
Window 1

Train:
Jan → Mar

Validate:
April

Test:
May
```

Then:

```text
Window 2

Train:
Feb → Apr

Validate:
May

Test:
June
```

Then:

```text
Window 3

Train:
Mar → May

Validate:
June

Test:
July
```

Continue through the entire dataset.

This provides a much more realistic simulation of live trading.

---

# 17. Feature Scaling

LightGBM generally does not require standardization.

However, normalized features should still be preferred.

Good:

```text
(Close - EMA20) / EMA20

ATR / Close

body / range
```

Less desirable:

```text
EMA20 = 1.17434

ATR = 0.00073
```

Normalized features generalize better across different price environments and instruments.

---

# 18. Class Imbalance

You may discover:

```text
SELL      15%
NO_TRADE  70%
BUY       15%
```

This is normal.

Do not remove NO_TRADE simply to balance the classes.

Instead evaluate:

```text
Class weights

Precision

Recall

F1

PR-AUC

Confusion matrix
```

Pay particular attention to BUY and SELL precision.

---

# 19. Trading Decision Layer

Do not automatically execute the class with the highest probability.

For example:

```text
BUY       0.41
NO_TRADE  0.35
SELL      0.24
```

The classifier technically predicts BUY.

The trading system should output:

```text
NO TRADE
```

because confidence is too low.

---

# 20. Confidence Threshold

Start with something such as:

```text
BUY if:

P(BUY) >= 0.70
```

and:

```text
SELL if:

P(SELL) >= 0.70
```

Otherwise:

```text
NO TRADE
```

Architecture:

```text
          LightGBM
              │
              ▼
      ┌───────────────┐
      │ SELL     0.08 │
      │ HOLD     0.13 │
      │ BUY      0.79 │
      └───────────────┘
              │
              ▼
     BUY > threshold?
              │
          Yes │
              ▼
             BUY
```

The threshold should eventually be optimized on validation data, not on test data.

---

# 21. Do Not Use Accuracy as the Main Metric

Suppose the dataset contains:

```text
NO_TRADE = 75%
BUY      = 13%
SELL     = 12%
```

A model that always predicts:

```text
NO_TRADE
```

has:

```text
75% accuracy
```

but is useless.

Track:

```text
BUY precision
BUY recall

SELL precision
SELL recall

Macro F1
PR-AUC
Confusion matrix
```

Then evaluate actual trading performance.

---

# 22. Trading Metrics

Every model evaluation should ultimately include:

```text
Number of trades

Winning trades
Losing trades

Win rate

Average winner
Average loser

Profit factor

Net PnL

Maximum drawdown

Sharpe ratio

Sortino ratio

Expectancy per trade
```

Also test after:

```text
Spread
Commission
Slippage
```

A classifier that looks good statistically but loses money after costs is not useful.

---

# 23. Feature Importance

After training LightGBM, extract:

```text
Feature Importance
```

and preferably:

```text
SHAP values
```

Example:

```text
Feature              Importance

ATR14_pct               1
EMA20_slope             2
CCI20                   3
RSI14                   4
position_range_20       5
MACD_histogram          6
```

Do not automatically assume that the most important features are genuinely predictive.

Validate them across multiple walk-forward periods.

---

# 24. Feature Ablation

Once the baseline works, remove groups of features and retrain.

Example:

```text
ALL FEATURES
PnL = X

Remove RSI
PnL = ?

Remove CCI
PnL = ?

Remove MACD
PnL = ?

Remove EMA
PnL = ?

Remove candle structure
PnL = ?
```

This reveals whether an indicator actually contributes useful information.

---

# 25. Avoid Look-Ahead Bias

Every feature at time `t` must only use:

```text
data <= t
```

Never:

```text
data > t
```

For example:

```text
EMA[t]
RSI[t]
ATR[t]
```

are acceptable.

Future high/low/close can only be used for generating the training label.

They must never appear in the model input.

---

# 26. Training Pipeline

```text
Load OHLC
      │
      ▼
Sort chronologically
      │
      ▼
Calculate indicators
      │
      ▼
Calculate normalized features
      │
      ▼
Remove warm-up rows
      │
      ▼
Generate future labels
      │
      ▼
Remove rows without future target
      │
      ▼
Create chronological train/validation/test
      │
      ▼
Train LightGBM
      │
      ▼
Probability predictions
      │
      ▼
Apply confidence threshold
      │
      ▼
Run backtest
      │
      ▼
Calculate metrics
```

---

# 27. Live Prediction Pipeline

Live execution should use exactly the same feature calculation code as training.

```text
New candle closes
      │
      ▼
Update indicator values
      │
      ▼
Generate features
      │
      ▼
LightGBM
      │
      ▼
P(SELL)
P(NO_TRADE)
P(BUY)
      │
      ▼
Confidence Filter
      │
      ▼
Signal
```

Never implement separate indicator logic for training and live trading.

Use one shared implementation.

---

# 28. Recommended V1 Configuration

Start with:

```text
Model:
LightGBM

Classes:
SELL
NO_TRADE
BUY

Prediction Horizon:
10 candles

Target:
Future close movement

Threshold:
0.75 ATR

Minimum Probability:
0.70

Input:
OHLC + technical indicators

Features:
~40–60

Training:
Walk-forward

Primary ML Metrics:
BUY precision
SELL precision
Macro F1

Primary Trading Metrics:
Profit Factor
Net PnL
Maximum Drawdown
Expectancy
```

---

# 29. V1 Indicator Set

Keep the first experiment restricted to:

```text
EMA
RSI
CCI
ATR
MACD
Bollinger Bands
```

plus OHLC-derived features.

Avoid adding:

```text
Order book
Volume
News
Sentiment
Fundamentals
Economic calendar
Liquidity sweep detection
Fair value gaps
Support/resistance ML
CNN
LSTM
Transformer
```

until the baseline has been properly evaluated.

---

# 30. Suggested Software Components

Separate the system into components.

```text
MarketData
    │
    ▼
FeatureEngine
    │
    ▼
LabelGenerator
    │
    ▼
DatasetBuilder
    │
    ▼
ModelTrainer
    │
    ▼
ModelEvaluator
    │
    ▼
BacktestEngine
    │
    ▼
TradingSignalEngine
```

Suggested interfaces:

```text
IFeatureCalculator

ILabelGenerator

ITradingModel

IModelTrainer

IModelEvaluator

ISignalGenerator
```

---

# 31. Feature Record

Conceptually:

```text
FeatureRow
{
    Timestamp

    Return1
    Return3
    Return5
    Return10

    BodyPct
    UpperWickPct
    LowerWickPct

    Ema5Vs20
    Ema20Vs50
    Ema20Slope

    Rsi7
    Rsi14

    Cci20

    Atr14Pct

    MacdHistogram

    BbPosition
    BbWidth

    Label
}
```

---

# 32. Prediction Object

The model should return something like:

```text
Prediction
{
    SellProbability
    NoTradeProbability
    BuyProbability
}
```

Example:

```text
SellProbability    = 0.07
NoTradeProbability = 0.14
BuyProbability     = 0.79
```

The model itself should not execute the trade.

---

# 33. Signal Generator

Keep decision logic outside the ML model.

Conceptually:

```text
if BuyProbability >= BuyThreshold
    return BUY

if SellProbability >= SellThreshold
    return SELL

return NO_TRADE
```

This separation is important because later you can modify trading rules without retraining the model.

---

# 34. Experiment Configuration

Do not hard-code important values.

Use configuration such as:

```text
PredictionHorizon = 10

AtrTargetMultiplier = 0.75

BuyProbabilityThreshold = 0.70

SellProbabilityThreshold = 0.70

EmaPeriods = [5, 10, 20, 50]

RsiPeriods = [7, 14, 21]

CciPeriods = [14, 20, 50]

AtrPeriods = [14, 20]
```

This makes controlled experiments much easier.

---

# 35. Experiment Order

Develop the system in this order:

### Experiment 1

OHLC-derived features only.

```text
returns
candle body
wicks
range position
```

### Experiment 2

Add:

```text
EMA
```

### Experiment 3

Add:

```text
RSI
CCI
```

### Experiment 4

Add:

```text
ATR
```

### Experiment 5

Add:

```text
MACD
Bollinger Bands
```

Compare each experiment against the previous one.

Do not simply throw every indicator into the first model.

---

# 36. Definition of Success

The model should not be considered successful because:

```text
Accuracy = 70%
```

Instead it should demonstrate consistent out-of-sample performance across several walk-forward periods.

A good result looks more like:

```text
Positive expectancy

Profit Factor > 1

Acceptable maximum drawdown

Stable results across periods

Profitable after trading costs

BUY/SELL precision higher than baseline

No single period responsible for all profit
```

Only after this V1 produces stable results should additional market information or more complex models be considered.