using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Networking.Duplex;

namespace Networking.WebSockets;

/// <summary>
/// A single long-lived WebSocket connection with exactly one send loop and one receive loop.
/// Outbound and inbound channels are bounded to prevent unlimited memory growth.
/// A session is intentionally one-shot; create a new instance after it terminates.
/// </summary>
public sealed class WebSocketDuplexSession : IDuplexSession
{
    private readonly WebSocketSessionOptions _options;
    private readonly Channel<ReadOnlyMemory<byte>> _outbound;
    private readonly Channel<ReadOnlyMemory<byte>> _inbound;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private ClientWebSocket? _socket;
    private Task _completion = Task.CompletedTask;
    private int _started;
    private int _terminated;
    private int _disposed;

    public WebSocketDuplexSession(WebSocketSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Uri.IsAbsoluteUri)
        {
            throw new ArgumentException("The WebSocket URI must be absolute.", nameof(options));
        }

        if (options.Uri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("The WebSocket URI must use ws:// or wss://.", nameof(options));
        }

        if (options.OutboundQueueCapacity <= 0 ||
            options.InboundQueueCapacity <= 0 ||
            options.ReceiveChunkSize <= 0 ||
            options.MaximumInboundMessageBytes <= 0 ||
            options.MaximumOutboundMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Queue capacities and message sizes must be greater than zero.");
        }

        if (options.CloseTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CloseTimeout must be positive.");
        }

        if (options.KeepAliveInterval < TimeSpan.Zero &&
            options.KeepAliveInterval != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "KeepAliveInterval must be non-negative or Timeout.InfiniteTimeSpan.");
        }

        if (options.OutboundMessageType is not (
                WebSocketMessageType.Text or WebSocketMessageType.Binary))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Outbound messages must be text or binary.");
        }

        _options = options;

        _outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(options.OutboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        _inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(options.InboundQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
    }

    public bool IsStarted => Volatile.Read(ref _started) == 1;

    public Task Completion => _completion;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _terminated) == 1)
        {
            throw new InvalidOperationException(
                "A terminated WebSocket session cannot be restarted; create a new session.");
        }

        if (IsStarted)
        {
            return;
        }

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        await _startGate.WaitAsync(startCts.Token).ConfigureAwait(false);

        try
        {
            if (IsStarted)
            {
                return;
            }

            ThrowIfDisposed();

            var socket = new ClientWebSocket();

            try
            {
                socket.Options.KeepAliveInterval = _options.KeepAliveInterval;
                _options.ConfigureClient?.Invoke(socket.Options);
                await socket.ConnectAsync(_options.Uri, startCts.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            Volatile.Write(ref _started, 1);

            Task sendTask = SendLoopAsync(socket, _lifetimeCts.Token);
            Task receiveTask = ReceiveLoopAsync(socket, _lifetimeCts.Token);
            _completion = MonitorLoopsAsync(socket, sendTask, receiveTask);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!IsStarted)
        {
            throw new InvalidOperationException("The WebSocket session has not been started.");
        }

        if (message.IsEmpty)
        {
            throw new ArgumentException("The outbound WebSocket message cannot be empty.", nameof(message));
        }

        if (message.Length > _options.MaximumOutboundMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"The outbound message exceeds the {_options.MaximumOutboundMessageBytes}-byte limit.");
        }

        // Copy before queuing so the caller may safely reuse its original buffer.
        await _outbound.Writer.WriteAsync(message.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await foreach (ReadOnlyMemory<byte> message in
                       _inbound.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _lifetimeCts.Cancel();
        _outbound.Writer.TryComplete();

        await _startGate.WaitAsync().ConfigureAwait(false);
        _startGate.Release();

        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (_socket?.State is not (WebSocketState.Closed or WebSocketState.None))
            {
                _socket?.Abort();
            }

            _socket?.Dispose();
            _startGate.Dispose();
            _lifetimeCts.Dispose();
        }
    }

    private async Task SendLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        await foreach (ReadOnlyMemory<byte> message in
                       _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(
                    message,
                    _options.OutboundMessageType,
                    endOfMessage: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var writer = new ArrayBufferWriter<byte>(_options.ReceiveChunkSize);
            ValueWebSocketReceiveResult result;

            do
            {
                Memory<byte> target = writer.GetMemory(_options.ReceiveChunkSize);
                result = await socket.ReceiveAsync(target, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (writer.WrittenCount + result.Count > _options.MaximumInboundMessageBytes)
                {
                    throw new InvalidDataException(
                        $"The inbound WebSocket message exceeded the " +
                        $"{_options.MaximumInboundMessageBytes}-byte limit.");
                }

                writer.Advance(result.Count);
            }
            while (!result.EndOfMessage);

            await _inbound.Writer.WriteAsync(
                    writer.WrittenMemory.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task MonitorLoopsAsync(
        ClientWebSocket socket,
        Task sendTask,
        Task receiveTask)
    {
        Exception? terminalError = null;

        try
        {
            Task firstCompleted = await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
            await firstCompleted.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
        }
        finally
        {
            _lifetimeCts.Cancel();

            try
            {
                await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                terminalError ??= exception;
            }

            _outbound.Writer.TryComplete(terminalError);
            _inbound.Writer.TryComplete(terminalError);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(_options.CloseTimeout);
                try
                {
                    await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Session completed",
                            closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (_lifetimeCts.IsCancellationRequested || closeCts.IsCancellationRequested)
                {
                    socket.Abort();
                }
            }

            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _terminated, 1);
        }

        if (terminalError is not null)
        {
            throw terminalError;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
}
