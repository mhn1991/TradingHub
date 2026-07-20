using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Causal CCI momentum, threshold and confirmed-price-pivot relationship state.
/// Confirmed swings are supplied by <see cref="Structure.SwingDetector"/> only after
/// their right-side candles have closed.
/// </summary>
public sealed class CciAnalysisState
{
    private readonly RingBuffer<CciSample> _samples;
    private readonly RingBuffer<CciPivot> _highPivots;
    private readonly RingBuffer<CciPivot> _lowPivots;
    private readonly int _momentumLookback;
    private readonly decimal _momentumThreshold;
    private readonly decimal _extremeNegative;
    private readonly decimal _extremePositive;
    private readonly decimal _minimumCciDifference;
    private readonly decimal _minimumPriceDifferenceAtr;
    private readonly int _signalLifetimeCandles;
    private CciRelationshipSnapshot? _latestRelationship;
    private int _relationshipAge;
    private int _barsSinceExtremeNegative = -1;
    private int _barsSinceExtremePositive = -1;

    public CciAnalysisState(
        int sampleCapacity = 2_000,
        int pivotCapacity = 500,
        int momentumLookback = 3,
        decimal momentumThreshold = 5m,
        decimal extremeNegativeThreshold = -100m,
        decimal extremePositiveThreshold = 100m,
        decimal minimumCciDifference = 10m,
        decimal minimumPriceDifferenceAtr = 0.05m,
        int signalLifetimeCandles = 50)
    {
        if (sampleCapacity < 2 || pivotCapacity < 2)
            throw new ArgumentOutOfRangeException(nameof(sampleCapacity));
        if (momentumLookback < 1 || momentumLookback >= sampleCapacity)
            throw new ArgumentOutOfRangeException(nameof(momentumLookback));
        if (momentumThreshold < 0m || minimumCciDifference < 0m || minimumPriceDifferenceAtr < 0m)
            throw new ArgumentOutOfRangeException(nameof(momentumThreshold));
        if (extremeNegativeThreshold >= 0m || extremePositiveThreshold <= 0m ||
            extremeNegativeThreshold >= extremePositiveThreshold)
            throw new ArgumentOutOfRangeException(nameof(extremeNegativeThreshold));
        if (signalLifetimeCandles < 1)
            throw new ArgumentOutOfRangeException(nameof(signalLifetimeCandles));

        _samples = new RingBuffer<CciSample>(sampleCapacity);
        _highPivots = new RingBuffer<CciPivot>(pivotCapacity);
        _lowPivots = new RingBuffer<CciPivot>(pivotCapacity);
        _momentumLookback = momentumLookback;
        _momentumThreshold = momentumThreshold;
        _extremeNegative = extremeNegativeThreshold;
        _extremePositive = extremePositiveThreshold;
        _minimumCciDifference = minimumCciDifference;
        _minimumPriceDifferenceAtr = minimumPriceDifferenceAtr;
        _signalLifetimeCandles = signalLifetimeCandles;
    }

    public CciAnalysisSnapshot Current { get; private set; } = CciAnalysisSnapshot.Empty;

    public CciAnalysisSnapshot Update(
        Candle candle,
        decimal? cci,
        IReadOnlyList<SwingPoint> confirmedSwings,
        decimal? atr)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentNullException.ThrowIfNull(confirmedSwings);

        AgeRelationship();
        if (cci is not decimal current)
        {
            Current = new CciAnalysisSnapshot
            {
                BarsSinceExtremeNegative = _barsSinceExtremeNegative,
                BarsSinceExtremePositive = _barsSinceExtremePositive,
                LatestRelationship = WithCurrentAge(_latestRelationship),
                SampleCount = _samples.Count
            };
            return Current;
        }

        decimal? previous = _samples.Count == 0 ? null : _samples.Latest.Value;
        _samples.Add(new CciSample(candle.OpenTime, current));
        UpdateExtremeAges(current);

        bool isNewRelationship = false;
        foreach (SwingPoint swing in confirmedSwings.OrderBy(item => item.ConfirmedAt).ThenBy(item => item.PivotTime))
        {
            if (!TryFindCci(swing.PivotTime, out decimal pivotCci))
                continue;

            CciRelationshipSnapshot? relationship = AddPivotAndDetect(new CciPivot(swing, pivotCci), atr);
            if (relationship is not null && (_latestRelationship is null ||
                relationship.ConfirmedAt > _latestRelationship.ConfirmedAt ||
                relationship.Strength >= _latestRelationship.Strength))
            {
                _latestRelationship = relationship;
                _relationshipAge = 0;
                isNewRelationship = true;
            }
        }

        decimal? momentumChange = null;
        MomentumDirection momentum = MomentumDirection.Unknown;
        if (_samples.Count > _momentumLookback)
        {
            momentumChange = current - _samples[_samples.Count - 1 - _momentumLookback].Value;
            momentum = momentumChange > _momentumThreshold
                ? MomentumDirection.Rising
                : momentumChange < -_momentumThreshold
                    ? MomentumDirection.Falling
                    : MomentumDirection.Stable;
        }

