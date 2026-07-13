using Brokers.Abstractions;

namespace Dashboard.Live;

public sealed record OandaWorkspaceOptions
{
    public const string SectionName = "Oanda";

    public bool Enabled { get; init; }
    public BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    public string AccountId { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public bool AllowDemoOrders { get; init; }
    public int WarmupCandles { get; init; } = 250;
    public int FrameCapacity { get; init; } = 500;
    public int RequestTimeoutSeconds { get; init; } = 20;
    public Uri? RestBaseAddress { get; init; }
    public Uri? StreamBaseAddress { get; init; }

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
        if (AllowDemoOrders && Environment != BrokerEnvironment.Demo)
        {
            throw new InvalidOperationException(
                "OANDA workspace order execution can only be enabled for a Demo account.");
        }
        if (WarmupCandles is < 21 or > 5_000)
        {
            throw new ArgumentOutOfRangeException(nameof(WarmupCandles));
        }
        if (FrameCapacity is < 50 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameCapacity));
        }
        if (RequestTimeoutSeconds is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds));
        }
        ValidateAddress(RestBaseAddress, Uri.UriSchemeHttps, nameof(RestBaseAddress));
        ValidateAddress(StreamBaseAddress, Uri.UriSchemeHttps, nameof(StreamBaseAddress));
    }

    private static void ValidateAddress(Uri? address, string scheme, string propertyName)
    {
        if (address is not null && (!address.IsAbsoluteUri || address.Scheme != scheme))
        {
            throw new ArgumentException($"{propertyName} must be an absolute HTTPS address.", propertyName);
        }
    }
}
