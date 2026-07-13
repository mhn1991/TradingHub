using System.Collections;

namespace ChartAnnotator.Collections;

/// <summary>
/// A fixed-capacity, allocation-free-on-write ring buffer. Logical indexing is
/// always oldest-to-newest even after the physical array wraps.
/// </summary>
public sealed class RingBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _buffer;
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new T[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public bool IsFull => _count == Capacity;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _buffer[PhysicalIndex(index)];
        }
    }

    public T this[Index index] => this[index.GetOffset(_count)];

    public T Oldest => _count == 0
        ? throw new InvalidOperationException("The ring buffer is empty.")
        : _buffer[_start];

    public T Latest => _count == 0
        ? throw new InvalidOperationException("The ring buffer is empty.")
        : _buffer[PhysicalIndex(_count - 1)];

    /// <summary>
    /// Adds an item. Returns true when the oldest item was overwritten.
    /// </summary>
    public bool Add(T item, out T overwritten)
    {
        if (_count < Capacity)
        {
            _buffer[PhysicalIndex(_count)] = item;
            _count++;
            overwritten = default!;
            return false;
        }

        overwritten = _buffer[_start];
        _buffer[_start] = item;
        _start = (_start + 1) % Capacity;
        return true;
    }

    public void Add(T item) => Add(item, out _);

    public void ReplaceLatest(T item)
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("The ring buffer is empty.");
        }

        _buffer[PhysicalIndex(_count - 1)] = item;
    }

    public bool TryGetLatest(int offset, out T item)
    {
        if (offset < 0 || offset >= _count)
        {
            item = default!;
            return false;
        }

        item = this[_count - 1 - offset];
        return true;
    }

    public T[] Snapshot()
    {
        var result = new T[_count];
        CopyTo(result);
        return result;
    }

    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < _count)
        {
            throw new ArgumentException("Destination is smaller than the buffer count.", nameof(destination));
        }

        if (_count == 0)
        {
            return;
        }

        int firstLength = Math.Min(_count, Capacity - _start);
        _buffer.AsSpan(_start, firstLength).CopyTo(destination);

        int secondLength = _count - firstLength;
        if (secondLength > 0)
        {
            _buffer.AsSpan(0, secondLength).CopyTo(destination[firstLength..]);
        }
    }

    public void Clear(bool clearReferences = true)
    {
        if (clearReferences && RuntimeHelpersEx.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(_buffer);
        }

        _start = 0;
        _count = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int index = 0; index < _count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int PhysicalIndex(int logicalIndex) => (_start + logicalIndex) % Capacity;

    private static class RuntimeHelpersEx
    {
        public static bool IsReferenceOrContainsReferences<TValue>() =>
            System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<TValue>();
    }
}
