using Brokers.Models;
using Brokers.Oanda;
using ChartAnnotator.Engine;
using Dashboard.Contracts;

namespace Dashboard.Live;

internal sealed class OandaLiveAnalysisSession
{
    private readonly object _sync = new();
    private readonly OandaBrokerClient _client;
    private readonly WorkspaceAsset _asset;
    private readonly string _intervalName;
    private readonly BarInterval _interval;
    private readonly OandaWorkspaceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OandaLiveAnalysisSession> _logger;
    private readonly InstrumentKey _instrument;
    private readonly ChartAnnotationOptions _annotationOptions;
    private readonly LiveAnalysisState _analysis;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource<long> _changed = NewChangeSignal();
    private LiveFeedStatus _status;
    private Task? _completion;
    private long _revision;

    public OandaLiveAnalysisSession(
        OandaBrokerClient client,
        WorkspaceAsset asset,
        string intervalName,
        BarInterval interval,
        OandaWorkspaceOptions options,
        TimeProvider timeProvider,
        ILogger<OandaLiveAnalysisSession> logger)
    {
        _client = client;
        _asset = asset;
        _intervalName = intervalName;
        _interval = interval;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _instrument = new InstrumentKey(asset.Instrument);
        _annotationOptions = WorkspaceAnalysis.CreateOptions();
        _analysis = new LiveAnalysisState(
            _instrument,
            interval,
            _annotationOptions,
            options.FrameCapacity,
            WorkspaceAnalysis.IsExpectedForexClosure);
        _status = new LiveFeedStatus
        {
            State = LiveConnectionState.Starting,
            Symbol = asset.Symbol,
            Interval = intervalName,
            Message = "The OANDA practice pricing stream is starting."
        };
    }

    public Task Completion => _completion ?? Task.CompletedTask;

    public void Start()
    {
        lock (_sync)
        {
            _completion ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    public void Stop() => _stop.Cancel();

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
        return new LiveReplayPayload(
            revision,
            status,
            new ReplayDataset(
                1,
                $"{_asset.DisplayName} OANDA closed-candle analysis",
                _asset.Instrument,
                generatedAt,
                "OANDA account candles warmed by REST and refreshed from the practice pricing stream",
                _annotationOptions,
                [new ReplaySeries(
                    _intervalName,
                    WorkspaceAnalysis.IntervalSeconds(_interval),
                    frames)]));
    }

    public Task<long> WaitForChangeAsync(long observedRevision, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return _revision != observedRevision
                ? Task.FromResult(_revision)
                : _changed.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetStatus(
                    LiveConnectionState.WarmingUp,
                    "Loading OANDA historical candles before opening the practice price stream.",
                    reconnectAttempt);
                await CatchUpAsync(_options.WarmupCandles, cancellationToken).ConfigureAwait(false);
                reconnectAttempt = 0;
                SetConnected();
                DateTimeOffset nextRefresh = NextRefreshTime();

                await foreach (OandaPriceTick tick in _client.StreamPricesAsync(
                    [_asset.Symbol],
                    cancellationToken))
                {
                    MarkMessageReceived(tick.Timestamp);
                    if (tick.Timestamp < nextRefresh)
                    {
                        continue;
                    }

                    DateTimeOffset? previousClose = LastClosedCandleAt();
                    await CatchUpAsync(5, cancellationToken).ConfigureAwait(false);
                    DateTimeOffset? currentClose = LastClosedCandleAt();
                    nextRefresh = currentClose > previousClose
                        ? _interval.AddTo(currentClose!.Value)
                        : tick.Timestamp.AddSeconds(3);
                }

                throw new IOException("The OANDA practice pricing stream closed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                reconnectAttempt++;
                TimeSpan delay = ReconnectDelay.Calculate(
                    reconnectAttempt,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(30),
                    Random.Shared.NextDouble());
                _logger.LogWarning(
                    exception,
                    "OANDA workspace stream {Symbol}/{Interval} disconnected; retrying in {Delay}.",
                    _asset.Symbol,
                    _intervalName,
                    delay);
                SetStatus(
                    LiveConnectionState.Reconnecting,
                    $"OANDA stream interrupted; retrying in {delay.TotalSeconds:F1} seconds.",
                    reconnectAttempt);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        SetStatus(LiveConnectionState.Stopped, "The OANDA workspace stream has stopped.", 0);
    }

    private async Task CatchUpAsync(int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<Candle> candles = await _client.MarketData.GetCandlesAsync(
            new CandleQuery(_instrument, _interval, Limit: limit),
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset? latestClosedCandle = null;
        foreach (Candle candle in candles)
        {
            ReplayFrame? frame = await _analysis.ProcessAsync(candle, cancellationToken)
                .ConfigureAwait(false);
            if (frame is not null)
            {
                latestClosedCandle = frame.AvailableAt;
            }
        }
        if (latestClosedCandle is not null)
        {
            PublishClosedCandle(latestClosedCandle.Value);
        }
    }

    private DateTimeOffset NextRefreshTime()
    {
        DateTimeOffset? lastClose = LastClosedCandleAt();
        return lastClose is null
            ? _timeProvider.GetUtcNow().AddSeconds(3)
            : _interval.AddTo(lastClose.Value);
    }

    private DateTimeOffset? LastClosedCandleAt() => _analysis.LastAvailableAt;

    private void SetConnected()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        UpdateStatus(status => status with
        {
            State = LiveConnectionState.Connected,
            Message = "Receiving OANDA practice prices; indicators use broker-confirmed closed candles.",
            ConnectedAt = now,
            ReconnectAttempt = 0
        });
    }

    private void SetStatus(LiveConnectionState state, string message, int reconnectAttempt)
    {
        UpdateStatus(status => status with
        {
            State = state,
            Message = message,
            ConnectedAt = state == LiveConnectionState.Connected ? status.ConnectedAt : null,
            ReconnectAttempt = reconnectAttempt
        });
    }

    private void MarkMessageReceived(DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            _status = _status with { LastMessageAt = timestamp };
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
