namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Per-input completion boundary for an explicitly opted-in report run. Producers reserve work
/// before scheduling or admitting it; closing and reservation share the same lock.
/// </summary>
public sealed class FinalReportCompletionGate(string inputId, DateTimeOffset deadlineUtc)
{
    private readonly object _sync = new();
    private TaskCompletionSource _changed = NewSignal();
    private int _reservations;
    private bool _closed;
    private bool _failed;

    public string InputId { get; } = inputId;
    public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;

    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    public IDisposable ReserveWork()
    {
        lock (_sync)
        {
            if (_closed || _failed || DateTimeOffset.UtcNow >= DeadlineUtc)
            {
                throw new InvalidOperationException("The final-report operation cannot accept more work.");
            }

            _reservations++;
            PulseUnderLock();
            return new Reservation(this);
        }
    }

    public void Fail()
    {
        lock (_sync)
        {
            _failed = true;
            PulseUnderLock();
        }
    }

    /// <summary>
    /// Samples existing queued/running work while admission is excluded. The callbacks must only
    /// read in-memory state and must not acquire a lock held while reserving work.
    /// </summary>
    internal Decision Inspect(Func<bool> hasQueuedInput, Func<bool> hasPendingWork, bool hasReport)
    {
        lock (_sync)
        {
            if (_failed || DateTimeOffset.UtcNow >= DeadlineUtc)
            {
                throw new InvalidOperationException("The final-report operation did not settle before its deadline.");
            }

            if (_closed)
            {
                throw new InvalidOperationException("The final-report operation is already closed.");
            }

            if (hasQueuedInput())
            {
                return new(Continue: true, Closed: false, Changed: Task.CompletedTask);
            }

            if (_reservations > 0 || hasPendingWork())
            {
                return new(Continue: false, Closed: false, Changed: _changed.Task);
            }

            if (!hasReport)
            {
                throw new InvalidOperationException("The settled final-report operation produced no report.");
            }

            _closed = true;
            PulseUnderLock();
            return new(Continue: false, Closed: true, Changed: Task.CompletedTask);
        }
    }

    internal async Task WaitAsync(Task changed, CancellationToken ct)
    {
        var remaining = DeadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("The final-report deadline expired.");
        }

        await changed.WaitAsync(remaining, ct).ConfigureAwait(false);
    }

    private void Release()
    {
        lock (_sync)
        {
            _reservations--;
            PulseUnderLock();
        }
    }

    private void PulseUnderLock()
    {
        var previous = _changed;
        _changed = NewSignal();
        _ = previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal readonly record struct Decision(bool Continue, bool Closed, Task Changed);

    private sealed class Reservation(FinalReportCompletionGate owner) : IDisposable
    {
        private FinalReportCompletionGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
