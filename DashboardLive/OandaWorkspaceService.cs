using Brokers.Abstractions;
using Brokers.Models;
using Brokers.Oanda;
using Dashboard.Contracts;
using Microsoft.Extensions.Options;

namespace Dashboard.Live;

internal sealed class OandaWorkspaceService : BackgroundService
{
    private const int MaximumSessions = 8;
    private static readonly TimeSpan AssetLifetime = TimeSpan.FromMinutes(30);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _assetGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly OandaWorkspaceOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OandaWorkspaceService> _logger;
    private readonly OandaBrokerClient? _client;
    private readonly Dictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<OandaLiveAnalysisSession> _retiredSessions = [];
    private readonly List<RevisionedOrderEvent> _orderEvents = [];
    private TaskCompletionSource<long> _ordersChanged = NewChangeSignal();
    private IReadOnlyList<WorkspaceAsset>? _assets;
    private DateTimeOffset _assetsExpireAt;
    private long _orderRevision;
    private int _disposed;

    public OandaWorkspaceService(
        IOptions<OandaWorkspaceOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ILogger<OandaWorkspaceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _options.Validate();
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = logger;
        if (_options.IsConfigured)
        {
            _client = new OandaBrokerClient(new OandaOptions
            {
                Environment = _options.Environment,
                AccountId = _options.AccountId,
                AccessToken = _options.AccessToken,
                BaseAddress = _options.RestBaseAddress,
                StreamBaseAddress = _options.StreamBaseAddress,
                RequestTimeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds)
            });
        }
    }

    public bool IsConfigured => _client is not null;
    public bool DemoOrderExecutionEnabled =>
        _client is not null &&
        _options.Environment == BrokerEnvironment.Demo &&
        _options.AllowDemoOrders;

    public async Task<WorkspaceBroker> GetBrokerAsync(CancellationToken cancellationToken)
    {
        WorkspaceEnvironment environment = _options.Environment == BrokerEnvironment.Live
            ? WorkspaceEnvironment.Live
            : WorkspaceEnvironment.Demo;
        if (_client is null)
        {
            return new WorkspaceBroker(
                "oanda",
                "OANDA",
                environment,
                WorkspaceDataKind.Market,
                IsConfigured: false,
                IsReadOnly: true,
                "OANDA is disabled on the backend. Import an enabled OANDA credential into the broker credential database.",
                []);
        }

        try
        {
            IReadOnlyList<WorkspaceAsset> assets = await GetAssetsAsync(cancellationToken)
                .ConfigureAwait(false);
            return new WorkspaceBroker(
                "oanda",
                "OANDA",
                environment,
                WorkspaceDataKind.Market,
                IsConfigured: true,
                IsReadOnly: !DemoOrderExecutionEnabled,
                DemoOrderExecutionEnabled
                    ? "Connected to OANDA practice pricing and candles; demo orders are enabled by backend policy."
                    : "Connected to OANDA pricing and candles; order execution is disabled by backend policy.",
                assets,
                CanTrade: DemoOrderExecutionEnabled);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "OANDA account instrument discovery failed.");
            return new WorkspaceBroker(
                "oanda",
                "OANDA",
                environment,
                WorkspaceDataKind.Market,
                IsConfigured: false,
                IsReadOnly: true,
                "OANDA credentials are configured, but the account connection or instrument discovery failed.",
                []);
        }
    }

    public async Task<OandaLiveAnalysisSession> GetSessionAsync(
        string symbol,
        string intervalName,
        CancellationToken cancellationToken)
    {
        OandaBrokerClient client = RequireClient();
        WorkspaceAsset asset = await ResolveAssetAsync(symbol, cancellationToken).ConfigureAwait(false);
        BarInterval interval = WorkspaceAnalysis.ParseInterval(intervalName);
        string key = $"{asset.Symbol}:{intervalName}";
        lock (_sync)
        {
            if (_sessions.TryGetValue(key, out SessionEntry? current))
            {
                current.LastAccess = _timeProvider.GetUtcNow();
                return current.Session;
            }

            if (_sessions.Count >= MaximumSessions)
            {
                KeyValuePair<string, SessionEntry> oldest = _sessions.MinBy(item => item.Value.LastAccess);
                _sessions.Remove(oldest.Key);
                oldest.Value.Session.Stop();
                _retiredSessions.Add(oldest.Value.Session);
                _ = oldest.Value.Session.Completion.ContinueWith(
                    _ => RemoveRetiredSession(oldest.Value.Session),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            var session = new OandaLiveAnalysisSession(
                client,
                asset,
                intervalName,
                interval,
                _options,
                _timeProvider,
                _loggerFactory.CreateLogger<OandaLiveAnalysisSession>());
            _sessions.Add(key, new SessionEntry(session, _timeProvider.GetUtcNow()));
            session.Start();
            return session;
        }
    }

    /// <summary>Largest window a single request may ask for, before warm-up is added on top.</summary>
    public const int MaximumWindowCandles = 2_000;

    /// <summary>
    /// OANDA's per-request candle ceiling (see OandaClients' <c>query.Validate(5000, "OANDA")</c>).
    /// The warm-up span plus the window must fit inside one request.
    /// </summary>
    private const int OandaMaximumCandlesPerRequest = 5_000;

    /// <summary>
    /// Analysed candles centred on <paramref name="anchorAt"/>, for drilling into another timeframe
    /// without losing your place.
    ///
    /// The analyser is fed <c>WarmupCandles</c> extra candles before the first visible one and those
    /// frames are then dropped, so every frame returned has settled ATR/RSI/Bollinger values and
    /// confirmed swings. Analysing only the visible span would annotate its earliest candles from
    /// half-formed indicator state, which is worse than not annotating them at all.
    ///
    /// The requested span is a maximum, not a guarantee: near the start of available history or the
    /// live edge, fewer candles exist on one side. The window is clamped and
    /// <see cref="WorkspaceWindowSnapshot.AnchorIndex"/> reports where the anchor actually landed,
    /// rather than padding with synthetic candles.
    /// </summary>
    public async Task<WorkspaceWindowSnapshot> GetWindowAsync(
        string symbol,
        string intervalName,
        DateTimeOffset anchorAt,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        if (before < 0 || after < 0)
            throw new ArgumentOutOfRangeException(nameof(before), "Window sizes cannot be negative.");
        if (before + after + 1 > MaximumWindowCandles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(before),
                $"A window of {before + after + 1} candles exceeds the {MaximumWindowCandles} maximum.");
        }

        OandaBrokerClient client = RequireClient();
        WorkspaceAsset asset = await ResolveAssetAsync(symbol, cancellationToken).ConfigureAwait(false);
        BarInterval interval = WorkspaceAnalysis.ParseInterval(intervalName);
        var instrument = new InstrumentKey(asset.Instrument);
        int intervalSeconds = WorkspaceAnalysis.IntervalSeconds(interval);
        int warmup = _options.WarmupCandles;

        // Ask by time range, not count, because OANDA returns candles ordered from `From`: a
        // count-only query would hand back the oldest N of the range instead of the span we need.
        // Over-reach the calendar span deliberately - FX has no weekend candles, so N candles span
        // materially more than N intervals of wall-clock time. Without the padding the "before" leg
        // silently comes up short across every weekend.
        long beforeSpanSeconds = (long)(warmup + before + 1) * intervalSeconds;
        long afterSpanSeconds = (long)(after + 1) * intervalSeconds;
        DateTimeOffset from = anchorAt.AddSeconds(-beforeSpanSeconds * WeekendPaddingNumerator / WeekendPaddingDenominator);
        DateTimeOffset to = anchorAt.AddSeconds(afterSpanSeconds * WeekendPaddingNumerator / WeekendPaddingDenominator);

        // OANDA rejects a range whose end is in the future. Anchoring near the live edge easily
        // produces one, because the padded "after" span reaches days past the anchor - so clamp.
        // The window simply comes back short on the after side, which the caller already handles.
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (to > now) to = now;
        if (from >= to)
        {
            throw new ArgumentException(
                $"An anchor of {anchorAt:O} leaves no candles to analyse before {now:O}.",
                nameof(anchorAt));
        }

        int limit = Math.Min(OandaMaximumCandlesPerRequest, warmup + before + 1 + after + WindowRequestSlack);
        IReadOnlyList<Candle> candles = await client.MarketData.GetCandlesAsync(
            new CandleQuery(instrument, interval, Limit: limit, From: from, To: to),
            cancellationToken).ConfigureAwait(false);

        // Retain every analysed frame: the anchor sits mid-span, so a capacity that keeps only the
        // newest frames would discard the "before" half that the drill-in exists to show.
        int capacity = Math.Max(1, candles.Count);
        (IReadOnlyList<ReplayFrame> allFrames, long gaps) = await WorkspaceAnalysis.AnalyzeAsync(
            instrument,
            interval,
            candles,
            capacity,
            cancellationToken).ConfigureAwait(false);

        int anchorIndex = WorkspaceAnalysis.FindAnchorFrameIndex(allFrames, anchorAt);
        if (anchorIndex < 0)
        {
            throw new KeyNotFoundException(
                $"No {intervalName} candle exists at or before {anchorAt:O} for {asset.Symbol}.");
        }

        (int start, int end) = WorkspaceAnalysis.WindowBounds(anchorIndex, before, after, allFrames.Count);
        ReplayFrame[] window = allFrames.Skip(start).Take(end - start + 1).ToArray();

        // Warm-up is satisfied only when real analysed candles preceded the window, not merely when
        // the window itself is full.
        bool warmupSatisfied = start >= warmup;

        var dataset = new ReplayDataset(
            1,
            $"{asset.DisplayName} OANDA {intervalName} window",
            asset.Instrument,
            _timeProvider.GetUtcNow(),
            $"OANDA REST candles centred on {anchorAt:O}, analysed with {warmup} warm-up candles",
            WorkspaceAnalysis.CreateOptions(),
            [new ReplaySeries(intervalName, intervalSeconds, window)]);

        var status = new LiveFeedStatus
        {
            State = LiveConnectionState.Connected,
            Symbol = asset.Symbol,
            Interval = intervalName,
            Message = warmupSatisfied
                ? $"Window of {window.Length} analysed candles."
                : $"Window of {window.Length} analysed candles; only {start} warm-up candles were available.",
            LastClosedCandleAt = window.Length > 0 ? window[^1].AvailableAt : null,
            GapsDetected = gaps
        };

        return new WorkspaceWindowSnapshot(
            status,
            dataset,
            intervalName,
            anchorAt,
            window.Length > 0 ? window[anchorIndex - start].AvailableAt : anchorAt,
            anchorIndex - start,
            before,
            after,
            warmup,
            warmupSatisfied);
    }

    /// <summary>Extra candles requested beyond the exact need, absorbing partial/pending candles.</summary>
    private const int WindowRequestSlack = 16;

    /// <summary>
    /// Calendar padding for the fetch range. FX trades ~5 of 7 days, so N candles span roughly 7/5
    /// of N intervals in wall-clock time; 2/1 leaves margin for holidays on top.
    /// </summary>
    private const int WeekendPaddingNumerator = 2;
    private const int WeekendPaddingDenominator = 1;

    public async Task<OandaWorkspaceAccount> GetAccountAsync(CancellationToken cancellationToken)
    {
        OandaBrokerClient client = RequireClient();
        Task<IReadOnlyList<AccountSnapshot>> accountsTask = client.Accounts.GetAccountsAsync(cancellationToken);
        Task<IReadOnlyList<BrokerPosition>> positionsTask = client.Positions.GetOpenPositionsAsync(cancellationToken);
        Task<IReadOnlyList<BrokerOrder>> ordersTask = client.Orders.GetOpenOrdersAsync(
            cancellationToken: cancellationToken);
        await Task.WhenAll(accountsTask, positionsTask, ordersTask).ConfigureAwait(false);
        AccountSnapshot account = (await accountsTask.ConfigureAwait(false)).Single();
        return new OandaWorkspaceAccount(
            MaskAccountId(account.AccountId),
            account.Currency,
            account.Balance,
            account.Available,
            account.MarginUsed,
            account.UnrealizedProfitLoss,
            account.CanTrade,
            await positionsTask.ConfigureAwait(false),
            await ordersTask.ConfigureAwait(false),
            DemoOrderExecutionEnabled);
    }

    public async Task<OrderSubmission> PlaceOrderAsync(
        OandaWorkspaceOrderRequest order,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        EnsureDemoOrdersEnabled();
        WorkspaceAsset asset = await ResolveAssetAsync(order.Symbol, cancellationToken).ConfigureAwait(false);
        if (order.Units <= 0m || order.Units > 100_000_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(order.Units));
        }

        IReadOnlyList<AccountSnapshot> accounts = await RequireClient().Accounts
            .GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (accounts.Count != 1 || accounts[0].CanTrade == false)
        {
            throw new InvalidOperationException(
                "The connected OANDA practice account reports that trading is disabled.");
        }

        var request = new PlaceOrderRequest
        {
            Instrument = new InstrumentKey(asset.Instrument),
            Side = ParseSide(order.Side),
            Type = ParseOrderType(order.Type),
            Quantity = new OrderQuantity(order.Units, QuantityUnit.Units),
            LimitPrice = string.Equals(order.Type, "Limit", StringComparison.OrdinalIgnoreCase)
                ? order.Price
                : null,
            StopPrice = string.Equals(order.Type, "Stop", StringComparison.OrdinalIgnoreCase)
                ? order.Price
                : null,
            ClientOrderId = order.ClientOrderId,
            StopLoss = order.StopLoss is null ? null : new StopLossInstruction(order.StopLoss.Value),
            TakeProfit = order.TakeProfit is null ? null : new TakeProfitInstruction(order.TakeProfit.Value)
        };
        return await RequireClient().Orders.PlaceOrderAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        EnsureDemoOrdersEnabled();
        return RequireClient().Orders.CancelOrderAsync(brokerOrderId, cancellationToken);
    }

    public OandaOrderEventBatch GetOrderEvents(long afterRevision)
    {
        lock (_sync)
        {
            IReadOnlyList<OrderEvent> events = afterRevision < _orderRevision
                ? _orderEvents
                    .Where(item => item.Revision > afterRevision)
                    .Select(item => item.Event)
                    .ToArray()
                : [];
            return new OandaOrderEventBatch(_orderRevision, events);
        }
    }

    public Task<long> WaitForOrderChangeAsync(long revision, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return _orderRevision != revision
                ? Task.FromResult(_orderRevision)
                : _ordersChanged.Task.WaitAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_client is null)
        {
            _logger.LogInformation(
                "OANDA is disabled; no OANDA requests will be sent. Set Oanda__Enabled=true, " +
                "an enabled OANDA credential in the broker credential database.");
            return;
        }

        _logger.LogInformation(
            "OANDA {Environment} workspace is configured. Demo order execution is {OrderPolicy}.",
            _options.Environment,
            DemoOrderExecutionEnabled ? "enabled" : "disabled");

        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (OrderEvent orderEvent in _client.Orders
                    .StreamOrderEventsAsync(stoppingToken))
                {
                    PublishOrderEvent(orderEvent);
                    attempt = 0;
                }
                throw new IOException("The OANDA transaction stream closed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                attempt++;
                TimeSpan delay = ReconnectDelay.Calculate(
                    attempt,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(30),
                    Random.Shared.NextDouble());
                _logger.LogWarning(exception, "OANDA transaction stream disconnected; retrying in {Delay}.", delay);
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        OandaLiveAnalysisSession[] sessions;
        lock (_sync)
        {
            sessions = _sessions.Values
                .Select(entry => entry.Session)
                .Concat(_retiredSessions)
                .Distinct()
                .ToArray();
            _sessions.Clear();
            _retiredSessions.Clear();
        }
        foreach (OandaLiveAnalysisSession session in sessions)
        {
            session.Stop();
        }
        await Task.WhenAll(sessions.Select(session => session.Completion)).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_client is not null && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        WorkspaceAsset asset,
        BarInterval interval,
        int limit,
        CancellationToken cancellationToken) =>
        await RequireClient().MarketData.GetCandlesAsync(
            new CandleQuery(new InstrumentKey(asset.Instrument), interval, Limit: limit),
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<WorkspaceAsset>> GetAssetsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_assets is not null && _assetsExpireAt > now)
        {
            return _assets;
        }

        await _assetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_assets is not null && _assetsExpireAt > now)
            {
                return _assets;
            }

            IReadOnlyList<OandaInstrumentInfo> instruments = await RequireClient()
                .GetInstrumentsAsync(cancellationToken).ConfigureAwait(false);
            _assets = instruments.Select(ToWorkspaceAsset).ToArray();
            _assetsExpireAt = now + AssetLifetime;
            return _assets;
        }
        finally
        {
            _assetGate.Release();
        }
    }

    private async Task<WorkspaceAsset> ResolveAssetAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        IReadOnlyList<WorkspaceAsset> assets = await GetAssetsAsync(cancellationToken)
            .ConfigureAwait(false);
        return assets.FirstOrDefault(asset =>
                string.Equals(asset.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"OANDA instrument '{symbol}' is not available to this account.");
    }

    private void PublishOrderEvent(OrderEvent orderEvent)
    {
        TaskCompletionSource<long> changed;
        long revision;
        lock (_sync)
        {
            revision = ++_orderRevision;
            _orderEvents.Add(new RevisionedOrderEvent(revision, orderEvent));
            if (_orderEvents.Count > 100)
            {
                _orderEvents.RemoveAt(0);
            }
            changed = _ordersChanged;
            _ordersChanged = NewChangeSignal();
        }
        changed.TrySetResult(revision);
    }

    private void RemoveRetiredSession(OandaLiveAnalysisSession session)
    {
        lock (_sync)
        {
            _retiredSessions.Remove(session);
        }
    }

    private OandaBrokerClient RequireClient() => _client
        ?? throw new InvalidOperationException("OANDA is not configured on the dashboard backend.");

    private void EnsureDemoOrdersEnabled()
    {
        if (!DemoOrderExecutionEnabled)
        {
            throw new InvalidOperationException(
                "OANDA demo order execution is disabled. Enable Oanda:AllowDemoOrders on the backend.");
        }
    }

    private static WorkspaceAsset ToWorkspaceAsset(OandaInstrumentInfo instrument)
    {
        string prefix = instrument.Type.ToUpperInvariant() switch
        {
            "CURRENCY" => "FX",
            "METAL" => "METAL",
            "CFD" => "CFD",
            _ => "OANDA"
        };
        string pair = instrument.Name.Replace('_', '/');
        return new WorkspaceAsset(
            instrument.Name,
            instrument.DisplayName,
            $"{prefix}:{pair}",
            WorkspaceAnalysis.OandaTimeframes);
    }

    private static OrderSide ParseSide(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.ToUpperInvariant() switch
        {
            "BUY" => OrderSide.Buy,
            "SELL" => OrderSide.Sell,
            _ => throw new ArgumentException("OANDA order side must be Buy or Sell.", nameof(value))
        };
    }

    private static StandardOrderType ParseOrderType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.ToUpperInvariant() switch
        {
            "MARKET" => StandardOrderType.Market,
            "LIMIT" => StandardOrderType.Limit,
            "STOP" => StandardOrderType.Stop,
            _ => throw new ArgumentException("OANDA order type must be Market, Limit, or Stop.", nameof(value))
        };
    }

    private static string MaskAccountId(string value) => value.Length <= 4
        ? value
        : $"••••{value[^4..]}";

    private static TaskCompletionSource<long> NewChangeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record SessionEntry(OandaLiveAnalysisSession Session, DateTimeOffset Created)
    {
        public DateTimeOffset LastAccess { get; set; } = Created;
    }

    private sealed record RevisionedOrderEvent(long Revision, OrderEvent Event);
}
