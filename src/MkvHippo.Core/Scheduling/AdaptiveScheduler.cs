namespace MkvHippo.Core.Scheduling;

/// <summary>
/// Dispatches queued jobs with a concurrency target that can be changed while running.
/// Raising the target dispatches additional jobs immediately; lowering it never interrupts
/// a running job — completed jobs simply aren't backfilled until the active count has
/// drained below the new target. (A semaphore can't express this: permits can't be revoked.)
/// </summary>
public sealed class AdaptiveScheduler
{
    public const int MinTarget = 1;
    public const int MaxTarget = 4;

    private readonly object _gate = new();
    private readonly Queue<Func<CancellationToken, Task>> _pending = new();
    private readonly CancellationToken _ct;
    private int _active;
    private int _target;
    private TaskCompletionSource? _idle;

    public AdaptiveScheduler(int initialTarget, CancellationToken ct = default)
    {
        _target = Math.Clamp(initialTarget, MinTarget, MaxTarget);
        _ct = ct;
    }

    /// <summary>Fires after any change to active/pending/target counts. May fire on any thread.</summary>
    public event Action? StateChanged;

    /// <summary>Fires when a job throws anything other than <see cref="OperationCanceledException"/>.</summary>
    public event Action<Exception>? JobFailed;

    public int Target
    {
        get { lock (_gate) return _target; }
    }

    public int ActiveCount
    {
        get { lock (_gate) return _active; }
    }

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>Callable at any time, including mid-batch from the UI thread.</summary>
    public void SetTarget(int n)
    {
        lock (_gate)
        {
            _target = Math.Clamp(n, MinTarget, MaxTarget);
        }
        StateChanged?.Invoke();
        TryDispatch(); // increases take effect immediately; decreases drain via completions
    }

    public void Enqueue(Func<CancellationToken, Task> job)
    {
        lock (_gate)
        {
            _pending.Enqueue(job);
        }
        TryDispatch();
    }

    /// <summary>Completes once no job is running and nothing is pending.</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            if (_active == 0 && _pending.Count == 0)
                return Task.CompletedTask;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    private void TryDispatch()
    {
        bool changed = false;
        while (true)
        {
            Func<CancellationToken, Task> job;
            lock (_gate)
            {
                if (_ct.IsCancellationRequested && _pending.Count > 0)
                {
                    _pending.Clear();
                    changed = true;
                }
                if (_active >= _target || _pending.Count == 0)
                {
                    if (_active == 0 && _pending.Count == 0 && _idle is not null)
                    {
                        _idle.TrySetResult();
                        _idle = null;
                    }
                    break;
                }
                job = _pending.Dequeue();
                _active++; // only ever incremented while _active < _target
                changed = true;
            }
            _ = RunAsync(job);
        }
        if (changed)
            StateChanged?.Invoke();
    }

    private async Task RunAsync(Func<CancellationToken, Task> job)
    {
        try
        {
            await job(_ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            JobFailed?.Invoke(ex);
        }
        finally
        {
            lock (_gate)
            {
                _active--;
            }
            TryDispatch(); // backfills only if _active < _target
            StateChanged?.Invoke();
        }
    }
}
