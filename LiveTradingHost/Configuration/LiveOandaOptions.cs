using Brokers.Abstractions;
using Brokers.Oanda;

namespace LiveTradingHost.Configuration;

/// <summary>Config-bound wrapper around <see cref="OandaOptions"/> (which is a plain data
/// carrier with no <c>Enabled</c>/validation of its own) - mirrors <c>DashboardLive.
/// OandaWorkspaceOptions</c>'s shape exactly, including its env-var override convention
/// (<c>Oanda__AccountId</c> / <c>Oanda__AccessToken</c>).</summary>
public sealed record LiveOandaOptions
{
    public const string SectionName = "Oanda";

    public bool Enabled { get; init; }
    public BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    public string AccountId { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public int RequestTimeoutSeconds { get; init; } = 20;
    public Uri? RestBaseAddress { get; init; }
    public Uri? StreamBaseAddress { get; init; }
    public IReadOnlyDictionary<string, string> InstrumentMappings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => Enabled &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(AccessToken);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccessToken);
        if (Environment is not (BrokerEnvironment.Demo or BrokerEnvironment.Live))
        {
            throw new ArgumentOutOfRangeException(nameof(Environment));
        }

        if (RequestTimeoutSeconds is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds));
        }
    }

    public OandaOptions ToOandaOptions() => new()
    {
        Environment = Environment,
        AccountId = AccountId,
        AccessToken = AccessToken,
        BaseAddress = RestBaseAddress,
        StreamBaseAddress = StreamBaseAddress,
        RequestTimeout = TimeSpan.FromSeconds(RequestTimeoutSeconds),
        InstrumentMappings = InstrumentMappings
    };
}
