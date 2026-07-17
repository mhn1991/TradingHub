using System.Diagnostics;
using System.Threading.Channels;
using Simulator.Models;
using TradingCore.Pipeline;

namespace Simulator.Engine;

public interface IStrategyWorkerHost : IAsyncDisposable
{
    string StrategyId { get; }
    string StrategyName { get; }
    StrategySimulationSession Session { get; }
    Task Completion { get; }
    int WorkerId { get; }
    AgentInstanceKey Key { get; }

    ValueTask<Task<StrategyFrameResult>> EnqueueAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default);

    ValueTask<Task<StrategyFrameResult[]>> EnqueueBatchAsync(
        IReadOnlyList<MarketFrame> frames,
        CancellationToken cancellationToken = default);

    StrategyWorkerMetrics SnapshotMetrics();
}

public sealed record StrategyFrameEnvelope
{
    public required IReadOnlyList<MarketFrame> Frames { get; init; }
    public required TaskCompletionSource<StrategyFrameResult[]> Completion { get; init; }
}

/// <summary>
/// Persistent per-strategy task worker for the entire simulation.
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
        AgentInstanceKey key)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (channelCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(channelCapacity));

        StrategyId = session.StrategyId;
        StrategyName = session.StrategyName;
        Key = key ?? throw new ArgumentNullException(nameof(key));
        WorkerId = Key.GetHashCode();

        _channel = Channel.CreateBounded<StrategyFrameEnvelope>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public string StrategyId { get; }
    public string StrategyName { get; }
    public StrategySimulationSession Session { get; }
    public Task Completion => _loop;
    public int WorkerId { get; private set; }
    public AgentInstanceKey Key { get; }

    public async ValueTask<Task<StrategyFrameResult>> EnqueueAsync(
        MarketFrame frame,
        CancellationToken cancellationToken = default)
    {
        Task<StrategyFrameResult[]> batch = await EnqueueBatchAsync([frame], cancellationToken)
            .ConfigureAwait(false);
        return FirstAsync(batch);
    }

    public async ValueTask<Task<StrategyFrameResult[]>> EnqueueBatchAsync(
        IReadOnlyList<MarketFrame> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        var tcs = new TaskCompletionSource<StrategyFrameResult[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var envelope = new StrategyFrameEnvelope { Frames = frames, Completion = tcs };

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

    private static async Task<StrategyFrameResult> FirstAsync(Task<StrategyFrameResult[]> batch)
    {
        StrategyFrameResult[] results = await batch.ConfigureAwait(false);
        return results[0];
    }

    public void Complete() => _channel.Writer.TryComplete();

    public StrategyWorkerMetrics SnapshotMetrics()
    {
        StrategyWorkerMetrics session = Session.BuildMetrics();
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
            PeakChannelOccupancy = Volatile.Read(ref _peakOccupancy),
            ManagementEvaluations = session.ManagementEvaluations,
            StopAmendmentRequests = session.StopAmendmentRequests,
            AcceptedStopAmendments = session.AcceptedStopAmendments,
            RejectedStopAmendments = session.RejectedStopAmendments
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
                var results = new List<StrategyFrameResult>(envelope.Frames.Count);
                Exception? failure = null;
                foreach (MarketFrame frame in envelope.Frames)
                {
                    if (frame.Sequence <= _lastSequence)
                    {
                        failure = new InvalidOperationException(
                            $"Strategy '{StrategyName}' received out-of-order frame {frame.Sequence} after {_lastSequence}.");
                        break;
                    }

                    _lastSequence = frame.Sequence;
                    _processing.Restart();
                    try
                    {
                        results.Add(await Session
                            .ProcessFrameAsync(frame, cancellationToken)
                            .ConfigureAwait(false));
                        _processing.Stop();
                        RecordTiming(_processing.Elapsed);
                    }
                    catch (Exception exception)
                    {
                        _processing.Stop();
                        RecordTiming(_processing.Elapsed);
                        Session.MarkFailed(frame.Sequence, exception.ToString());
                        failure = exception;
                        break;
                    }
                }

                if (failure is null)
                    envelope.Completion.TrySetResult(results.ToArray());
                else
                {
                    Interlocked.Increment(ref _failures);
                    envelope.Completion.TrySetException(failure);
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
