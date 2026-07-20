namespace LiveTradingHost.Configuration;

public sealed record LiveHostRuntimeOptions
{
    public const string SectionName = "LiveHost";

    public string Urls { get; init; } = "http://127.0.0.1:5088";
    public string HostInstanceId { get; init; } = Environment.MachineName;
    public bool RequireLoopback { get; init; } = true;
    public string ControlToken { get; init; } = string.Empty;
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int ManagementQuoteQueueCapacity { get; init; } = 2_048;

    /// <summary>
    /// Multi-agent architecture Phase 6: bounds how many <c>AgentSupervisor.ApplyAsync</c>
    /// per-agent evaluations may run concurrently for one market update. Defaults to
    /// <c>max(2, ProcessorCount - 1)</c> - never 1 (parallel dispatch would be pointless) or the
    /// full processor count (leaves headroom for the rest of the host's worker tasks).
    /// </summary>
    public int MaxConcurrentAgentEvaluations { get; init; } = Math.Max(2, Environment.ProcessorCount - 1);

    /// <summary>
    /// Multi-agent architecture Phase 6: per-agent evaluation timeout. On expiry the agent is
    /// marked unhealthy (<c>AgentInstanceState.TimeoutCount</c>) but its evaluation task is never
    /// abandoned - see <c>AgentSupervisor</c>'s remarks. Deliberately shorter than
    /// <c>LiveDecisionEpochCoordinatorOptions.BarrierTimeout</c>'s 5s default, so a single slow
    /// agent is flagged before it can make the whole epoch barrier time out.
    /// </summary>
    public TimeSpan AgentEvaluationTimeout { get; init; } = TimeSpan.FromSeconds(4);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Urls) || string.IsNullOrWhiteSpace(HostInstanceId) ||
            ReconciliationInterval <= TimeSpan.Zero ||
            CheckpointInterval <= TimeSpan.Zero || ManagementQuoteQueueCapacity < 128 ||
            MaxConcurrentAgentEvaluations < 1 || AgentEvaluationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LiveHostRuntimeOptions));
        }
        if (!RequireLoopback && string.IsNullOrWhiteSpace(ControlToken))
        {
            throw new InvalidOperationException(
                "Remote live-host control requires a non-empty LiveHost:ControlToken and HTTPS.");
        }
    }
}
