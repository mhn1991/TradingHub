using Brokers;
using Brokers.Abstractions;
using Brokers.Models;
using Brokers.Oanda;
using Microsoft.Extensions.Options;
using Simulator.MarketData;

namespace Dashboard.Live;

public sealed record SimulationInstrumentOption(
    string Symbol,
    string DisplayName,
    string Instrument,
    string AssetClass);

public sealed record SimulationBrokerOption(
    string Id,
    string DisplayName,
    string Environment,
    string SourceKind,
    bool IsAvailable,
    bool RequiresCredentials,
    string Description,
    IReadOnlyList<string> SupportedExecutionIntervals,
    IReadOnlyList<SimulationInstrumentOption> Instruments);

public sealed record SimulationBrokerCatalog(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SimulationBrokerOption> Brokers,
    string? Warning = null);

/// <summary>
/// Capability-aware broker and instrument discovery for the historical simulator.
/// The catalog is cached so opening the simulator does not repeatedly call broker APIs.
/// </summary>
internal sealed class SimulationBrokerCatalogService
{
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(30);
    private readonly BinanceWorkspaceMarketData _workspaceMarketData;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SimulationBrokerCatalogService> _logger;
    private readonly OandaWorkspaceOptions _oandaOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SimulationBrokerCatalog? _catalog;
    private DateTimeOffset _expiresAt;

