using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Brokers.Models;
using ChartAnnotator.Engine;
using Dashboard.Contracts;
using Microsoft.Extensions.Options;

namespace Dashboard.Live;

internal sealed class BinanceWorkspaceMarketData
{
    private const int MaximumExchangeInfoBytes = 16 * 1024 * 1024;
    private const int MaximumKlineBytes = 4 * 1024 * 1024;
    private const int MaximumSnapshotCacheEntries = 16;
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(15);
    private static readonly string[] Timeframes = ["1m", "3m", "5m", "15m", "30m", "1h", "4h", "1d"];
    private static readonly HashSet<string> SupportedTimeframes = new(Timeframes, StringComparer.Ordinal);
    private static readonly WorkspaceAsset[] FallbackAssets =
    [
        Asset("BTCUSDT", "BTC", "USDT"),
        Asset("ETHUSDT", "ETH", "USDT"),
        Asset("BNBUSDT", "BNB", "USDT"),
        Asset("SOLUSDT", "SOL", "USDT"),
        Asset("XRPUSDT", "XRP", "USDT")
    ];

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly LiveFeedOptions _options;
    private readonly OandaWorkspaceService? _oanda;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SnapshotEntry> _snapshots = new(StringComparer.Ordinal);
    private IReadOnlyList<WorkspaceAsset>? _catalogAssets;
    private DateTimeOffset _catalogExpiresAt;
    private bool _catalogUsesFallback;

    public BinanceWorkspaceMarketData(
        HttpClient httpClient,
        TimeProvider timeProvider,
        IOptions<LiveFeedOptions> options,
        OandaWorkspaceService? oanda = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _options = options.Value;
        _oanda = oanda;
        _options.Validate();
    }

