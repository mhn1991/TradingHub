namespace ChartAnnotator.NeoWave;

public enum NeoWaveDirection
{
    Neutral,
    Up,
    Down
}

public enum NeoWaveDegree
{
    Micro,
    Minor,
    Intermediate,
    Primary
}

public enum NeoWavePatternType
{
    Unknown,
    TrendSequence,
    ImpulseCandidate,
    ZigZagCorrection,
    FlatCorrection,
    TriangleCorrection,
    ComplexCorrection
}

public enum NeoWaveHypothesisStatus
{
    Possible,
    Confirmed,
    Preferred,
    Invalidated
}

public enum NeoWaveInvalidationComparison
{
    None,
    Below,
    Above
}

public sealed record MonoWave
{
    public required string WaveId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required DateTimeOffset StartConfirmedAt { get; init; }
    public required DateTimeOffset EndConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required decimal StartPrice { get; init; }
    public required decimal EndPrice { get; init; }
    public required NeoWaveDirection Direction { get; init; }
    public required decimal PriceLength { get; init; }
    public required TimeSpan TimeLength { get; init; }
    public decimal? LengthAtr { get; init; }
    public bool IsConfirmed { get; init; } = true;
}

public sealed record ProvisionalMonoWave
{
    public required string WaveId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset CurrentTime { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required decimal StartPrice { get; init; }
    public required decimal CurrentPrice { get; init; }
    public required NeoWaveDirection Direction { get; init; }
    public required decimal PriceLength { get; init; }
    public decimal? LengthAtr { get; init; }
}

public sealed record MonoWaveRelationship
{
    public required string LeftWaveId { get; init; }
    public required string RightWaveId { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public decimal? PriceRatio { get; init; }
    public decimal? TimeRatio { get; init; }
    public required bool FullyRetracesPrevious { get; init; }
    public required bool ReturnsInsidePreviousOrigin { get; init; }
}

public sealed record NeoWaveInvalidationCondition
{
    public static NeoWaveInvalidationCondition None { get; } = new()
    {
        Comparison = NeoWaveInvalidationComparison.None,
        Description = "No deterministic invalidation level is available."
    };

    public required NeoWaveInvalidationComparison Comparison { get; init; }
    public decimal? Price { get; init; }
    public required string Description { get; init; }
}

public sealed record NeoWaveHypothesis
{
    public required string HypothesisId { get; init; }
    public required NeoWavePatternType PatternType { get; init; }
    public required NeoWaveDirection Direction { get; init; }
    public required NeoWaveDegree Degree { get; init; }
    public required IReadOnlyList<string> ComponentWaveIds { get; init; }
    public required NeoWaveHypothesisStatus Status { get; init; }
    public required decimal StructuralScore { get; init; }
    public required decimal Maturity { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyList<string> SupportingRuleIds { get; init; }
    public required IReadOnlyList<string> ViolatedRuleIds { get; init; }
    public required NeoWaveInvalidationCondition Invalidation { get; init; }
}

public sealed record NeoWaveQuality
{
    public static NeoWaveQuality Empty { get; } = new();

    public bool IsReady { get; init; }
    public int ConfirmedSwingCount { get; init; }
    public int ConfirmedMonoWaveCount { get; init; }
    public int HypothesisCount { get; init; }
    public int PrunedHypothesisCount { get; init; }
    public string ReasonCode { get; init; } = "Disabled";
}

public sealed record NeoWaveSnapshot
{
    public static NeoWaveSnapshot Disabled { get; } = new()
    {
        Enabled = false,
        AvailableAt = DateTimeOffset.MinValue,
        ConfirmedMonoWaves = [],
        Relationships = [],
        Hypotheses = [],
        StructuralBias = NeoWaveDirection.Neutral,
        ReasonCodes = ["NeoWaveDisabled"],
        Quality = NeoWaveQuality.Empty
    };

    public bool Enabled { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required IReadOnlyList<MonoWave> ConfirmedMonoWaves { get; init; }
    public ProvisionalMonoWave? ProvisionalWave { get; init; }
    public required IReadOnlyList<MonoWaveRelationship> Relationships { get; init; }
    public required IReadOnlyList<NeoWaveHypothesis> Hypotheses { get; init; }
    public string? PreferredHypothesisId { get; init; }
    public NeoWaveDirection StructuralBias { get; init; }
    public decimal StructuralScore { get; init; }
    public decimal Maturity { get; init; }
    public decimal ConflictScore { get; init; }
    public decimal? InvalidationPrice { get; init; }
    public decimal? InvalidationDistanceAtr { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];
    public NeoWaveQuality Quality { get; init; } = NeoWaveQuality.Empty;
}
