using System.Net;
using Brokers.Models;
using ChartAnnotator.Engine;
using Dashboard.Contracts;
using Microsoft.Extensions.Options;
using Networking.Duplex;

namespace Dashboard.Live;

internal sealed class BinanceLiveAnalysisService : BackgroundService
{
    private const int MaximumRestResponseBytes = 4 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly HttpClient _httpClient;
    private readonly ILiveWebSocketFactory _webSocketFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BinanceLiveAnalysisService> _logger;
    private readonly LiveFeedOptions _options;
    private readonly InstrumentKey _instrument;
    private readonly BarInterval _interval;
    private readonly ChartAnnotationOptions _annotationOptions;
    private readonly LiveAnalysisState _analysis;
    private TaskCompletionSource<long> _changed = NewChangeSignal();
    private LiveFeedStatus _status;
    private long _revision;

    public BinanceLiveAnalysisService(
        HttpClient httpClient,
        ILiveWebSocketFactory webSocketFactory,
        TimeProvider timeProvider,
        IOptions<LiveFeedOptions> options,
        ILogger<BinanceLiveAnalysisService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(webSocketFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _webSocketFactory = webSocketFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
        (_instrument, _interval) = _options.Validate();
        _annotationOptions = WorkspaceAnalysis.CreateOptions();
        _analysis = new LiveAnalysisState(
            _instrument,
            _interval,
            _annotationOptions,
            _options.FrameCapacity);
        _status = new LiveFeedStatus
        {
            State = LiveConnectionState.Starting,
            Symbol = _options.Symbol.ToUpperInvariant(),
            Interval = _options.Interval,
            Message = "The public market-data feed is starting."
        };
    }

    public LiveReplayPayload GetPayload()
    {
        LiveFeedStatus status;
        long revision;
        lock (_sync)
        {
            status = _status;
            revision = _revision;
        }

        IReadOnlyList<ReplayFrame> frames = _analysis.Snapshot();
        DateTimeOffset generatedAt = frames.Count > 0
            ? frames[^1].AvailableAt
            : _timeProvider.GetUtcNow();
        var dataset = new ReplayDataset(
            SchemaVersion: 1,
            Title: $"{_options.Symbol.ToUpperInvariant()} live closed-candle analysis",
            Instrument: _instrument.Value,
            GeneratedAt: generatedAt,
            Source: "Binance public market-data REST warm-up and WebSocket kline stream",
            Parameters: _annotationOptions,
            Series:
            [
                new ReplaySeries(
                    _options.Interval,
                    checked((int)(_interval.AddTo(DateTimeOffset.UnixEpoch) -
                        DateTimeOffset.UnixEpoch).TotalSeconds),
                    frames)
            ]);
        return new LiveReplayPayload(revision, status, dataset);
    }

    public Task<long> WaitForChangeAsync(long observedRevision, CancellationToken cancellationToken)
    {
        Task<long> task;
        lock (_sync)
        {
            if (_revision != observedRevision)
            {
                return Task.FromResult(_revision);
            }

            task = _changed.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int reconnectAttempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SetStatus(
                    LiveConnectionState.WarmingUp,
                    "Connecting and warming indicators from recent closed candles.",
                    reconnectAttempt);
                Uri streamUri = new(
                    _options.WebSocketBaseAddress,
                    $"{_options.Symbol.ToLowerInvariant()}@kline_{_options.Interval}");
                await using IDuplexSession session = _webSocketFactory.Create(streamUri);
                await session.StartAsync(stoppingToken).ConfigureAwait(false);

                // The socket buffers events while REST catches up, closing the hand-off race.
                await CatchUpFromRestAsync(stoppingToken).ConfigureAwait(false);
                reconnectAttempt = 0;
                SetConnected();

                await foreach (ReadOnlyMemory<byte> message in
                               session.ReadAllAsync(stoppingToken))
                {
                    MarkMessageReceived();
                    BinanceStreamMessage parsed = BinanceKlineParser.ParseStream(
                        message.Span,
                        _options.Symbol,
                        _options.Interval,
                        _instrument,
                        _interval);
                    if (parsed.Kind == BinanceStreamMessageKind.ServerShutdown)
                    {
                        throw new IOException("Binance announced a market-data server shutdown.");
                    }

                    if (parsed.Kind != BinanceStreamMessageKind.Kline || parsed.Candle is null)
                    {
                        continue;
                    }

                    ReplayFrame? frame = await _analysis.ProcessAsync(
                        parsed.Candle,
                        stoppingToken).ConfigureAwait(false);
                    if (frame is not null)
                    {
                        PublishClosedCandle(frame.AvailableAt);
                    }
                }

                throw new IOException("The Binance market-data WebSocket closed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                reconnectAttempt++;
                TimeSpan delay = ReconnectDelay.Calculate(
                    reconnectAttempt,
                    TimeSpan.FromSeconds(_options.ReconnectMinimumSeconds),
                    TimeSpan.FromSeconds(_options.ReconnectMaximumSeconds),
                    Random.Shared.NextDouble());
                delay = ReconnectDelay.RespectRetryAfter(
                    delay,
                    (exception as BinanceRateLimitException)?.RetryAfter);
                _logger.LogWarning(
                    exception,
                    "The Binance live feed disconnected. Reconnecting in {Delay}.",
                    delay);
                SetStatus(
                    LiveConnectionState.Reconnecting,
                    $"Feed interrupted; retrying in {delay.TotalSeconds:F1} seconds.",
                    reconnectAttempt);
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

        SetStatus(LiveConnectionState.Stopped, "The live feed has stopped.", 0);
    }

    private async Task CatchUpFromRestAsync(CancellationToken cancellationToken)
    {
        string path =
            $"api/v3/klines?symbol={Uri.EscapeDataString(_options.Symbol.ToUpperInvariant())}" +
            $"&interval={Uri.EscapeDataString(_options.Interval)}" +
            $"&limit={_options.WarmupCandles}";
        using HttpResponseMessage response = await _httpClient.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)418)
        {
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
            {
                retryAfter = retryDate - _timeProvider.GetUtcNow();
            }

            throw new BinanceRateLimitException(response.StatusCode, retryAfter);
        }

        response.EnsureSuccessStatusCode();
        byte[] body = await BoundedHttpContent.ReadAsync(
            response.Content,
            MaximumRestResponseBytes,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Candle> candles = BinanceKlineParser.ParseRest(
            body,
            _instrument,
            _interval,
            _timeProvider.GetUtcNow());
        foreach (Candle candle in candles)
        {
            ReplayFrame? frame = await _analysis.ProcessAsync(candle, cancellationToken)
                .ConfigureAwait(false);
            if (frame is not null)
            {
                PublishClosedCandle(frame.AvailableAt);
            }
        }
    }

    private void SetConnected()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        UpdateStatus(status => status with
        {
            State = LiveConnectionState.Connected,
            Message = "Receiving public Binance klines; indicators use closed candles only.",
            ConnectedAt = now,
            ReconnectAttempt = 0
        });
    }

    private void SetStatus(
        LiveConnectionState state,
        string message,
        int reconnectAttempt)
    {
        UpdateStatus(status => status with
        {
            State = state,
            Message = message,
            ConnectedAt = state == LiveConnectionState.Connected ? status.ConnectedAt : null,
            ReconnectAttempt = reconnectAttempt
        });
    }

    private void MarkMessageReceived()
    {
        lock (_sync)
        {
            _status = _status with { LastMessageAt = _timeProvider.GetUtcNow() };
        }
    }

    private void PublishClosedCandle(DateTimeOffset availableAt)
    {
        UpdateStatus(status => status with
        {
            LastClosedCandleAt = availableAt,
            GapsDetected = _analysis.GapsDetected
        });
    }

    private void UpdateStatus(Func<LiveFeedStatus, LiveFeedStatus> update)
    {
        TaskCompletionSource<long> changed;
        long revision;
        lock (_sync)
        {
            _status = update(_status);
            revision = ++_revision;
            changed = _changed;
            _changed = NewChangeSignal();
        }

        changed.TrySetResult(revision);
    }

    private static TaskCompletionSource<long> NewChangeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
