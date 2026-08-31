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

    /// <summary>
    /// Instruments to fall back to when account instrument discovery fails and no cached list is
    /// available - the state a restart lands in during a broker outage. Symbols use OANDA's own
    /// form (<c>XAU_USD</c>, <c>EUR_USD</c>). Empty by default, so this changes nothing unless set.
    /// <para>
    /// Discovery is account-scoped, but candles are not (<c>v3/instruments/{i}/candles</c>), so a
    /// discovery outage leaves market data perfectly usable - the workspace only lacks the names
    /// of the instruments to request. This supplies those names. Observed 2026-08-28/29: OANDA's
    /// practice environment returned 503 on <c>/v3/accounts/{id}/instruments</c> and the streaming
    /// routes for hours, while practice candles and the live environment were healthy throughout.
    /// </para>
    /// <para>
    /// A fallback entry is a claim that the account can trade the symbol, which discovery would
    /// normally have proven. A wrong entry surfaces as a failed candle request for that symbol,
    /// not as a bad order - order execution is independently gated and impossible on Live.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> FallbackInstruments { get; init; } = [];

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
        foreach (string symbol in FallbackInstruments)
        {
            if (string.IsNullOrWhiteSpace(symbol) || !symbol.Contains('_', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Fallback instrument '{symbol}' is not an OANDA symbol; expected a form such as XAU_USD.");
            }
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
