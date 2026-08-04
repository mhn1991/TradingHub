namespace Dashboard.Live;

/// <summary>
/// Deliberately separate, minimal DTOs for the standalone agent-debugger page - not the general
/// ReplayFrame/IndicatorSnapshot contracts used by the main dashboard. See PROJECT_STATE.md §2.8's
/// "agent debugger" entry for why this stays decoupled from the Simulator/execution pipeline.
/// </summary>
public sealed record AgentDebugRunRequest
{
    public required string Instrument { get; init; }

    /// <summary>Timeframe strings (e.g. "1h","30m") the agent treats as MonitoredIntervals - any
    /// one can independently trigger a signal.</summary>
    public required IReadOnlyList<string> MonitoredTimeframes { get; init; }

    /// <summary>Lower timeframes checked, in the given order, only when a monitored-timeframe
    /// reading is Partial.</summary>
    public IReadOnlyList<string> ConfirmationTimeframes { get; init; } = [];

    /// <summary>Candles from here are fed into the aggregator/analysis engine silently to prime
    /// indicators - no diagnostic/decision events are emitted until <see cref="RunStart"/>.</summary>
    public required DateTimeOffset WarmupStart { get; init; }

    public required DateTimeOffset RunStart { get; init; }
    public required DateTimeOffset RunEnd { get; init; }

    public decimal Quantity { get; init; } = 1_000m;
    public decimal RsiOverbought { get; init; } = 75m;
    public decimal RsiOversold { get; init; } = 25m;
    public decimal StochRsiFastOverbought { get; init; } = 100m;
    public decimal StochRsiFastOversold { get; init; } = 0m;
    public decimal PartialStochRsiFastOverbought { get; init; } = 85m;
    public decimal PartialStochRsiFastOversold { get; init; } = 15m;
    public decimal ProtectiveStopAtrMultiple { get; init; } = 3.0m;
}

public sealed record AgentDebugInstrumentInfo(string Instrument, DateTimeOffset CachedFrom, DateTimeOffset CachedTo);

public enum AgentDebugEventType
{
    /// <summary>A timeframe candle closed - OHLC + indicator readout.</summary>
    Candle,
    /// <summary>Display-only extreme-condition classification for one timeframe on one candle
    /// close (mirrors DivergenceReversalAgent.ClassifyExtreme; never drives the real decision).</summary>
    Diagnostic,
    /// <summary>The actual AgentDecision returned by a real DivergenceReversalAgent.EvaluateAsync
    /// call - ground truth, not a re-derivation.</summary>
    Decision,
    Status,
    Error,
    Complete
}

public sealed record AgentDebugEvent
{
    public required AgentDebugEventType Type { get; init; }
    public required DateTimeOffset Time { get; init; }

    // Candle
    public string? Interval { get; init; }
    public bool? IsBaseInterval { get; init; }
    public decimal? Open { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public decimal? Close { get; init; }
    public decimal? Rsi { get; init; }
    public decimal? BollingerUpper { get; init; }
    public decimal? BollingerMiddle { get; init; }
    public decimal? BollingerLower { get; init; }
    public decimal? StochRsiFast { get; init; }
    public decimal? StochRsiSlow { get; init; }
    public decimal? Atr { get; init; }

    // Diagnostic
    public string? Role { get; init; }
    public string? Certainty { get; init; }
    public string? Direction { get; init; }
    public string? RelationshipType { get; init; }
    public decimal? RelationshipStrength { get; init; }
    public bool? IsNewRelationship { get; init; }

    // Decision
    public string? Action { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public string? Reason { get; init; }

    // Status / Error / Complete
    public string? Message { get; init; }
    public int? TotalTrades { get; init; }
}
