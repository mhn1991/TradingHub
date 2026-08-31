using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Tracks RSI momentum and compares RSI values at confirmed price swing pivots.
/// A relationship is emitted only after the right-hand swing candles have closed,
/// so it does not introduce look-ahead bias.
/// </summary>
public sealed class RsiAnalysisState
{
    private readonly RingBuffer<RsiSample> _samples;
    private readonly RingBuffer<RsiPivot> _highPivots;
    private readonly RingBuffer<RsiPivot> _lowPivots;
    private readonly int _momentumLookback;
    private readonly decimal _momentumThreshold;
    private readonly decimal _minimumRsiDifference;
    private readonly decimal _minimumPriceDifferenceAtr;
    private readonly int _signalLifetimeCandles;
    private RsiRelationshipSnapshot? _latestRelationship;
    private int _relationshipAge;

    public RsiAnalysisState(
        int sampleCapacity = 2_000,
        int pivotCapacity = 500,
        int momentumLookback = 3,
        decimal momentumThreshold = 1m,
        decimal minimumRsiDifference = 2m,
        decimal minimumPriceDifferenceAtr = 0.05m,
        int signalLifetimeCandles = 50)
    {
        if (sampleCapacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCapacity));
        }

        if (pivotCapacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(pivotCapacity));
        }

        if (momentumLookback < 1 || momentumLookback >= sampleCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(momentumLookback));
        }

        if (momentumThreshold < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(momentumThreshold));
        }

        if (minimumRsiDifference < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRsiDifference));
        }

        if (minimumPriceDifferenceAtr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPriceDifferenceAtr));
        }

        if (signalLifetimeCandles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(signalLifetimeCandles));
        }

        _samples = new RingBuffer<RsiSample>(sampleCapacity);
        _highPivots = new RingBuffer<RsiPivot>(pivotCapacity);
        _lowPivots = new RingBuffer<RsiPivot>(pivotCapacity);
        _momentumLookback = momentumLookback;
        _momentumThreshold = momentumThreshold;
        _minimumRsiDifference = minimumRsiDifference;
        _minimumPriceDifferenceAtr = minimumPriceDifferenceAtr;
        _signalLifetimeCandles = signalLifetimeCandles;
    }

    public RsiAnalysisSnapshot Current { get; private set; } = RsiAnalysisSnapshot.Empty;

    public RsiAnalysisSnapshot Update(
        Candle candle,
        decimal? rsi,
        IReadOnlyList<SwingPoint> confirmedSwings,
        decimal? atr)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentNullException.ThrowIfNull(confirmedSwings);

        bool isNewRelationship = false;
        if (_latestRelationship is not null)
        {
            _relationshipAge++;
            if (_relationshipAge > _signalLifetimeCandles)
            {
                _latestRelationship = null;
                _relationshipAge = 0;
            }
        }

        if (rsi is decimal currentRsi)
        {
            // Captured BEFORE the add: afterwards _samples.Latest is this bar, not the previous
            // one. Same ordering as CciAnalysisState.
            decimal? previousRsi = _samples.Count == 0 ? null : _samples.Latest.Value;
            _samples.Add(new RsiSample(candle.OpenTime, currentRsi));
            foreach (SwingPoint swing in confirmedSwings)
            {
                if (!TryFindRsi(swing.PivotTime, out decimal pivotRsi))
                {
                    continue;
                }

                RsiRelationshipSnapshot? relationship = AddPivotAndDetect(
                    new RsiPivot(swing, pivotRsi),
                    atr);
                if (relationship is not null &&
                    (_latestRelationship is null ||
                     relationship.Strength >= _latestRelationship.Strength ||
                     relationship.ConfirmedAt > _latestRelationship.ConfirmedAt))
                {
                    _latestRelationship = relationship;
                    _relationshipAge = 0;
                    isNewRelationship = true;
                }
            }

            decimal? momentumChange = null;
            MomentumDirection momentumDirection = MomentumDirection.Unknown;
            if (_samples.Count > _momentumLookback)
            {
                decimal previous = _samples[_samples.Count - 1 - _momentumLookback].Value;
                momentumChange = currentRsi - previous;
                momentumDirection = momentumChange.Value > _momentumThreshold
                    ? MomentumDirection.Rising
                    : momentumChange.Value < -_momentumThreshold
                        ? MomentumDirection.Falling
                        : MomentumDirection.Stable;
            }

            Current = new RsiAnalysisSnapshot
            {
                Zone = ClassifyZone(currentRsi),
                MomentumDirection = momentumDirection,
                MomentumChange = momentumChange,
                PreviousValue = previousRsi,
                LatestRelationship = _latestRelationship is null
                    ? null
                    : _latestRelationship with { AgeCandles = _relationshipAge },
                IsNewRelationship = isNewRelationship,
                SampleCount = _samples.Count
            };
            return Current;
        }

        Current = new RsiAnalysisSnapshot
        {
            LatestRelationship = _latestRelationship is null
                ? null
                : _latestRelationship with { AgeCandles = _relationshipAge },
            IsNewRelationship = false,
            SampleCount = _samples.Count
        };
        return Current;
    }

    private RsiRelationshipSnapshot? AddPivotAndDetect(RsiPivot current, decimal? atr)
    {
        RingBuffer<RsiPivot> pivots = current.Swing.Type == SwingType.High
            ? _highPivots
            : _lowPivots;
        RsiPivot? previous = pivots.Count == 0 ? null : pivots.Latest;
        pivots.Add(current);
        if (previous is null)
        {
            return null;
        }

        decimal priceChange = current.Swing.Price - previous.Value.Swing.Price;
        decimal rsiChange = current.Rsi - previous.Value.Rsi;
        decimal priceTolerance = atr is > 0m
            ? atr.Value * _minimumPriceDifferenceAtr
            : Math.Max(Math.Abs(previous.Value.Swing.Price) * 0.0001m, 0.00000001m);
        if (Math.Abs(priceChange) <= priceTolerance ||
            Math.Abs(rsiChange) < _minimumRsiDifference)
        {
            return null;
        }

        RsiRelationshipType type = ClassifyRelationship(
            current.Swing.Type,
            priceChange,
            rsiChange);
        if (type == RsiRelationshipType.None)
        {
            return null;
        }

        decimal normalizedPriceMove = atr is > 0m
            ? Math.Abs(priceChange) / atr.Value
            : Math.Abs(priceChange) / Math.Max(Math.Abs(previous.Value.Swing.Price), 0.00000001m) * 100m;
        decimal strength = Math.Clamp(
            normalizedPriceMove * 25m + Math.Abs(rsiChange) * 2m,
            0m,
            100m);

        return new RsiRelationshipSnapshot
        {
            Type = type,
            FirstPivotTime = previous.Value.Swing.PivotTime,
            SecondPivotTime = current.Swing.PivotTime,
            ConfirmedAt = current.Swing.ConfirmedAt,
            FirstPrice = previous.Value.Swing.Price,
            SecondPrice = current.Swing.Price,
            FirstRsi = previous.Value.Rsi,
            SecondRsi = current.Rsi,
            PriceChange = priceChange,
            RsiChange = rsiChange,
            Strength = strength,
            AgeCandles = 0
        };
    }

    private static RsiRelationshipType ClassifyRelationship(
        SwingType swingType,
        decimal priceChange,
        decimal rsiChange)
    {
        if (swingType == SwingType.Low)
        {
            if (priceChange < 0m && rsiChange > 0m)
            {
                return RsiRelationshipType.RegularBullishDivergence;
            }

            if (priceChange > 0m && rsiChange < 0m)
            {
                return RsiRelationshipType.HiddenBullishDivergence;
            }

            if (priceChange > 0m && rsiChange > 0m)
            {
                return RsiRelationshipType.BullishConvergence;
            }

            if (priceChange < 0m && rsiChange < 0m)
            {
                return RsiRelationshipType.BearishConvergence;
            }
        }
        else
        {
            if (priceChange > 0m && rsiChange < 0m)
            {
                return RsiRelationshipType.RegularBearishDivergence;
            }

            if (priceChange < 0m && rsiChange > 0m)
            {
                return RsiRelationshipType.HiddenBearishDivergence;
            }

            if (priceChange > 0m && rsiChange > 0m)
            {
                return RsiRelationshipType.BullishConvergence;
            }

            if (priceChange < 0m && rsiChange < 0m)
            {
                return RsiRelationshipType.BearishConvergence;
            }
        }

        return RsiRelationshipType.None;
    }

    private bool TryFindRsi(DateTimeOffset pivotTime, out decimal rsi)
    {
        for (int index = _samples.Count - 1; index >= 0; index--)
        {
            RsiSample sample = _samples[index];
            if (sample.CandleOpenTime == pivotTime)
            {
                rsi = sample.Value;
                return true;
            }

            if (sample.CandleOpenTime < pivotTime)
            {
                break;
            }
        }

        rsi = default;
        return false;
    }

    private static RsiZone ClassifyZone(decimal rsi) => rsi switch
    {
        >= 70m => RsiZone.Overbought,
        <= 30m => RsiZone.Oversold,
        >= 55m => RsiZone.Bullish,
        <= 45m => RsiZone.Bearish,
        _ => RsiZone.Neutral
    };

    private readonly record struct RsiSample(DateTimeOffset CandleOpenTime, decimal Value);
    private readonly record struct RsiPivot(SwingPoint Swing, decimal Rsi);
}
