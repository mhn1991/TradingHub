namespace Simulator.Jobs;

public interface IAsyncPauseGate
{
    bool IsPaused { get; }

    ValueTask WaitIfPausedAsync(CancellationToken cancellationToken = default);

    void Pause();

    void Resume();
}

/// <summary>Async frame-boundary pause. Does not block a thread while paused.</summary>
public sealed class AsyncPauseGate : IAsyncPauseGate
{
    private readonly object _sync = new();
    private TaskCompletionSource _gate = CreateOpenGate();
    private bool _paused;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _paused;
        }
    }

    public ValueTask WaitIfPausedAsync(CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (_sync)
        {
            if (!_paused)
                return ValueTask.CompletedTask;
            wait = _gate.Task;
        }

        if (cancellationToken.CanBeCanceled)
            return new ValueTask(wait.WaitAsync(cancellationToken));
        return new ValueTask(wait);
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_paused)
                return;
            _paused = true;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? toRelease = null;
        lock (_sync)
        {
            if (!_paused)
                return;
            _paused = false;
            toRelease = _gate;
            _gate = CreateOpenGate();
        }

        toRelease?.TrySetResult();
    }

    private static TaskCompletionSource CreateOpenGate()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.TrySetResult();
        return open;
    }
}
