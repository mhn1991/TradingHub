namespace LiveTradingHost.Configuration;

public sealed record LiveHostRuntimeOptions
{
    public const string SectionName = "LiveHost";

    public string Urls { get; init; } = "http://127.0.0.1:5088";
    public bool RequireLoopback { get; init; } = true;
    public string ControlToken { get; init; } = string.Empty;
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int ManagementQuoteQueueCapacity { get; init; } = 2_048;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Urls) || ReconciliationInterval <= TimeSpan.Zero ||
            CheckpointInterval <= TimeSpan.Zero || ManagementQuoteQueueCapacity < 128)
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
