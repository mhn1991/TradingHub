using System.Collections.Concurrent;
using Simulator.Experiments.Models;

namespace Simulator.Experiments;

public sealed class SimulationResourceGovernor : ISimulationResourceGovernor, IDisposable
{
    private readonly SimulationResourceGovernorOptions _options;
    private readonly SemaphoreSlim _experiments;
    private readonly SemaphoreSlim _profileGroups;
    private readonly CapacityGate _workers;
    private readonly CapacityGate? _memory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloads =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _downloadUsage =
        new(StringComparer.OrdinalIgnoreCase);
    private int _experimentUsage;
    private int _profileGroupUsage;
    private int _workerUsage;
    private long _memoryUsage;
    private bool _disposed;

    public SimulationResourceGovernor(SimulationResourceGovernorOptions? options = null)
    {
        _options = options ?? new SimulationResourceGovernorOptions();
        _options.Validate();
        _experiments = new SemaphoreSlim(_options.MaxConcurrentExperiments, _options.MaxConcurrentExperiments);
        _profileGroups = new SemaphoreSlim(_options.MaxConcurrentProfileGroups, _options.MaxConcurrentProfileGroups);
        _workers = new CapacityGate(_options.MaxTotalStrategyWorkers);
        _memory = _options.EstimatedMemoryBudgetBytes is { } budget ? new CapacityGate(budget) : null;
    }

    public async ValueTask<IAsyncDisposable> AcquireExperimentAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _experiments.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _experimentUsage);
        return new Lease(() =>
        {
            Interlocked.Decrement(ref _experimentUsage);
            _experiments.Release();
        });
    }

    public async ValueTask<IAsyncDisposable> AcquireProfileGroupAsync(
        int strategyWorkers,
        long estimatedMemoryBytes = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (strategyWorkers is < 1 || strategyWorkers > int.MaxValue ||
            strategyWorkers > _options.MaxTotalStrategyWorkers)
            throw new ArgumentOutOfRangeException(nameof(strategyWorkers));
        if (estimatedMemoryBytes < 0 ||
            _options.EstimatedMemoryBudgetBytes is { } budget && estimatedMemoryBytes > budget)
            throw new ArgumentOutOfRangeException(nameof(estimatedMemoryBytes));

        await _profileGroups.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool workersHeld = false;
        bool memoryHeld = false;
        try
        {
            await _workers.AcquireAsync(strategyWorkers, cancellationToken).ConfigureAwait(false);
            workersHeld = true;
            if (_memory is not null && estimatedMemoryBytes > 0)
            {
                await _memory.AcquireAsync(estimatedMemoryBytes, cancellationToken).ConfigureAwait(false);
                memoryHeld = true;
            }
            Interlocked.Increment(ref _profileGroupUsage);
            Interlocked.Add(ref _workerUsage, strategyWorkers);
            Interlocked.Add(ref _memoryUsage, estimatedMemoryBytes);
            return new Lease(() =>
            {
                Interlocked.Decrement(ref _profileGroupUsage);
                Interlocked.Add(ref _workerUsage, -strategyWorkers);
                Interlocked.Add(ref _memoryUsage, -estimatedMemoryBytes);
                if (memoryHeld)
                    _memory!.Release(estimatedMemoryBytes);
                _workers.Release(strategyWorkers);
                _profileGroups.Release();
            });
        }
        catch
        {
            if (memoryHeld)
                _memory!.Release(estimatedMemoryBytes);
            if (workersHeld)
                _workers.Release(strategyWorkers);
            _profileGroups.Release();
            throw;
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireHistoricalDownloadAsync(
        string brokerIdentity,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerIdentity);
        string key = brokerIdentity.Trim();
        SemaphoreSlim semaphore = _downloads.GetOrAdd(
            key,
            _ => new SemaphoreSlim(
                _options.MaxHistoricalDownloadsPerBroker,
                _options.MaxHistoricalDownloadsPerBroker));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        _downloadUsage.AddOrUpdate(key, 1, (_, value) => value + 1);
        return new Lease(() =>
        {
            _downloadUsage.AddOrUpdate(key, 0, (_, value) => Math.Max(0, value - 1));
            semaphore.Release();
        });
    }

    public SimulationResourceUsageSnapshot GetUsage() => new()
    {
        ParentExperiments = Volatile.Read(ref _experimentUsage),
        ProfileGroups = Volatile.Read(ref _profileGroupUsage),
        StrategyWorkers = Volatile.Read(ref _workerUsage),
        EstimatedMemoryBytes = Interlocked.Read(ref _memoryUsage),
        HistoricalDownloadsByBroker = _downloadUsage
            .Where(item => item.Value > 0)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase)
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _experiments.Dispose();
        _profileGroups.Dispose();
        foreach (SemaphoreSlim semaphore in _downloads.Values)
            semaphore.Dispose();
    }

    private sealed class Lease(Action release) : IAsyncDisposable
    {
        private Action? _release = release;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapacityGate(long capacity)
    {
        private readonly object _sync = new();
        private readonly long _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
        private long _available = capacity;
        private TaskCompletionSource _changed = NewSignal();

        public async ValueTask AcquireAsync(long amount, CancellationToken cancellationToken)
        {
            if (amount is < 1 || amount > _capacity)
                throw new ArgumentOutOfRangeException(nameof(amount));
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_available >= amount)
                    {
                        _available -= amount;
                        return;
                    }
                    wait = _changed.Task;
                }
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Release(long amount)
        {
            TaskCompletionSource signal;
            lock (_sync)
            {
                if (amount < 1 || _available > _capacity - amount)
                    throw new InvalidOperationException("Resource permit release exceeds capacity.");
                _available += amount;
                signal = _changed;
                _changed = NewSignal();
            }
            signal.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
