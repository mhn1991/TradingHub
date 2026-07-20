namespace Dashboard.Live;

public enum ResearchJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

/// <summary>Starts a server-owned calibration run over a real simulator backtest window.</summary>
public sealed record ResearchCalibrationRequest
{
    /// <summary>One of: setup, management, metamodel.</summary>
    public required string Kind { get; init; }
    public required string Instrument { get; init; }
    /// <summary>Catalog strategy id: legacy, improved, or structural-confluence.</summary>
    public required string Strategy { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Quantity { get; init; } = 1_000m;
    public string SourceKind { get; init; } = "OandaCandles";
    public int WarmupDays { get; init; } = 21;
    public string? Description { get; init; }
    /// <summary>Required when Kind is "management" - the frozen setup-calibration artifact to build management trades against.</summary>
    public Guid? ExistingSetupArtifactId { get; init; }
    /// <summary>Required when Kind is "management" - the frozen meta-model artifact to build management trades against.</summary>
    public Guid? ExistingMetaModelArtifactId { get; init; }
}

public sealed record ResearchJobSnapshot
{
    public required Guid JobId { get; init; }
    public required string Kind { get; init; }
    public required ResearchJobStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string Instrument { get; init; }
    public required string Strategy { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public Guid? ArtifactId { get; init; }
    public string? ArtifactType { get; init; }
    public string? CalibrationId { get; init; }
    public int TradeCount { get; init; }
    public int OutcomeCount { get; init; }
    public int BucketOrCohortCount { get; init; }
}

public sealed record ResearchSelectionState
{
    public string? SetupCalibrationArtifactId { get; init; }
    public string? ManagementCalibrationArtifactId { get; init; }
    public string? MetaModelArtifactId { get; init; }
}