        Current = new CciAnalysisSnapshot
        {
            Zone = ClassifyZone(current),
            MomentumDirection = momentum,
            MomentumChange = momentumChange,
            PreviousValue = previous,
            CrossedUpFromExtremeNegative = previous <= _extremeNegative && current > _extremeNegative,
            CrossedDownFromExtremePositive = previous >= _extremePositive && current < _extremePositive,
            CrossedUpZero = previous <= 0m && current > 0m,
            CrossedDownZero = previous >= 0m && current < 0m,
            BarsSinceExtremeNegative = _barsSinceExtremeNegative,
            BarsSinceExtremePositive = _barsSinceExtremePositive,
            LatestRelationship = WithCurrentAge(_latestRelationship),
            IsNewRelationship = isNewRelationship,
            SampleCount = _samples.Count
        };
        return Current;
    }

    private void AgeRelationship()
    {
        if (_latestRelationship is null)
            return;
        _relationshipAge++;
        if (_relationshipAge > _signalLifetimeCandles)
        {
            _latestRelationship = null;
            _relationshipAge = 0;
        }
    }

    private void UpdateExtremeAges(decimal current)
    {
        _barsSinceExtremeNegative = current <= _extremeNegative
            ? 0
            : _barsSinceExtremeNegative < 0 ? -1 : _barsSinceExtremeNegative + 1;
        _barsSinceExtremePositive = current >= _extremePositive
            ? 0
            : _barsSinceExtremePositive < 0 ? -1 : _barsSinceExtremePositive + 1;
    }

    private CciRelationshipSnapshot? AddPivotAndDetect(CciPivot current, decimal? atr)
    {
        RingBuffer<CciPivot> pivots = current.Swing.Type == SwingType.High ? _highPivots : _lowPivots;
        CciPivot? previous = pivots.Count == 0 ? null : pivots.Latest;
        pivots.Add(current);
        if (previous is null)
            return null;

        decimal priceChange = current.Swing.Price - previous.Value.Swing.Price;
        decimal cciChange = current.Cci - previous.Value.Cci;
        decimal priceTolerance = atr is > 0m
            ? atr.Value * _minimumPriceDifferenceAtr
            : Math.Max(Math.Abs(previous.Value.Swing.Price) * 0.0001m, 0.00000001m);
        if (Math.Abs(priceChange) <= priceTolerance || Math.Abs(cciChange) < _minimumCciDifference)
            return null;

        CciRelationshipType type = ClassifyRelationship(current.Swing.Type, priceChange, cciChange);
        if (type == CciRelationshipType.None)
            return null;

        decimal normalizedPriceMove = atr is > 0m
            ? Math.Abs(priceChange) / atr.Value
            : Math.Abs(priceChange) / Math.Max(Math.Abs(previous.Value.Swing.Price), 0.00000001m) * 100m;
        return new CciRelationshipSnapshot
        {
            Type = type,
            FirstPivotTime = previous.Value.Swing.PivotTime,
            SecondPivotTime = current.Swing.PivotTime,
            ConfirmedAt = current.Swing.ConfirmedAt,
            FirstPrice = previous.Value.Swing.Price,
            SecondPrice = current.Swing.Price,
            FirstCci = previous.Value.Cci,
            SecondCci = current.Cci,
            PriceChange = priceChange,
            CciChange = cciChange,
            Strength = Math.Clamp(normalizedPriceMove * 25m + Math.Abs(cciChange) * 0.4m, 0m, 100m),
            AgeCandles = 0
        };
    }

    private static CciRelationshipType ClassifyRelationship(SwingType type, decimal priceChange, decimal cciChange) =>
        (type, priceChange, cciChange) switch
        {
            (SwingType.Low, < 0m, > 0m) => CciRelationshipType.RegularBullishDivergence,
            (SwingType.Low, > 0m, < 0m) => CciRelationshipType.HiddenBullishDivergence,
            (SwingType.Low, > 0m, > 0m) => CciRelationshipType.BullishConvergence,
            (SwingType.Low, < 0m, < 0m) => CciRelationshipType.BearishConvergence,
            (SwingType.High, > 0m, < 0m) => CciRelationshipType.RegularBearishDivergence,
            (SwingType.High, < 0m, > 0m) => CciRelationshipType.HiddenBearishDivergence,
            (SwingType.High, > 0m, > 0m) => CciRelationshipType.BullishConvergence,
            (SwingType.High, < 0m, < 0m) => CciRelationshipType.BearishConvergence,
            _ => CciRelationshipType.None
        };

    private bool TryFindCci(DateTimeOffset pivotTime, out decimal cci)
    {
        for (int index = _samples.Count - 1; index >= 0; index--)
        {
            CciSample sample = _samples[index];
            if (sample.CandleOpenTime == pivotTime)
            {
                cci = sample.Value;
                return true;
            }
            if (sample.CandleOpenTime < pivotTime)
                break;
        }
        cci = default;
        return false;
    }

    private CciZone ClassifyZone(decimal cci)
    {
        if (cci <= _extremeNegative)
            return CciZone.ExtremeNegative;
        if (cci >= _extremePositive)
            return CciZone.ExtremePositive;
        if (cci < 0m)
            return CciZone.Negative;
        if (cci > 0m)
            return CciZone.Positive;
        return CciZone.Neutral;
    }

    private CciRelationshipSnapshot? WithCurrentAge(CciRelationshipSnapshot? relationship) =>
        relationship is null ? null : relationship with { AgeCandles = _relationshipAge };

    private readonly record struct CciSample(DateTimeOffset CandleOpenTime, decimal Value);
    private readonly record struct CciPivot(SwingPoint Swing, decimal Cci);
}
