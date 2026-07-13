using System.Runtime.CompilerServices;
using ChartAnnotator.Collections;

namespace Simulator.Broker;

/// <summary>
/// A bounded replayable event stream. Producers never block. A consumer that falls
/// behind the retained ring-buffer window receives an explicit error instead of
/// silently losing order events.
/// </summary>
internal sealed class BoundedAsyncEventLog<T>
{
    private readonly object _sync = new();
    private readonly RingBuffer<Entry> _entries;
    private TaskCompletionSource _changed = NewSignal();
    private long _nextSequence;
    private bool _completed;
    private Exception? _completionError;

    public BoundedAsyncEventLog(int capacity)
    {
        _entries = new RingBuffer<Entry>(capacity);
    }

    public void Append(T item)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The event log has completed.");
            }

            _entries.Add(new Entry(++_nextSequence, item));
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    public void Complete(Exception? error = null)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _completionError = error;
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
    }

    public async IAsyncEnumerable<T> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long cursor;
        lock (_sync)
        {
            cursor = _entries.Count == 0
                ? _nextSequence + 1
                : _entries.Oldest.Sequence;
        }

        while (true)
        {
            Entry[] available;
            Task waitTask;
            bool completed;
            Exception? completionError;

            lock (_sync)
            {
                long oldestRetained = _entries.Count == 0
                    ? _nextSequence + 1
                    : _entries.Oldest.Sequence;
                if (cursor < oldestRetained)
                {
                    throw new InvalidOperationException(
                        $"The consumer fell behind the bounded event log. " +
                        $"Requested sequence {cursor}, oldest retained sequence {oldestRetained}.");
                }

                available = _entries
                    .Where(entry => entry.Sequence >= cursor)
                    .ToArray();
                completed = _completed;
                completionError = _completionError;
                waitTask = _changed.Task;
            }

            if (available.Length > 0)
            {
                foreach (Entry entry in available)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cursor = entry.Sequence + 1;
                    yield return entry.Item;
                }

                continue;
            }

            if (completed)
            {
                if (completionError is not null)
                {
                    throw completionError;
                }

                yield break;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Entry(long Sequence, T Item);
}
