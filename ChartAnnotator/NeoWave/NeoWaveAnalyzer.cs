using System.Security.Cryptography;
using System.Text;
using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.NeoWave;

/// <summary>
/// Builds causal monowaves exclusively from confirmed swing points, then produces a bounded set
/// of deterministic structural hypotheses. Published snapshots are immutable; later candles may
/// invalidate or replace current hypotheses but never mutate an earlier snapshot.
/// </summary>
public sealed class NeoWaveAnalyzer
{
    private readonly NeoWaveOptions _options;

    public NeoWaveAnalyzer(NeoWaveOptions? options = null)
    {
        _options = options ?? new NeoWaveOptions();
        _options.Validate();
    }

    public NeoWaveSnapshot Update(
        IReadOnlyList<SwingPoint> confirmedSwings,
        Candle currentCandle,
        decimal? currentAtr,
        IReadOnlyList<IndicatorPoint>? indicatorHistory = null)
    {
        ArgumentNullException.ThrowIfNull(confirmedSwings);
        ArgumentNullException.ThrowIfNull(currentCandle);

        DateTimeOffset availableAt = currentCandle.CloseTime ?? currentCandle.OpenTime;
        if (!_options.Enabled)
            return NeoWaveSnapshot.Disabled with { AvailableAt = availableAt };

        IReadOnlyList<SwingPoint> normalized = NormalizeSwings(confirmedSwings, availableAt);
        IReadOnlyList<MonoWave> waves = BuildMonoWaves(
            normalized,
            availableAt,
            currentAtr,
            indicatorHistory);
        IReadOnlyList<MonoWaveRelationship> relationships = BuildRelationships(waves);
        ProvisionalMonoWave? provisional = _options.IncludeProvisionalWave
            ? BuildProvisional(normalized.LastOrDefault(), currentCandle, currentAtr)
            : null;

        List<NeoWaveHypothesis> allHypotheses = BuildHypotheses(waves)
            .Select(item => IsInvalidated(item, currentCandle.Prices.Close)
                ? item with { Status = NeoWaveHypothesisStatus.Invalidated }
                : item)
            .ToList();
        int beforePrune = allHypotheses.Count;
        List<NeoWaveHypothesis> hypotheses = allHypotheses
            .Where(item => item.StructuralScore >= _options.MinimumHypothesisScore)
            .OrderBy(item => item.Status == NeoWaveHypothesisStatus.Invalidated)
            .ThenByDescending(item => item.StructuralScore)
            .ThenByDescending(item => item.ComponentWaveIds.Count)
            .ThenBy(item => item.PatternType)
            .ThenBy(item => item.HypothesisId, StringComparer.Ordinal)
            .Take(_options.MaximumHypotheses)
            .ToList();

        NeoWaveHypothesis? preferred = hypotheses.FirstOrDefault(item =>
            item.Status != NeoWaveHypothesisStatus.Invalidated &&
            item.StructuralScore >= _options.PreferredHypothesisMinimumScore);
        if (preferred is not null)
        {
            for (int index = 0; index < hypotheses.Count; index++)
            {
                hypotheses[index] = hypotheses[index].HypothesisId == preferred.HypothesisId
                    ? hypotheses[index] with { Status = NeoWaveHypothesisStatus.Preferred }
                    : hypotheses[index];
            }
            preferred = hypotheses.First(item => item.HypothesisId == preferred.HypothesisId);
        }

        decimal conflict = CalculateConflict(preferred, hypotheses);
        decimal? invalidationPrice = preferred?.Invalidation.Price;
        decimal? invalidationDistanceAtr = invalidationPrice is decimal invalidation &&
            currentAtr is decimal atr && atr > 0m
                ? Math.Abs(currentCandle.Prices.Close - invalidation) / atr
                : null;

        bool ready = waves.Count >= _options.MinimumConfirmedMonoWaves;
        List<string> reasons = [];
        reasons.Add(ready ? "NeoWaveReady" : "NeoWaveInsufficientConfirmedWaves");
        if (preferred is null)
            reasons.Add("NeoWaveNoPreferredHypothesis");
        else
            reasons.Add($"NeoWavePreferred:{preferred.PatternType}");
        if (conflict >= 50m)
            reasons.Add("NeoWaveStructuralConflict");
        if (hypotheses.Any(item => item.Status == NeoWaveHypothesisStatus.Invalidated))
            reasons.Add("NeoWaveHypothesisInvalidated");
        if (provisional is not null)
            reasons.Add("NeoWaveProvisionalLegPresent");

        return new NeoWaveSnapshot
        {
            Enabled = true,
            AvailableAt = availableAt,
            ConfirmedMonoWaves = waves,
            ProvisionalWave = provisional,
            Relationships = relationships,
            Hypotheses = hypotheses.ToArray(),
            PreferredHypothesisId = preferred?.HypothesisId,
            StructuralBias = preferred?.Direction ?? NeoWaveDirection.Neutral,
            StructuralScore = preferred?.StructuralScore ?? 0m,
            Maturity = preferred?.Maturity ?? 0m,
            ConflictScore = conflict,
            InvalidationPrice = invalidationPrice,
            InvalidationDistanceAtr = invalidationDistanceAtr,
            ReasonCodes = reasons.ToArray(),
            Quality = new NeoWaveQuality
            {
                IsReady = ready,
                ConfirmedSwingCount = normalized.Count,
                ConfirmedMonoWaveCount = waves.Count,
                HypothesisCount = hypotheses.Count,
                PrunedHypothesisCount = Math.Max(0, beforePrune - hypotheses.Count),
                ReasonCode = ready ? "Ready" : "InsufficientConfirmedWaves"
            }
        };
    }

