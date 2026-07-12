using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Networking.Abstractions;

namespace Networking.Duplex;

/// <summary>
/// Adds request correlation and stream routing to one long-lived duplex session.
/// It is suitable for WebSocket transports and for FIX sessions wrapped by IDuplexSession.
/// </summary>
public sealed class CorrelatedDuplexTransport :
    IRequestTransport,
    IStreamingTransport,
    IAsyncDisposable
{
    private readonly IDuplexSession _session;
    private readonly IInboundMessageClassifier _classifier;
    private readonly bool _ownsSession;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly ConcurrentDictionary<
        string,
        TaskCompletionSource<ReadOnlyMemory<byte>>> _pendingRequests =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, IStreamSink> _streamSinks =
        new(StringComparer.Ordinal);

    private Task? _dispatchTask;
    private Exception? _terminalException;
    private int _started;
    private int _disposed;

    public CorrelatedDuplexTransport(
        TransportId id,
        TransportKind kind,
        IDuplexSession session,
        IInboundMessageClassifier classifier,
        bool ownsSession = false)
    {
        if (kind == TransportKind.Http)
        {
            throw new ArgumentException(
                "CorrelatedDuplexTransport can only represent WebSocket or FIX transports.",
                nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(classifier);

        Id = id;
        Kind = kind;
        _session = session;
        _classifier = classifier;
        _ownsSession = ownsSession;
    }

    public TransportId Id { get; }

    public TransportKind Kind { get; }

    /// <summary>
    /// Receives messages that contain neither a known correlation ID nor a registered stream key.
    /// Exceptions thrown by handlers are ignored so they cannot terminate the receive loop.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? UnhandledMessage;

    public async Task<TResponse> SendAsync<TResponse>(
        INetworkCommand<TResponse> command,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(command);

        if (command.TransportId != Id)
        {
            throw new InvalidOperationException(
                $"Command route '{command.TransportId}' does not match duplex transport '{Id}'.");
        }

        if (command is not IDuplexCommand<TResponse> duplexCommand)
        {
            throw new ArgumentException(
                $"Commands sent through '{Id}' must implement " +
                $"{typeof(IDuplexCommand<TResponse>).Name}.",
                nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(duplexCommand.CorrelationId);
        ValidateTimeout(duplexCommand.Timeout);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfTerminal();

        var completion = new TaskCompletionSource<ReadOnlyMemory<byte>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(duplexCommand.CorrelationId, completion))
        {
            throw new InvalidOperationException(
                $"Correlation ID '{duplexCommand.CorrelationId}' is already pending on '{Id}'.");
        }

        try
        {
            ReadOnlyMemory<byte> outbound = duplexCommand.Encode();

            if (outbound.IsEmpty)
            {
                throw new InvalidOperationException("The duplex command encoded an empty message.");
            }

            await _session.SendAsync(outbound, cancellationToken).ConfigureAwait(false);

            ReadOnlyMemory<byte> response = duplexCommand.Timeout == Timeout.InfiniteTimeSpan
                ? await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await completion.Task
                    .WaitAsync(duplexCommand.Timeout, cancellationToken)
                    .ConfigureAwait(false);

            return duplexCommand.DecodeResponse(response);
        }
        finally
        {
            _pendingRequests.TryRemove(duplexCommand.CorrelationId, out _);
        }
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        INetworkSubscription<TEvent> subscription,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.TransportId != Id)
        {
            throw new InvalidOperationException(
                $"Subscription route '{subscription.TransportId}' does not match " +
                $"duplex transport '{Id}'.");
        }

        if (subscription is not IDuplexSubscription<TEvent> duplexSubscription)
        {
            throw new ArgumentException(
                $"Subscriptions sent through '{Id}' must implement " +
                $"{typeof(IDuplexSubscription<TEvent>).Name}.",
                nameof(subscription));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(duplexSubscription.StreamKey);

        if (duplexSubscription.BufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duplexSubscription.BufferCapacity),
                "The stream buffer capacity must be greater than zero.");
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfTerminal();

        var sink = new StreamSink<TEvent>(duplexSubscription);

        if (!_streamSinks.TryAdd(duplexSubscription.StreamKey, sink))
        {
            throw new InvalidOperationException(
                $"Stream key '{duplexSubscription.StreamKey}' already has an active subscriber " +
                $"on transport '{Id}'.");
        }

        try
        {
            ReadOnlyMemory<byte> subscribeMessage = duplexSubscription.EncodeSubscribe();

            if (!subscribeMessage.IsEmpty)
            {
                await _session.SendAsync(subscribeMessage, cancellationToken).ConfigureAwait(false);
            }

            await foreach (TEvent item in sink.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            sink.BeginClose();

            try
            {
                ReadOnlyMemory<byte>? unsubscribeMessage = duplexSubscription.EncodeUnsubscribe();

                if (unsubscribeMessage is { } message &&
                    !message.IsEmpty &&
                    !_lifetimeCts.IsCancellationRequested)
                {
                    using var unsubscribeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                    try
                    {
                        await _session.SendAsync(message, unsubscribeCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception) when (unsubscribeCts.IsCancellationRequested)
                    {
                        // Best-effort unsubscribe during iterator cleanup.
                    }
                    catch (ObjectDisposedException)
                    {
                        // The session was already stopped.
                    }
                    catch (ChannelClosedException)
                    {
                        // The session was already stopped.
                    }
                }
            }
            finally
            {
                RemoveStream(duplexSubscription.StreamKey, sink);
                sink.Complete();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _lifetimeCts.Cancel();

        await _startGate.WaitAsync().ConfigureAwait(false);
        _startGate.Release();

        var disposedException = new ObjectDisposedException(nameof(CorrelatedDuplexTransport));
        FailPendingRequests(disposedException);
        CompleteStreams(disposedException);

        if (_ownsSession)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        if (_dispatchTask is not null)
        {
            try
            {
                await _dispatchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
        }

        _startGate.Dispose();
        _lifetimeCts.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _started) == 1)
        {
            return;
        }

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        await _startGate.WaitAsync(startCts.Token).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _started) == 1)
            {
                return;
            }

            ThrowIfDisposed();
            await _session.StartAsync(startCts.Token).ConfigureAwait(false);
            _dispatchTask = DispatchLoopAsync(_lifetimeCts.Token);
            Volatile.Write(ref _started, 1);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        Exception? terminalError = null;

        try
        {
            await foreach (ReadOnlyMemory<byte> message in
                           _session.ReadAllAsync(cancellationToken))
            {
                bool handled = false;

                if (_classifier.TryGetCorrelationId(message, out string correlationId) &&
                    _pendingRequests.TryRemove(correlationId, out var completion))
                {
                    completion.TrySetResult(message);
                    handled = true;
                }

                if (!handled &&
                    _classifier.TryGetStreamKey(message, out string streamKey) &&
                    _streamSinks.TryGetValue(streamKey, out IStreamSink? sink))
                {
                    try
                    {
                        if (!sink.TryPublish(message, out Exception? publishError))
                        {
                            throw publishError ?? new InvalidOperationException(
                                $"Stream '{streamKey}' overflowed its configured buffer.");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        if (RemoveStream(streamKey, sink))
                        {
                            sink.Complete(exception);
                        }
                    }

                    handled = true;
                }

                if (!handled)
                {
                    NotifyUnhandledMessage(message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            terminalError ??= cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new IOException($"The duplex session for transport '{Id}' ended.");

            Volatile.Write(ref _terminalException, terminalError);
            FailPendingRequests(terminalError);
            CompleteStreams(terminalError);
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach ((string key, TaskCompletionSource<ReadOnlyMemory<byte>> completion) in
                 _pendingRequests)
        {
            if (_pendingRequests.TryRemove(key, out var removed))
            {
                removed.TrySetException(exception);
            }
        }
    }

    private void CompleteStreams(Exception exception)
    {
        foreach ((string key, IStreamSink sink) in _streamSinks)
        {
            if (_streamSinks.TryRemove(key, out IStreamSink? removed))
            {
                removed.Complete(exception);
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }

    private interface IStreamSink
    {
        bool TryPublish(ReadOnlyMemory<byte> message, out Exception? exception);

        void BeginClose();

        void Complete(Exception? exception = null);
    }

    private sealed class StreamSink<TEvent> : IStreamSink
    {
        private readonly IDuplexSubscription<TEvent> _subscription;
        private readonly Channel<TEvent> _channel;
        private int _closing;

        public StreamSink(IDuplexSubscription<TEvent> subscription)
        {
            _subscription = subscription;
            _channel = Channel.CreateBounded<TEvent>(
                new BoundedChannelOptions(subscription.BufferCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = subscription.FullMode,
                    AllowSynchronousContinuations = false
                });
        }

        public bool TryPublish(ReadOnlyMemory<byte> message, out Exception? exception)
        {
            exception = null;
            if (Volatile.Read(ref _closing) == 1)
            {
                return true;
            }

            try
            {
                TEvent item = _subscription.DecodeEvent(message);
                if (_channel.Writer.TryWrite(item))
                {
                    return true;
                }

                exception = new InvalidOperationException(
                    $"Stream '{_subscription.StreamKey}' overflowed its configured buffer.");
                return false;
            }
            catch (Exception caught)
            {
                exception = caught;
                return false;
            }
        }

        public void BeginClose() => Volatile.Write(ref _closing, 1);

        public IAsyncEnumerable<TEvent> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Complete(Exception? exception = null) =>
            _channel.Writer.TryComplete(exception);
    }

    private bool RemoveStream(string key, IStreamSink expected) =>
        ((ICollection<KeyValuePair<string, IStreamSink>>)_streamSinks)
        .Remove(new KeyValuePair<string, IStreamSink>(key, expected));

    private void ThrowIfTerminal()
    {
        if (Volatile.Read(ref _terminalException) is { } exception)
        {
            throw new IOException($"The duplex transport '{Id}' is no longer running.", exception);
        }
    }

    private void NotifyUnhandledMessage(ReadOnlyMemory<byte> message)
    {
        if (UnhandledMessage is not { } handlers)
        {
            return;
        }

        foreach (Action<ReadOnlyMemory<byte>> handler in handlers.GetInvocationList()
                     .Cast<Action<ReadOnlyMemory<byte>>>())
        {
            ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    try
                    {
                        state.Handler(state.Message);
                    }
                    catch
                    {
                        // Observers must not terminate the receive loop.
                    }
                },
                (Handler: handler, Message: message),
                preferLocal: false);
        }
    }
}
