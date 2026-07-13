using System.Diagnostics;
using System.Threading.Channels;
using Simulator.Models;

namespace Simulator.Engine;

public interface IStrategyWorkerHost : IAsyncDisposable
{
    string StrategyId { get; }
    string StrategyName { get; }
    StrategySimulationSession Session { get; }
    Task Completion { get; }
    int WorkerId { get; }

    ValueTask<Task<StrategyFrameResult>> EnqueueAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default);

    StrategyWorkerMetrics SnapshotMetrics();
}

public sealed record StrategyFrameEnvelope
{
    public required MarketFrame Frame { get; init; }
    public required TaskCompletionSource<StrategyFrameResult> Completion { get; init; }
}

/// <summary>
/// Persistent per-strategy worker. One task or dedicated thread for the entire simulation —
/// never one thread per market frame.
/// </summary>
public sealed class StrategyWorkerHost : IStrategyWorkerHost
{
    private readonly Channel<StrategyFrameEnvelope> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Stopwatch _idle = new();
    private readonly Stopwatch _processing = new();
    private long _processedFrames;
    private long _totalProcessingTicks;
    private long _maxProcessingTicks;
    private long _producerWaitTicks;
    private int _currentOccupancy;
    private int _peakOccupancy;
    private long _lastSequence;
    private int _failures;

    public StrategyWorkerHost(
        StrategySimulationSession session,
        int channelCapacity,
        StrategyWorkerMode mode)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (channelCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(channelCapacity));

        StrategyId = session.StrategyId;
        StrategyName = session.StrategyName;
        WorkerId = Environment.CurrentManagedThreadId;

        _channel = Channel.CreateBounded<StrategyFrameEnvelope>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        if (mode == StrategyWorkerMode.DedicatedThread)
        {
            _loop = Task.Factory.StartNew(
                () => RunLoopAsync(_cts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
        else
        {
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public string StrategyId { get; }
    public string StrategyName { get; }
    public StrategySimulationSession Session { get; }
    public Task Completion => _loop;
    public int WorkerId { get; private set; }

    public async ValueTask<Task<StrategyFrameResult>> EnqueueAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<StrategyFrameResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var envelope = new StrategyFrameEnvelope { Frame = frame, Completion = tcs };

        var wait = Stopwatch.StartNew();
        await _channel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        wait.Stop();
        Interlocked.Add(ref _producerWaitTicks, wait.Elapsed.Ticks);
        int occupancy = Interlocked.Increment(ref _currentOccupancy);
        int peak = Volatile.Read(ref _peakOccupancy);
        while (occupancy > peak &&
               Interlocked.CompareExchange(ref _peakOccupancy, occupancy, peak) != peak)
        {
            peak = Volatile.Read(ref _peakOccupancy);
        }

        return tcs.Task;
    }

    public void Complete() => _channel.Writer.TryComplete();

    public StrategyWorkerMetrics SnapshotMetrics()
    {
        long frames = Interlocked.Read(ref _processedFrames);
        TimeSpan total = TimeSpan.FromTicks(Interlocked.Read(ref _totalProcessingTicks));
        TimeSpan max = TimeSpan.FromTicks(Interlocked.Read(ref _maxProcessingTicks));
        TimeSpan average = frames == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(total.Ticks / frames);
        return new StrategyWorkerMetrics
        {
            StrategyName = StrategyName,
            ProcessedFrames = frames,
            TotalProcessingTime = total,
            MaximumFrameProcessingTime = max,
            AverageFrameProcessingTime = average,
            BarrierWaitTime = TimeSpan.FromTicks(Interlocked.Read(ref _producerWaitTicks)),
            PeakChannelOccupancy = Volatile.Read(ref _peakOccupancy)
        };
    }

    public async ValueTask DisposeAsync()
    {
        Complete();
        try
        {
            // Prefer graceful drain before cancelling.
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            _cts.Cancel();
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // Worker observed cancellation/completion.
            }
        }

        _cts.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        WorkerId = Environment.CurrentManagedThreadId;
        _idle.Start();
        try
        {
            await foreach (StrategyFrameEnvelope envelope in _channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _currentOccupancy);
                _idle.Stop();
                if (envelope.Frame.Sequence <= _lastSequence)
                {
                    Interlocked.Increment(ref _failures);
                    envelope.Completion.TrySetException(new InvalidOperationException(
                        $"Strategy '{StrategyName}' received out-of-order frame {envelope.Frame.Sequence} after {_lastSequence}."));
                    continue;
                }

                _lastSequence = envelope.Frame.Sequence;
                _processing.Restart();
                try
                {
                    StrategyFrameResult result = await Session
                        .ProcessFrameAsync(envelope.Frame, cancellationToken)
                        .ConfigureAwait(false);
                    _processing.Stop();
                    RecordTiming(_processing.Elapsed);
                    envelope.Completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    _processing.Stop();
                    RecordTiming(_processing.Elapsed);
                    Interlocked.Increment(ref _failures);
                    Session.MarkFailed(envelope.Frame.Sequence, exception.ToString());
                    envelope.Completion.TrySetException(exception);
                }

                _idle.Restart();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _idle.Stop();
        }
    }

    private void RecordTiming(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _processedFrames);
        Interlocked.Add(ref _totalProcessingTicks, elapsed.Ticks);
        long currentMax = Interlocked.Read(ref _maxProcessingTicks);
        while (elapsed.Ticks > currentMax)
        {
            long original = Interlocked.CompareExchange(ref _maxProcessingTicks, elapsed.Ticks, currentMax);
            if (original == currentMax)
                break;
            currentMax = original;
        }
    }
}