    private IReadOnlyList<SwingPoint> NormalizeSwings(
        IReadOnlyList<SwingPoint> swings,
        DateTimeOffset availableAt)
    {
        var ordered = swings
            .Where(item => item.ConfirmedAt <= availableAt && item.PivotTime <= item.ConfirmedAt)
            .OrderBy(item => item.PivotTime)
            .ThenBy(item => item.ConfirmedAt)
            .ToList();
        var result = new List<SwingPoint>(ordered.Count);
        foreach (SwingPoint swing in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(swing);
                continue;
            }

            SwingPoint previous = result[^1];
            if (swing.PivotTime == previous.PivotTime && swing.Type == previous.Type)
            {
                if (IsMoreExtreme(swing, previous))
                    result[^1] = swing;
                continue;
            }

            if (swing.Type == previous.Type)
            {
                if (IsMoreExtreme(swing, previous))
                    result[^1] = swing;
                continue;
            }

            result.Add(swing);
        }

        int maximumSwings = _options.MaximumConfirmedMonoWaves + 1;
        return result.Count <= maximumSwings
            ? result.ToArray()
            : result.Skip(result.Count - maximumSwings).ToArray();
    }

    private IReadOnlyList<MonoWave> BuildMonoWaves(
        IReadOnlyList<SwingPoint> swings,
        DateTimeOffset availableAt,
        decimal? currentAtr,
        IReadOnlyList<IndicatorPoint>? indicatorHistory)
    {
        var result = new List<MonoWave>(Math.Max(0, swings.Count - 1));
        for (int index = 1; index < swings.Count; index++)
        {
            SwingPoint start = swings[index - 1];
            SwingPoint end = swings[index];
            decimal length = Math.Abs(end.Price - start.Price);
            decimal? waveAtr = ResolveAtrAt(
                end.ConfirmedAt,
                availableAt,
                currentAtr,
                indicatorHistory);
            decimal? lengthAtr = waveAtr is decimal atr && atr > 0m ? length / atr : null;
            if (length <= 0m)
                continue;

            result.Add(new MonoWave
            {
                WaveId = StableId("wave", start.PivotTime, end.PivotTime, start.Price, end.Price),
                StartTime = start.PivotTime,
                EndTime = end.PivotTime,
                StartConfirmedAt = start.ConfirmedAt,
                EndConfirmedAt = end.ConfirmedAt,
                AvailableAt = Later(start.ConfirmedAt, end.ConfirmedAt),
                StartPrice = start.Price,
                EndPrice = end.Price,
                Direction = end.Price > start.Price ? NeoWaveDirection.Up : NeoWaveDirection.Down,
                PriceLength = length,
                TimeLength = end.PivotTime - start.PivotTime,
                LengthAtr = lengthAtr,
                IsConfirmed = true
            });
        }
        return result.ToArray();
    }

    private static decimal? ResolveAtrAt(
        DateTimeOffset confirmedAt,
        DateTimeOffset availableAt,
        decimal? currentAtr,
        IReadOnlyList<IndicatorPoint>? indicatorHistory)
    {
        if (confirmedAt >= availableAt && currentAtr is decimal current && current > 0m)
            return current;
        if (indicatorHistory is null)
            return null;

        for (int index = indicatorHistory.Count - 1; index >= 0; index--)
        {
            IndicatorPoint point = indicatorHistory[index];
            if (point.Timestamp <= confirmedAt && point.Atr is decimal atr && atr > 0m)
                return atr;
        }
        return null;
    }

    private static IReadOnlyList<MonoWaveRelationship> BuildRelationships(
        IReadOnlyList<MonoWave> waves)
    {
        var result = new List<MonoWaveRelationship>(Math.Max(0, waves.Count - 1));
        for (int index = 1; index < waves.Count; index++)
        {
            MonoWave left = waves[index - 1];
            MonoWave right = waves[index];
            bool full = left.Direction == NeoWaveDirection.Up
                ? right.EndPrice <= left.StartPrice
                : right.EndPrice >= left.StartPrice;
            result.Add(new MonoWaveRelationship
            {
                LeftWaveId = left.WaveId,
                RightWaveId = right.WaveId,
                AvailableAt = right.AvailableAt,
                PriceRatio = Ratio(right.PriceLength, left.PriceLength),
                TimeRatio = Ratio((decimal)right.TimeLength.TotalSeconds, (decimal)left.TimeLength.TotalSeconds),
                FullyRetracesPrevious = full,
                ReturnsInsidePreviousOrigin = full
            });
        }
        return result.ToArray();
    }

    private ProvisionalMonoWave? BuildProvisional(
        SwingPoint? lastSwing,
        Candle candle,
        decimal? currentAtr)
    {
        if (lastSwing is null || lastSwing.ConfirmedAt > (candle.CloseTime ?? candle.OpenTime))
            return null;
        decimal length = Math.Abs(candle.Prices.Close - lastSwing.Price);
        if (length <= 0m)
            return null;
        decimal? lengthAtr = currentAtr is decimal atr && atr > 0m ? length / atr : null;
        return new ProvisionalMonoWave
        {
            WaveId = StableId("provisional", lastSwing.PivotTime, candle.CloseTime ?? candle.OpenTime, lastSwing.Price, candle.Prices.Close),
            StartTime = lastSwing.PivotTime,
            CurrentTime = candle.CloseTime ?? candle.OpenTime,
            AvailableAt = candle.CloseTime ?? candle.OpenTime,
            StartPrice = lastSwing.Price,
            CurrentPrice = candle.Prices.Close,
            Direction = candle.Prices.Close > lastSwing.Price ? NeoWaveDirection.Up : NeoWaveDirection.Down,
            PriceLength = length,
            LengthAtr = lengthAtr
        };
    }

    private List<NeoWaveHypothesis> BuildHypotheses(IReadOnlyList<MonoWave> waves)
    {
        var result = new List<NeoWaveHypothesis>();
        if (waves.Count >= 3)
        {
            MonoWave[] lastThree = waves.Skip(waves.Count - 3).ToArray();
            if (MeetsMinimumLength(lastThree))
            {
                result.Add(CreateZigZag(lastThree));
                result.Add(CreateFlat(lastThree));
                result.Add(CreateTrendSequence(lastThree));
            }
        }
        if (waves.Count >= 5)
        {
            MonoWave[] lastFive = waves.Skip(waves.Count - 5).ToArray();
            if (MeetsMinimumLength(lastFive))
            {
                result.Add(CreateImpulse(lastFive));
                result.Add(CreateTriangle(lastFive));
                result.Add(CreateComplexCorrection(lastFive));
            }
        }
        return result;
    }

    private NeoWaveHypothesis CreateTrendSequence(IReadOnlyList<MonoWave> waves)
    {
        var score = new RuleScore();
        bool alternating = waves[0].Direction == waves[2].Direction && waves[1].Direction != waves[0].Direction;
        score.Add("NW-SEQ-ALT", alternating, 35m);
        score.Add("NW-SEQ-NET", NetMovementAligned(waves, waves[0].Direction), 25m);
        score.Add("NW-SEQ-W3", waves[2].PriceLength >= waves[0].PriceLength * 0.50m, 20m);
        score.Add("NW-SEQ-TIME", SimilarTime(waves[0], waves[2]), 20m);
        return CreateHypothesis(NeoWavePatternType.TrendSequence, waves[0].Direction, waves, score, 60m);
    }

    private NeoWaveHypothesis CreateImpulse(IReadOnlyList<MonoWave> waves)
    {
        NeoWaveDirection direction = waves[0].Direction;
        var score = new RuleScore();
        score.Add("NW-IMP-ALT", waves[2].Direction == direction && waves[4].Direction == direction &&
            waves[1].Direction != direction && waves[3].Direction != direction, 15m);
        score.Add("NW-IMP-W2", !FullyRetraces(waves[0], waves[1]), 15m);
        score.Add("NW-IMP-W4", !FullyRetraces(waves[2], waves[3]), 15m);
        score.Add("NW-IMP-W3-NOT-SHORTEST", waves[2].PriceLength >= Math.Min(waves[0].PriceLength, waves[4].PriceLength), 20m);
        score.Add("NW-IMP-W4-NO-OVERLAP", NoWaveOneOverlap(waves), 15m);
        score.Add("NW-IMP-W2-RATIO", InRange(Ratio(waves[1].PriceLength, waves[0].PriceLength), 0.236m, 0.886m), 10m);
        score.Add("NW-IMP-W4-RATIO", InRange(Ratio(waves[3].PriceLength, waves[2].PriceLength), 0.146m, 0.786m), 10m);
        return CreateHypothesis(NeoWavePatternType.ImpulseCandidate, direction, waves, score, 100m);
    }

    private NeoWaveHypothesis CreateZigZag(IReadOnlyList<MonoWave> waves)
    {
        NeoWaveDirection direction = waves[0].Direction;
        decimal? bToA = Ratio(waves[1].PriceLength, waves[0].PriceLength);
        decimal? cToA = Ratio(waves[2].PriceLength, waves[0].PriceLength);
        var score = new RuleScore();
        score.Add("NW-ZZ-ALT", waves[2].Direction == direction && waves[1].Direction != direction, 25m);
        score.Add("NW-ZZ-B", InRangeWithTolerance(bToA, 0.236m, 0.886m), 25m);
        score.Add("NW-ZZ-C", InRangeWithTolerance(cToA, 0.618m, 1.618m), 30m);
        score.Add("NW-ZZ-NET", NetMovementAligned(waves, direction), 20m);
        return CreateHypothesis(NeoWavePatternType.ZigZagCorrection, direction, waves, score, 100m);
    }

    private NeoWaveHypothesis CreateFlat(IReadOnlyList<MonoWave> waves)
    {
        NeoWaveDirection direction = waves[0].Direction;
        decimal? bToA = Ratio(waves[1].PriceLength, waves[0].PriceLength);
        decimal? cToA = Ratio(waves[2].PriceLength, waves[0].PriceLength);
        var score = new RuleScore();
        score.Add("NW-FLAT-ALT", waves[2].Direction == direction && waves[1].Direction != direction, 20m);
        score.Add("NW-FLAT-B", InRangeWithTolerance(bToA, 0.80m, 1.382m), 35m);
        score.Add("NW-FLAT-C", InRangeWithTolerance(cToA, 0.80m, 1.618m), 30m);
        score.Add("NW-FLAT-TIME", SimilarTime(waves[0], waves[2]), 15m);
        return CreateHypothesis(NeoWavePatternType.FlatCorrection, direction, waves, score, 100m);
    }

    private NeoWaveHypothesis CreateTriangle(IReadOnlyList<MonoWave> waves)
    {
        int contracting = 0;
        for (int index = 1; index < waves.Count; index++)
        {
            if (waves[index].PriceLength <= waves[index - 1].PriceLength * (1m + _options.TriangleContractionTolerance))
                contracting++;
        }
        var score = new RuleScore();
        score.Add("NW-TRI-ALT", Alternating(waves), 20m);
        score.Add("NW-TRI-CONTRACT-3", contracting >= 3, 40m);
        score.Add("NW-TRI-CONTRACT-4", contracting == 4, 20m);
        score.Add("NW-TRI-NET-SMALL", Math.Abs(waves[^1].EndPrice - waves[0].StartPrice) <=
            waves.Sum(item => item.PriceLength) * 0.35m, 20m);
        return CreateHypothesis(NeoWavePatternType.TriangleCorrection, NeoWaveDirection.Neutral, waves, score, 100m);
    }

    private NeoWaveHypothesis CreateComplexCorrection(IReadOnlyList<MonoWave> waves)
    {
        decimal largest = waves.Max(item => item.PriceLength);
        decimal smallest = waves.Min(item => item.PriceLength);
        var score = new RuleScore();
        score.Add("NW-CX-ALT", Alternating(waves), 25m);
        score.Add("NW-CX-OVERLAP", HasMaterialOverlap(waves), 35m);
        score.Add("NW-CX-BALANCED", largest <= smallest * 4m, 20m);
        score.Add("NW-CX-NON-IMPULSE", !NoWaveOneOverlap(waves), 20m);
        return CreateHypothesis(NeoWavePatternType.ComplexCorrection, NeoWaveDirection.Neutral, waves, score, 100m);
    }

    private NeoWaveHypothesis CreateHypothesis(
        NeoWavePatternType pattern,
        NeoWaveDirection direction,
        IReadOnlyList<MonoWave> waves,
        RuleScore score,
        decimal maturity)
    {
        MonoWave first = waves[0];
        MonoWave last = waves[^1];
        NeoWaveInvalidationCondition invalidation = direction switch
        {
            NeoWaveDirection.Up => new NeoWaveInvalidationCondition
            {
                Comparison = NeoWaveInvalidationComparison.Below,
                Price = first.StartPrice,
                Description = $"The {pattern} hypothesis is invalid below {first.StartPrice}."
            },
            NeoWaveDirection.Down => new NeoWaveInvalidationCondition
            {
                Comparison = NeoWaveInvalidationComparison.Above,
                Price = first.StartPrice,
                Description = $"The {pattern} hypothesis is invalid above {first.StartPrice}."
            },
            _ => new NeoWaveInvalidationCondition
            {
                Comparison = NeoWaveInvalidationComparison.None,
                Price = null,
                Description = $"The neutral {pattern} hypothesis is invalidated by a decisive break of its component range."
            }
        };
        string components = string.Join(',', waves.Select(item => item.WaveId));
        decimal normalizedScore = Math.Clamp(score.Score, 0m, 100m);
        return new NeoWaveHypothesis
        {
            HypothesisId = StableId("hypothesis", pattern, direction, components),
            PatternType = pattern,
            Direction = direction,
            Degree = _options.Degree,
            ComponentWaveIds = waves.Select(item => item.WaveId).ToArray(),
            Status = normalizedScore >= _options.PreferredHypothesisMinimumScore
                ? NeoWaveHypothesisStatus.Confirmed
                : NeoWaveHypothesisStatus.Possible,
            StructuralScore = normalizedScore,
            Maturity = maturity,
            AvailableAt = last.AvailableAt,
            SupportingRuleIds = score.Supporting.ToArray(),
            ViolatedRuleIds = score.Violated.ToArray(),
            Invalidation = invalidation
        };
    }

    private static decimal CalculateConflict(
        NeoWaveHypothesis? preferred,
        IReadOnlyList<NeoWaveHypothesis> hypotheses)
    {
        NeoWaveHypothesis[] active = hypotheses
            .Where(item => item.Status != NeoWaveHypothesisStatus.Invalidated)
            .ToArray();
        if (preferred is null || preferred.StructuralScore <= 0m)
            return active.Length > 1 ? 50m : 0m;

        decimal competingScore = active
            .Where(item => item.HypothesisId != preferred.HypothesisId)
            .Select(item => item.StructuralScore)
            .DefaultIfEmpty(0m)
            .Max();
        return Math.Clamp(100m * competingScore / preferred.StructuralScore, 0m, 100m);
    }

    private bool MeetsMinimumLength(IReadOnlyList<MonoWave> waves) =>
        _options.MinimumWaveLengthAtr <= 0m ||
        waves.All(item => item.LengthAtr is null || item.LengthAtr >= _options.MinimumWaveLengthAtr);

    private static bool IsInvalidated(NeoWaveHypothesis hypothesis, decimal currentPrice) =>
        hypothesis.Invalidation.Comparison switch
        {
            NeoWaveInvalidationComparison.Below =>
                hypothesis.Invalidation.Price is decimal price && currentPrice < price,
            NeoWaveInvalidationComparison.Above =>
                hypothesis.Invalidation.Price is decimal price && currentPrice > price,
            _ => false
        };

    private bool InRangeWithTolerance(decimal? value, decimal minimum, decimal maximum)
    {
        decimal tolerance = _options.FibonacciRatioTolerance;
        return InRange(value, minimum * (1m - tolerance), maximum * (1m + tolerance));
    }

    private bool SimilarTime(MonoWave left, MonoWave right)
    {
        decimal? ratio = Ratio((decimal)right.TimeLength.TotalSeconds, (decimal)left.TimeLength.TotalSeconds);
        return ratio is decimal value &&
            value >= 1m - _options.TimeSimilarityTolerance &&
            value <= 1m + _options.TimeSimilarityTolerance;
    }

    private static bool IsMoreExtreme(SwingPoint candidate, SwingPoint current) => candidate.Type switch
    {
        SwingType.High => candidate.Price > current.Price,
        SwingType.Low => candidate.Price < current.Price,
        _ => false
    };

    private static bool FullyRetraces(MonoWave impulse, MonoWave correction) => impulse.Direction switch
    {
        NeoWaveDirection.Up => correction.EndPrice <= impulse.StartPrice,
        NeoWaveDirection.Down => correction.EndPrice >= impulse.StartPrice,
        _ => false
    };

    private static bool NoWaveOneOverlap(IReadOnlyList<MonoWave> waves)
    {
        NeoWaveDirection direction = waves[0].Direction;
        return direction == NeoWaveDirection.Up
            ? waves[3].EndPrice > waves[0].EndPrice
            : waves[3].EndPrice < waves[0].EndPrice;
    }

    private static bool HasMaterialOverlap(IReadOnlyList<MonoWave> waves)
    {
        decimal lower = waves.Min(item => Math.Min(item.StartPrice, item.EndPrice));
        decimal upper = waves.Max(item => Math.Max(item.StartPrice, item.EndPrice));
        decimal range = upper - lower;
        decimal net = Math.Abs(waves[^1].EndPrice - waves[0].StartPrice);
        return range > 0m && net / range < 0.70m;
    }

    private static bool NetMovementAligned(IReadOnlyList<MonoWave> waves, NeoWaveDirection direction) => direction switch
    {
        NeoWaveDirection.Up => waves[^1].EndPrice > waves[0].StartPrice,
        NeoWaveDirection.Down => waves[^1].EndPrice < waves[0].StartPrice,
        _ => false
    };

    private static bool Alternating(IReadOnlyList<MonoWave> waves)
    {
        for (int index = 1; index < waves.Count; index++)
        {
            if (waves[index].Direction == waves[index - 1].Direction)
                return false;
        }
        return true;
    }

    private static bool InRange(decimal? value, decimal minimum, decimal maximum) =>
        value is decimal actual && actual >= minimum && actual <= maximum;

    private static decimal? Ratio(decimal numerator, decimal denominator) =>
        denominator == 0m ? null : numerator / denominator;

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static string StableId(string prefix, params object?[] parts)
    {
        string canonical = string.Join("|", parts.Select(item => item switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
            decimal value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => item?.ToString() ?? "null"
        }));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private sealed class RuleScore
    {
        private readonly List<string> _supporting = [];
        private readonly List<string> _violated = [];
        public decimal Score { get; private set; }
        public IReadOnlyList<string> Supporting => _supporting;
        public IReadOnlyList<string> Violated => _violated;

        public void Add(string ruleId, bool passed, decimal weight)
        {
            if (passed)
            {
                Score += weight;
                _supporting.Add(ruleId);
            }
            else
            {
                _violated.Add(ruleId);
            }
        }
    }
}
