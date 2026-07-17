namespace ChartAnnotator.NeoWave;

/// <summary>
/// Configuration for the causal monowave/structural-hypothesis engine. The implementation is a
/// deterministic, independently expressed subset of wave-analysis concepts; it does not claim
/// complete compatibility with any proprietary discretionary methodology.
/// </summary>
public sealed record NeoWaveOptions
{
    public bool Enabled { get; init; }
    public NeoWaveDegree Degree { get; init; } = NeoWaveDegree.Minor;
    public int MinimumConfirmedMonoWaves { get; init; } = 3;
    public int MaximumConfirmedMonoWaves { get; init; } = 34;
    public int MaximumHypotheses { get; init; } = 12;
    public bool IncludeProvisionalWave { get; init; } = true;
    /// <summary>Minimum ATR-normalized leg size required for pattern classification.
    /// Confirmed monowaves remain visible even when smaller, preserving a contiguous audit trail.</summary>
    public decimal MinimumWaveLengthAtr { get; init; } = 0.10m;
    public decimal FibonacciRatioTolerance { get; init; } = 0.15m;
    public decimal TimeSimilarityTolerance { get; init; } = 0.35m;
    public decimal TriangleContractionTolerance { get; init; } = 0.10m;
    public decimal MinimumHypothesisScore { get; init; } = 45m;
    public decimal PreferredHypothesisMinimumScore { get; init; } = 55m;

    public void Validate()
    {
        if (!Enum.IsDefined(Degree) ||
            MinimumConfirmedMonoWaves < 2 ||
            MaximumConfirmedMonoWaves is < 5 or > 55 ||
            MinimumConfirmedMonoWaves > MaximumConfirmedMonoWaves ||
            MaximumHypotheses is < 1 or > 100 ||
            MinimumWaveLengthAtr < 0m ||
            FibonacciRatioTolerance is < 0m or > 1m ||
            TimeSimilarityTolerance is < 0m or > 2m ||
            TriangleContractionTolerance is < 0m or > 1m ||
            MinimumHypothesisScore is < 0m or > 100m ||
            PreferredHypothesisMinimumScore is < 0m or > 100m ||
            PreferredHypothesisMinimumScore < MinimumHypothesisScore)
        {
            throw new ArgumentOutOfRangeException(nameof(NeoWaveOptions));
        }
    }
}
