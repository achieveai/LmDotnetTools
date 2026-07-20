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
///     — reporting cost does not scale with the number of observations. The delegate is responsible for its
///     own error handling; a fault it surfaces is swallowed here so a persistence failure can neither wedge
///     the drain loop nor fault a lifecycle <see cref="FlushAsync" />.
/// </remarks>
internal sealed class UsagePersistenceWriter
{
    private readonly Func<CancellationToken, Task> _persist;
    private readonly object _gate = new();
    private bool _pending;
    private Task _drain = Task.CompletedTask;

    public UsagePersistenceWriter(Func<CancellationToken, Task> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        _persist = persist;
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
    ///     caller (run completion / disposal) proceeds. A no-op when nothing has been scheduled.
    /// </summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_pending && _drain.IsCompleted)
            {
                _drain = DrainAsync();
            }

            return _drain;
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
            catch
            {
                // The delegate logs its own failures; swallow so a persistence fault cannot wedge the
                // loop or fault a lifecycle flush. A later Schedule() re-attempts with the newest state.
            }
        }
    }
}