    public SimulationBrokerCatalogService(
        BinanceWorkspaceMarketData workspaceMarketData,
        TimeProvider timeProvider,
        IOptions<OandaWorkspaceOptions> oandaOptions,
        ILogger<SimulationBrokerCatalogService> logger)
    {
        _workspaceMarketData = workspaceMarketData ?? throw new ArgumentNullException(nameof(workspaceMarketData));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _oandaOptions = oandaOptions?.Value ?? throw new ArgumentNullException(nameof(oandaOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SimulationBrokerCatalog> GetAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!refresh && _catalog is not null && _expiresAt > now)
            return _catalog;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (!refresh && _catalog is not null && _expiresAt > now)
                return _catalog;

            WorkspaceCatalog workspaceCatalog = await _workspaceMarketData
                .GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            WorkspaceBroker? workspaceBinance = workspaceCatalog.Brokers.FirstOrDefault(
                broker => string.Equals(broker.Id, "binance", StringComparison.OrdinalIgnoreCase));
            WorkspaceBroker? workspaceOanda = workspaceCatalog.Brokers.FirstOrDefault(
                broker => string.Equals(broker.Id, "oanda", StringComparison.OrdinalIgnoreCase));

            SimulationBrokerOption binance = BuildBinance(workspaceBinance);
            SimulationBrokerOption oanda = workspaceOanda is { IsConfigured: true, Assets.Count: > 0 }
                ? BuildOandaFromWorkspace(workspaceOanda)
                : await BuildOandaFromSimulatorCredentialsAsync(cancellationToken).ConfigureAwait(false);

            var imported = new SimulationBrokerOption(
                "imported",
                "Imported candle dataset",
                "Local",
                HistoricalDataSourceKind.ImportedSecondCandles.ToString(),
                IsAvailable: true,
                RequiresCredentials: false,
                "Upload a validated 1-second or 5-second midpoint OHLC CSV. The instrument is supplied with the dataset run.",
                FormatIntervals(ImportedSecondCandleCapabilities.Instance.SupportedExecutionIntervals),
                []);

            var ig = new SimulationBrokerOption(
                "ig",
                "IG",
                "Demo",
                "Unavailable",
                IsAvailable: false,
                RequiresCredentials: true,
                "IG broker integration exists for account/order workflows, but a paged historical candle source and instrument catalog are not yet certified for the simulator.",
                [],
                []);

            string? warning = workspaceCatalog.Warning;
            if (!oanda.IsAvailable)
            {
                warning = string.Join(" ", new[]
                {
                    warning,
                    "OANDA instruments are unavailable until simulator credentials are configured."
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            _catalog = new SimulationBrokerCatalog(now, [oanda, binance, imported, ig], warning);
            _expiresAt = now + CatalogLifetime;
            return _catalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SimulationBrokerOption> ValidateSelectionAsync(
        string brokerId,
        string instrument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        SimulationBrokerCatalog catalog = await GetAsync(refresh: false, cancellationToken)
            .ConfigureAwait(false);
        SimulationBrokerOption broker = catalog.Brokers.FirstOrDefault(item =>
                string.Equals(item.Id, brokerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown simulation broker '{brokerId}'.", nameof(brokerId));
        if (!broker.IsAvailable)
            throw new InvalidOperationException(broker.Description);

        if (broker.Instruments.Count > 0 && !broker.Instruments.Any(asset =>
                string.Equals(asset.Instrument, instrument, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Instrument '{instrument}' is not available for {broker.DisplayName}. Refresh the broker catalog and select an available asset.",
                nameof(instrument));
        }

        return broker;
    }

    private static SimulationBrokerOption BuildBinance(WorkspaceBroker? broker)
    {
        IReadOnlyList<SimulationInstrumentOption> instruments = broker?.Assets
            .Select(asset => new SimulationInstrumentOption(
                asset.Symbol,
                asset.DisplayName,
                asset.Instrument,
                "Crypto Spot"))
            .OrderBy(asset => QuotePriority(asset.DisplayName))
            .ThenBy(asset => asset.DisplayName, StringComparer.Ordinal)
            .ToArray() ?? [];

        return new SimulationBrokerOption(
            "binance",
            "Binance Spot",
            "Live public data",
            HistoricalDataSourceKind.BinanceCandles.ToString(),
            IsAvailable: instruments.Count > 0,
            RequiresCredentials: false,
            instruments.Count > 0
                ? "Public Binance spot candles. Historical simulation does not require API keys; this does not enable live order execution."
                : "Binance public instrument discovery is currently unavailable.",
            FormatIntervals(BinanceCandleCapabilities.Instance.SupportedExecutionIntervals),
            instruments);
    }

    private static SimulationBrokerOption BuildOandaFromWorkspace(WorkspaceBroker broker) => new(
        "oanda",
        "OANDA",
        broker.Environment.ToString(),
        HistoricalDataSourceKind.OandaCandles.ToString(),
        IsAvailable: broker.Assets.Count > 0,
        RequiresCredentials: true,
        broker.Description,
        FormatIntervals(OandaCandleCapabilities.Instance.SupportedExecutionIntervals),
        broker.Assets.Select(asset => new SimulationInstrumentOption(
                asset.Symbol,
                asset.DisplayName,
                asset.Instrument,
                AssetClassFromInstrument(asset.Instrument)))
            .OrderBy(asset => AssetClassPriority(asset.AssetClass))
            .ThenBy(asset => asset.DisplayName, StringComparer.Ordinal)
            .ToArray());

    private async Task<SimulationBrokerOption> BuildOandaFromSimulatorCredentialsAsync(
        CancellationToken cancellationToken)
    {
        string? accountId = _oandaOptions.AccountId;
        string? accessToken = _oandaOptions.AccessToken;
        BrokerEnvironment environment = _oandaOptions.Environment;

        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return new SimulationBrokerOption(
                "oanda",
                "OANDA",
                environment.ToString(),
                HistoricalDataSourceKind.OandaCandles.ToString(),
                IsAvailable: false,
                RequiresCredentials: true,
                "Store enabled OANDA credentials in the broker credential database to discover the instruments available to the account.",
                FormatIntervals(OandaCandleCapabilities.Instance.SupportedExecutionIntervals),
                []);
        }

        try
        {
            await using OandaBrokerClient client = BrokerClientFactory.CreateOanda(new OandaOptions
            {
                Environment = environment,
                AccountId = accountId,
                AccessToken = accessToken
            });
            IReadOnlyList<OandaInstrumentInfo> instruments = await client
                .GetInstrumentsAsync(cancellationToken).ConfigureAwait(false);
            return new SimulationBrokerOption(
                "oanda",
                "OANDA",
                environment.ToString(),
                HistoricalDataSourceKind.OandaCandles.ToString(),
                IsAvailable: instruments.Count > 0,
                RequiresCredentials: true,
                "Historical candles and the instruments enabled for the configured OANDA account.",
                FormatIntervals(OandaCandleCapabilities.Instance.SupportedExecutionIntervals),
                instruments.Select(ToSimulationInstrument)
                    .OrderBy(asset => AssetClassPriority(asset.AssetClass))
                    .ThenBy(asset => asset.DisplayName, StringComparer.Ordinal)
                    .ToArray());
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "OANDA simulator instrument discovery failed.");
            return new SimulationBrokerOption(
                "oanda",
                "OANDA",
                environment.ToString(),
                HistoricalDataSourceKind.OandaCandles.ToString(),
                IsAvailable: false,
                RequiresCredentials: true,
                "OANDA credentials are present, but account instrument discovery failed. Check the backend log and credentials.",
                FormatIntervals(OandaCandleCapabilities.Instance.SupportedExecutionIntervals),
                []);
        }
    }

    private static SimulationInstrumentOption ToSimulationInstrument(OandaInstrumentInfo instrument)
    {
        string assetClass = instrument.Type.ToUpperInvariant() switch
        {
            "CURRENCY" => "Forex",
            "METAL" => "Metals",
            "CFD" => "CFDs",
            _ => "Other"
        };
        string prefix = assetClass switch
        {
            "Forex" => "FX",
            "Metals" => "METAL",
            "CFDs" => "CFD",
            _ => "OANDA"
        };
        string pair = instrument.Name.Replace('_', '/');
        return new SimulationInstrumentOption(
            instrument.Name,
            instrument.DisplayName,
            $"{prefix}:{pair}",
            assetClass);
    }

    private static string AssetClassFromInstrument(string instrument) =>
        instrument.Split(':', 2)[0].ToUpperInvariant() switch
        {
            "FX" => "Forex",
            "METAL" => "Metals",
            "CFD" => "CFDs",
            "CRYPTO" => "Crypto Spot",
            _ => "Other"
        };

    private static IReadOnlyList<string> FormatIntervals(IEnumerable<BarInterval> intervals) =>
        intervals.OrderBy(IntervalOrder).Select(BarIntervalParser.Format).ToArray();

    private static long IntervalOrder(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => interval.Value,
        BarUnit.Minute => interval.Value * 60L,
        BarUnit.Hour => interval.Value * 3_600L,
        BarUnit.Day => interval.Value * 86_400L,
        BarUnit.Week => interval.Value * 604_800L,
        BarUnit.Month => interval.Value * 2_678_400L,
        _ => long.MaxValue
    };

    private static int AssetClassPriority(string value) => value switch
    {
        "Forex" => 0,
        "Metals" => 1,
        "CFDs" => 2,
        "Crypto Spot" => 3,
        _ => 4
    };

    private static int QuotePriority(string displayName) =>
        displayName.EndsWith(" / USDT", StringComparison.Ordinal) ? 0
        : displayName.EndsWith(" / USDC", StringComparison.Ordinal) ? 1
        : displayName.EndsWith(" / BTC", StringComparison.Ordinal) ? 2
        : displayName.EndsWith(" / EUR", StringComparison.Ordinal) ? 3
        : 4;

    private static BrokerEnvironment ParseOandaEnvironment(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out BrokerEnvironment environment) &&
        environment is BrokerEnvironment.Demo or BrokerEnvironment.Live
            ? environment
            : BrokerEnvironment.Demo;
}