    public async Task<WorkspaceCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkspaceAsset> assets = await GetAssetsAsync(cancellationToken)
            .ConfigureAwait(false);
        WorkspaceBroker oanda = _oanda is null
            ? new WorkspaceBroker(
                "oanda",
                "OANDA",
                WorkspaceEnvironment.Demo,
                WorkspaceDataKind.Market,
                IsConfigured: false,
                IsReadOnly: true,
                "OANDA is available but not connected. Configure its practice account token and account ID on the backend.",
                [])
            : await _oanda.GetBrokerAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return new WorkspaceCatalog(
            now,
            new WorkspaceStreamSelection(
                "binance",
                _options.Symbol.ToUpperInvariant(),
                _options.Interval),
            [
                new WorkspaceBroker(
                    "simulator",
                    "Simulator",
                    WorkspaceEnvironment.Demo,
                    WorkspaceDataKind.Replay,
                    IsConfigured: true,
                    IsReadOnly: true,
                    "Deterministic local replay; no orders or broker account.",
                    [new WorkspaceAsset(
                        "EURUSD",
                        "EUR / USD",
                        "FX:EUR/USD",
                        ["5m", "15m", "1h"])]),
                new WorkspaceBroker(
                    "binance",
                    "Binance Spot",
                    WorkspaceEnvironment.Live,
                    WorkspaceDataKind.Market,
                    IsConfigured: true,
                    IsReadOnly: true,
                    "Live public market data only; no API keys and no order access.",
                    assets),
                oanda,
                new WorkspaceBroker(
                    "ig",
                    "IG",
                    WorkspaceEnvironment.Demo,
                    WorkspaceDataKind.Market,
                    IsConfigured: false,
                    IsReadOnly: true,
                    "Add IG demo credentials and EPIC mappings to enable this broker.",
                    [])
            ],
            _catalogUsesFallback
                ? "Binance pair discovery is temporarily unavailable; showing a small built-in pair list."
                : null);
    }

    public async Task<WorkspaceSnapshot> GetSnapshotAsync(
        string symbol,
        string interval,
        CancellationToken cancellationToken)
    {
        WorkspaceAsset asset = await ResolveAssetAsync(symbol, cancellationToken)
            .ConfigureAwait(false);
        if (!SupportedTimeframes.Contains(interval))
        {
            throw new ArgumentException($"Unsupported workspace timeframe '{interval}'.", nameof(interval));
        }

        string cacheKey = $"{asset.Symbol}:{interval}";
        SnapshotEntry entry = GetSnapshotEntry(cacheKey);
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (entry.Value is not null && entry.ExpiresAt > now)
            {
                return entry.Value;
            }

            WorkspaceSnapshot value = await DownloadSnapshotAsync(asset, interval, cancellationToken)
                .ConfigureAwait(false);
            entry.Value = value;
            entry.ExpiresAt = now + SnapshotLifetime;
            return value;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    internal static IReadOnlyList<WorkspaceAsset> ParseAssets(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("symbols", out JsonElement symbols) ||
            symbols.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The Binance exchange information did not contain a symbols array.");
        }

        var assets = new List<WorkspaceAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in symbols.EnumerateArray())
        {
            string? symbol = GetString(item, "symbol");
            string? status = GetString(item, "status");
            string? baseAsset = GetString(item, "baseAsset");
            string? quoteAsset = GetString(item, "quoteAsset");
            bool spotAllowed = !item.TryGetProperty("isSpotTradingAllowed", out JsonElement allowed) ||
                allowed.ValueKind == JsonValueKind.True;
            if (!spotAllowed ||
                !string.Equals(status, "TRADING", StringComparison.Ordinal) ||
                !IsSafeToken(symbol) ||
                !IsSafeToken(baseAsset) ||
                !IsSafeToken(quoteAsset) ||
                !seen.Add(symbol!))
            {
                continue;
            }

            assets.Add(Asset(symbol!, baseAsset!, quoteAsset!));
        }

        if (assets.Count == 0)
        {
            throw new JsonException("The Binance exchange information contained no supported spot pairs.");
        }

        return assets
            .OrderBy(asset => QuotePriority(asset.DisplayName), Comparer<int>.Default)
            .ThenBy(asset => asset.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<WorkspaceAsset>> GetAssetsAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_catalogAssets is not null && _catalogExpiresAt > now)
        {
            return _catalogAssets;
        }

        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_catalogAssets is not null && _catalogExpiresAt > now)
            {
                return _catalogAssets;
            }

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    "api/v3/exchangeInfo?symbolStatus=TRADING&permissions=SPOT&showPermissionSets=false",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                ThrowIfRateLimited(response, now);
                response.EnsureSuccessStatusCode();
                byte[] body = await ReadBoundedAsync(
                    response,
                    MaximumExchangeInfoBytes,
                    cancellationToken).ConfigureAwait(false);
                _catalogAssets = ParseAssets(body);
                _catalogUsesFallback = false;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A short built-in list keeps replay and known public pairs usable if discovery is offline.
                _catalogAssets = FallbackAssets;
                _catalogUsesFallback = true;
            }

            _catalogExpiresAt = now + CatalogLifetime;
            return _catalogAssets;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<WorkspaceAsset> ResolveAssetAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(symbol))
        {
            throw new ArgumentException("A valid Binance symbol is required.", nameof(symbol));
        }

        IReadOnlyList<WorkspaceAsset> assets = await GetAssetsAsync(cancellationToken)
            .ConfigureAwait(false);
        return assets.FirstOrDefault(asset =>
                string.Equals(asset.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Binance spot pair '{symbol}' is not available.");
    }

    private async Task<WorkspaceSnapshot> DownloadSnapshotAsync(
        WorkspaceAsset asset,
        string intervalName,
        CancellationToken cancellationToken)
    {
        var instrument = new InstrumentKey(asset.Instrument);
        BarInterval interval = BinanceInterval.Parse(intervalName);
        string path =
            $"api/v3/klines?symbol={Uri.EscapeDataString(asset.Symbol)}" +
            $"&interval={Uri.EscapeDataString(intervalName)}&limit={_options.WarmupCandles}";
        using HttpResponseMessage response = await _httpClient.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        ThrowIfRateLimited(response, _timeProvider.GetUtcNow());
        response.EnsureSuccessStatusCode();
        byte[] body = await ReadBoundedAsync(response, MaximumKlineBytes, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        IReadOnlyList<Candle> candles = BinanceKlineParser.ParseRest(body, instrument, interval, now);
        ChartAnnotationOptions annotationOptions = CreateAnnotationOptions();
        var analysis = new LiveAnalysisState(
            instrument,
            interval,
            annotationOptions,
            _options.FrameCapacity);
        foreach (Candle candle in candles)
        {
            await analysis.ProcessAsync(candle, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<ReplayFrame> frames = analysis.Snapshot();
        DateTimeOffset generatedAt = frames.Count > 0 ? frames[^1].AvailableAt : now;
        var status = new LiveFeedStatus
        {
            State = frames.Count > 0 ? LiveConnectionState.Connected : LiveConnectionState.WarmingUp,
            Symbol = asset.Symbol,
            Interval = intervalName,
            Message = "Polling Binance public closed candles for this workspace selection.",
            ConnectedAt = now,
            LastMessageAt = now,
            LastClosedCandleAt = frames.Count > 0 ? frames[^1].AvailableAt : null,
            GapsDetected = analysis.GapsDetected
        };
        var dataset = new ReplayDataset(
            1,
            $"{asset.DisplayName} live closed-candle analysis",
            asset.Instrument,
            generatedAt,
            "Binance public market-data REST workspace snapshot",
            annotationOptions,
            [new ReplaySeries(
                intervalName,
                checked((int)(interval.AddTo(DateTimeOffset.UnixEpoch) -
                    DateTimeOffset.UnixEpoch).TotalSeconds),
                frames)]);
        return new WorkspaceSnapshot(status, dataset);
    }

    private SnapshotEntry GetSnapshotEntry(string cacheKey)
    {
        if (_snapshots.TryGetValue(cacheKey, out SnapshotEntry? existing))
        {
            return existing;
        }

        if (_snapshots.Count >= MaximumSnapshotCacheEntries)
        {
            KeyValuePair<string, SnapshotEntry> oldest = _snapshots
                .Where(item => item.Value.Gate.CurrentCount > 0)
                .OrderBy(item => item.Value.ExpiresAt)
                .FirstOrDefault();
            if (oldest.Key is not null)
            {
                _snapshots.TryRemove(oldest.Key, out _);
            }
        }

        return _snapshots.GetOrAdd(cacheKey, static _ => new SnapshotEntry());
    }

    private static ChartAnnotationOptions CreateAnnotationOptions() => new()
    {
        AtrPeriod = 14,
        RsiPeriod = 14,
        BollingerPeriod = 20,
        BollingerStandardDeviations = 2m,
        SwingLeftBars = 2,
        SwingRightBars = 2,
        HeavyAnalysisEveryCandles = 6
    };

    private static WorkspaceAsset Asset(string symbol, string baseAsset, string quoteAsset) =>
        new(
            symbol,
            $"{baseAsset} / {quoteAsset}",
            $"CRYPTO:{baseAsset}/{quoteAsset}",
            Timeframes);

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 24 &&
        value.All(char.IsAsciiLetterOrDigit);

    private static int QuotePriority(string displayName) => displayName.EndsWith(" / USDT", StringComparison.Ordinal) ? 0
        : displayName.EndsWith(" / USDC", StringComparison.Ordinal) ? 1
        : displayName.EndsWith(" / BTC", StringComparison.Ordinal) ? 2
        : displayName.EndsWith(" / EUR", StringComparison.Ordinal) ? 3
        : 4;

    private static void ThrowIfRateLimited(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or (HttpStatusCode)418))
        {
            return;
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            retryAfter = date - now;
        }

        throw new BinanceRateLimitException(response.StatusCode, retryAfter);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException($"The Binance response exceeded {maximumBytes} bytes.");
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (body.Length > maximumBytes)
        {
            throw new InvalidDataException($"The Binance response exceeded {maximumBytes} bytes.");
        }

        return body;
    }

    private sealed class SnapshotEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public WorkspaceSnapshot? Value { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
