namespace Networking.Notifications;

/// <summary>Which way a signal points. Deliberately independent of <c>Agent.Models.AgentAction</c>
/// so this project stays a dependency-free leaf that strategy code can reference.</summary>
public enum SignalSide
{
    Buy,
    Sell
}

/// <summary>
/// One outbound signal alert. A flat, self-describing payload: everything a human needs to read
/// the alert without opening the dashboard, and nothing that ties this project to the strategy
/// or annotator assemblies.
/// </summary>
public sealed record SignalNotification
{
    /// <summary>Instrument the signal is for, e.g. <c>METAL:XAU/USD</c>.</summary>
    public required string Instrument { get; init; }

    public required SignalSide Side { get; init; }

    /// <summary>Agent or strategy that produced it, for when several are running at once.</summary>
    public required string Strategy { get; init; }

    /// <summary>Candle close the decision was taken on — not the send time, which may lag.</summary>
    public required DateTimeOffset DecisionTime { get; init; }

    /// <summary>Timeframe the trigger fired on, e.g. <c>5m</c>. Optional.</summary>
    public string? Interval { get; init; }

    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }

    /// <summary>0-100 as produced by the agent.</summary>
    public decimal? Confidence { get; init; }

    /// <summary>Free-form explanation; shown verbatim.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Stable key for suppressing duplicates. Defaults to instrument + side + decision time, so
    /// re-evaluating the same closed candle will not send twice. Override to widen or narrow it.
    /// </summary>
    public string DeduplicationKey =>
        $"{Instrument}|{Side}|{Strategy}|{DecisionTime.ToUniversalTime():O}";
}
