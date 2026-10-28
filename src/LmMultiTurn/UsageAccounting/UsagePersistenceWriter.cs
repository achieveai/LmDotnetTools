namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Serializes and coalesces conversation-usage persistence for one root conversation. Every observation
///     — the primary loop's own usage and each descendant (sub-agent / workflow) relay — is routed through
///     the same writer, so durable writes never interleave. Run completion awaits
///     <see cref="WaitForScheduledAsync" />, so a run is not reported complete until the write of usage it
///     already observed has finished (#855). Persistence is best-effort: if that write fails, the failure
///     is reported and the run still completes without the usage. Disposal awaits <see cref="FlushAsync" />
///     so the latest ledger snapshot is written rather than left in memory at shutdown (#196).
/// </summary>
/// <remarks>
///     The persist delegate is invoked with the <b>latest</b> ledger state at write time, so a burst of
///     <see cref="Schedule" /> calls collapses into a single in-flight write plus at most one trailing write
///     — reporting cost does not scale with the number of observations. When the delegate <b>throws</b>, the
///     write is kept pending (so a later <see cref="Schedule" />, <see cref="FlushAsync" /> or
///     <see cref="WaitForScheduledAsync" /> retries it) and the boundary waits return <c>false</c>, so a
///     failed authoritative write is never silently reported as a clean durability boundary. The failure
///     is reported once via <c>onError</c>, before the waiters of that write are released.
/// </remarks>
internal sealed class UsagePersistenceWriter
{
    private readonly Func<CancellationToken, Task> _persist;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private long _scheduled;
    private long _durable;
    private bool _draining;
    private Task _drain = Task.CompletedTask;
    private readonly List<(long Target, TaskCompletionSource<bool> Completion)> _waiters = [];

    public UsagePersistenceWriter(Func<CancellationToken, Task> persist, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(persist);
        _persist = persist;
        _onError = onError;
    }

    /// <summary>
    ///     Requests a durable write of the current ledger state. Non-blocking; coalesces into the in-flight
    ///     drain when one is already running.
    /// </summary>
    public void Schedule()
    {
        lock (_gate)
        {
            _scheduled++;
            StartDrain();
        }
    }

    /// <summary>
    ///     Awaits the full drain, including observations scheduled while it is running. Used at shutdown,
    ///     after the owner has stopped its producers. Returns false when storage failed and work remains.
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        Task drain;
        lock (_gate)
        {
            if (_durable < _scheduled)
            {
                StartDrain();
            }
            drain = _drain;
        }

        await drain;
        lock (_gate)
        {
            return _durable >= _scheduled;
        }
    }

    /// <summary>
    ///     Awaits observations scheduled before this call. Later observations do not extend the captured
    ///     boundary. Returns <c>true</c> once that boundary is durable, or <c>false</c> when its write failed
    ///     and remains outstanding for a later retry. A no-op returning <c>true</c> when already durable.
    /// </summary>
    public Task<bool> WaitForScheduledAsync()
    {
        lock (_gate)
        {
            if (_durable >= _scheduled)
            {
                return Task.FromResult(true);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((_scheduled, completion));
            StartDrain();
            return completion.Task;
        }
    }

    // Called under _gate; publishing drain ownership here also closes the schedule/exit race.
    private void StartDrain()
    {
        if (!_draining)
        {
            _draining = true;
            _drain = DrainAsync();
        }
    }

    private async Task DrainAsync()
    {
        // Detach from the caller's stack/lock before doing any IO.
        await Task.Yield();

        while (true)
        {
            long target;
            lock (_gate)
            {
                if (_durable >= _scheduled)
                {
                    _draining = false;
                    return;
                }

                target = _scheduled;
            }

            try
            {
                await _persist(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Persist failed: retain the pending write so a later trigger retries it and report a
                // non-durable boundary. Stop draining so Flush cannot spin. Ownership is released BEFORE
                // reporting, so a Schedule() that lands while the failure is logged starts its own retry;
                // the waiters are released AFTER it, so a caller told "not durable" finds it reported.
                TaskCompletionSource<bool>[] failed;
                lock (_gate)
                {
                    _draining = false;
                    failed = [.. _waiters.Select(waiter => waiter.Completion)];
                    _waiters.Clear();
                }

                try
                {
                    _onError?.Invoke(ex);
                }
                finally
                {
                    foreach (var completion in failed)
                    {
                        completion.SetResult(false);
                    }
                }

                return;
            }

            lock (_gate)
            {
                _durable = target;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Target <= _durable)
                    {
                        _waiters[i].Completion.SetResult(true);
                        _waiters.RemoveAt(i);
                    }
                }
            }
        }
    }
}
