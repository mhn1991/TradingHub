using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Tracks StochRSI's fast line (%K) at confirmed price swing pivots, classifying regular/hidden
/// divergence and convergence the same way <see cref="RsiAnalysisState"/> does for raw RSI -
/// deliberately the same algorithm and ring-buffer shape, just against a different oscillator
/// series, so the two stay easy to compare and audit side by side. A relationship is emitted only
/// after the right-hand swing candles have closed, so it does not introduce look-ahead bias.
/// </summary>
public sealed class StochRsiAnalysisState
{
    private readonly RingBuffer<StochRsiSample> _samples;
    private readonly RingBuffer<StochRsiPivot> _highPivots;
    private readonly RingBuffer<StochRsiPivot> _lowPivots;
    private readonly decimal _minimumStochRsiDifference;
    private readonly decimal _minimumPriceDifferenceAtr;
    private readonly int _signalLifetimeCandles;
    private StochRsiRelationshipSnapshot? _latestRelationship;
    private int _relationshipAge;

    public StochRsiAnalysisState(
        int sampleCapacity = 2_000,
        int pivotCapacity = 500,
        decimal minimumStochRsiDifference = 5m,
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

        if (minimumStochRsiDifference < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumStochRsiDifference));
        }

        if (minimumPriceDifferenceAtr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumPriceDifferenceAtr));
        }

        if (signalLifetimeCandles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(signalLifetimeCandles));
        }

        _samples = new RingBuffer<StochRsiSample>(sampleCapacity);
        _highPivots = new RingBuffer<StochRsiPivot>(pivotCapacity);
        _lowPivots = new RingBuffer<StochRsiPivot>(pivotCapacity);
        _minimumStochRsiDifference = minimumStochRsiDifference;
        _minimumPriceDifferenceAtr = minimumPriceDifferenceAtr;
        _signalLifetimeCandles = signalLifetimeCandles;
    }

    public StochRsiAnalysisSnapshot Current { get; private set; } = StochRsiAnalysisSnapshot.Empty;

    /// <summary>
    /// <paramref name="stochRsiFast"/> is StochRSI's %K (fast) line - deliberately not %D (slow);
    /// the fast line reacts to a momentum shift several bars before the slow line would.
    /// </summary>
    public StochRsiAnalysisSnapshot Update(
        Candle candle,
        decimal? stochRsiFast,
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

        if (stochRsiFast is decimal currentFast)
        {
            _samples.Add(new StochRsiSample(candle.OpenTime, currentFast));
            foreach (SwingPoint swing in confirmedSwings)
            {
                if (!TryFindStochRsiFast(swing.PivotTime, out decimal pivotFast))
                {
                    continue;
                }

                StochRsiRelationshipSnapshot? relationship = AddPivotAndDetect(
                    new StochRsiPivot(swing, pivotFast),
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

            Current = new StochRsiAnalysisSnapshot
            {
                LatestRelationship = _latestRelationship is null
                    ? null
                    : _latestRelationship with { AgeCandles = _relationshipAge },
                IsNewRelationship = isNewRelationship,
                SampleCount = _samples.Count
            };
            return Current;
        }

        Current = new StochRsiAnalysisSnapshot
        {
            LatestRelationship = _latestRelationship is null
                ? null
                : _latestRelationship with { AgeCandles = _relationshipAge },
            IsNewRelationship = false,
            SampleCount = _samples.Count
        };
        return Current;
    }

    private StochRsiRelationshipSnapshot? AddPivotAndDetect(StochRsiPivot current, decimal? atr)
    {
        RingBuffer<StochRsiPivot> pivots = current.Swing.Type == SwingType.High
            ? _highPivots
            : _lowPivots;
        StochRsiPivot? previous = pivots.Count == 0 ? null : pivots.Latest;
        pivots.Add(current);
        if (previous is null)
        {
            return null;
        }

        decimal priceChange = current.Swing.Price - previous.Value.Swing.Price;
        decimal fastChange = current.Fast - previous.Value.Fast;
        decimal priceTolerance = atr is > 0m
            ? atr.Value * _minimumPriceDifferenceAtr
            : Math.Max(Math.Abs(previous.Value.Swing.Price) * 0.0001m, 0.00000001m);
        if (Math.Abs(priceChange) <= priceTolerance ||
            Math.Abs(fastChange) < _minimumStochRsiDifference)
        {
            return null;
        }

        StochRsiRelationshipType type = ClassifyRelationship(
            current.Swing.Type,
            priceChange,
            fastChange);
        if (type == StochRsiRelationshipType.None)
        {
            return null;
        }

        decimal normalizedPriceMove = atr is > 0m
            ? Math.Abs(priceChange) / atr.Value
            : Math.Abs(priceChange) / Math.Max(Math.Abs(previous.Value.Swing.Price), 0.00000001m) * 100m;
        decimal strength = Math.Clamp(
            normalizedPriceMove * 25m + Math.Abs(fastChange) * 0.8m,
            0m,
            100m);

        return new StochRsiRelationshipSnapshot
        {
            Type = type,
            FirstPivotTime = previous.Value.Swing.PivotTime,
            SecondPivotTime = current.Swing.PivotTime,
            ConfirmedAt = current.Swing.ConfirmedAt,
            FirstPrice = previous.Value.Swing.Price,
            SecondPrice = current.Swing.Price,
            FirstStochRsiFast = previous.Value.Fast,
            SecondStochRsiFast = current.Fast,
            PriceChange = priceChange,
            StochRsiFastChange = fastChange,
            Strength = strength,
            AgeCandles = 0
        };
    }

    private static StochRsiRelationshipType ClassifyRelationship(
        SwingType swingType,
        decimal priceChange,
        decimal fastChange)
    {
        if (swingType == SwingType.Low)
        {
            if (priceChange < 0m && fastChange > 0m)
            {
                return StochRsiRelationshipType.RegularBullishDivergence;
            }

            if (priceChange > 0m && fastChange < 0m)
            {
                return StochRsiRelationshipType.HiddenBullishDivergence;
            }

            if (priceChange > 0m && fastChange > 0m)
            {
                return StochRsiRelationshipType.BullishConvergence;
            }

            if (priceChange < 0m && fastChange < 0m)
            {
                return StochRsiRelationshipType.BearishConvergence;
            }
        }
        else
        {
            if (priceChange > 0m && fastChange < 0m)
            {
                return StochRsiRelationshipType.RegularBearishDivergence;
            }

            if (priceChange < 0m && fastChange > 0m)
            {
                return StochRsiRelationshipType.HiddenBearishDivergence;
            }

            if (priceChange > 0m && fastChange > 0m)
            {
                return StochRsiRelationshipType.BullishConvergence;
            }

            if (priceChange < 0m && fastChange < 0m)
            {
                return StochRsiRelationshipType.BearishConvergence;
            }
        }

        return StochRsiRelationshipType.None;
    }

    private bool TryFindStochRsiFast(DateTimeOffset pivotTime, out decimal fast)
    {
        for (int index = _samples.Count - 1; index >= 0; index--)
        {
            StochRsiSample sample = _samples[index];
            if (sample.CandleOpenTime == pivotTime)
            {
                fast = sample.Value;
                return true;
            }

            if (sample.CandleOpenTime < pivotTime)
            {
                break;
            }
        }

        fast = default;
        return false;
    }

    private readonly record struct StochRsiSample(DateTimeOffset CandleOpenTime, decimal Value);
    private readonly record struct StochRsiPivot(SwingPoint Swing, decimal Fast);
}
