namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Serializes and coalesces conversation-usage persistence for one root conversation. Every observation
///     — the primary loop's own usage and each descendant (sub-agent / workflow) relay — is routed through
///     the same writer, so durable writes never interleave, and the owner can <see cref="FlushAsync" /> at
///     run completion / disposal to guarantee the latest ledger snapshot is durable rather than left in
///     memory when the process shuts down (#196).
/// </summary>
/// <remarks>
///     The persist delegate is invoked with the <b>latest</b> ledger state at write time, so a burst of
///     <see cref="Schedule" /> calls collapses into a single in-flight write plus at most one trailing write
///     — reporting cost does not scale with the number of observations. When the delegate <b>throws</b>, the
///     write is kept pending (so a later <see cref="Schedule" /> / <see cref="FlushAsync" /> retries it) and
///     <see cref="FlushAsync" /> returns <c>false</c>, so a failed authoritative write is never silently
///     reported as a clean durability boundary. The failure is reported once via <c>onError</c>.
/// </remarks>
internal sealed class UsagePersistenceWriter
{
    private readonly Func<CancellationToken, Task> _persist;
    private readonly Action<Exception>? _onError;
    private readonly object _gate = new();
    private bool _pending;
    private Task _drain = Task.CompletedTask;

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
            _pending = true;
            if (_drain.IsCompleted)
            {
                _drain = DrainAsync();
            }
        }
    }

    /// <summary>
    ///     Awaits any pending or in-flight write so the latest scheduled snapshot is durable before the
    ///     caller (run completion / disposal) proceeds. Returns <c>true</c> when no write remains pending
    ///     (durable), or <c>false</c> when a write failed and is still outstanding — so the caller can log a
    ///     genuine error rather than treat the boundary as clean. A no-op returning <c>true</c> when nothing
    ///     was scheduled.
    /// </summary>
    public async Task<bool> FlushAsync()
    {
        Task drain;
        lock (_gate)
        {
            if (_pending && _drain.IsCompleted)
            {
                _drain = DrainAsync();
            }

            drain = _drain;
        }

        await drain;

        lock (_gate)
        {
            return !_pending;
        }
    }

    private async Task DrainAsync()
    {
        // Detach from the caller's stack/lock before doing any IO.
        await Task.Yield();

        while (true)
        {
            lock (_gate)
            {
                if (!_pending)
                {
                    return;
                }

                _pending = false;
            }

            try
            {
                await _persist(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Persist failed: retain the pending write so a later Schedule()/FlushAsync() retries it and
                // so FlushAsync reports a non-durable boundary. Stop draining so Flush cannot spin.
                lock (_gate)
                {
                    _pending = true;
                }

                _onError?.Invoke(ex);
                return;
            }
        }
    }
}
