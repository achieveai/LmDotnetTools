namespace LmStreaming.Sample.Services;

/// <summary>
///     Coalescing background drain for the workspace transcript mirror (#251). Callers <see cref="Schedule" />
///     a key (a conversation's threadId) from the message-subscriber hot path; a single background loop later
///     invokes the flush callback once per pending key. The point of the indirection is that the subscriber
///     never awaits I/O — a transcript flush talks to the sandbox gateway, and blocking the loop body on that
///     would add gateway latency to every turn boundary.
/// </summary>
/// <remarks>
///     <para>
///     <b>This is a deliberate COPY of <c>UsagePersistenceWriter</c>'s Schedule/Drain shape, not a duplicate to
///     be deleted.</b> That type is <c>internal sealed</c> in <c>AchieveAi.LmDotnetTools.LmMultiTurn</c>, and its
///     <c>InternalsVisibleTo</c> grants cover only the <i>test</i> assemblies (<c>LmMultiTurn.Tests</c>,
///     <c>LmWorkflow.Tests</c>, <c>LmStreaming.Sample.Tests</c>) — <b>not</b> this sample's production assembly.
///     It is therefore unreachable from here, and this copy carries its own tests; "already reviewed elsewhere"
///     does not transfer.
///     </para>
///     <para>
///     Three deliberate differences from the original:
///     </para>
///     <list type="number">
///         <item>
///             A <see cref="HashSet{T}" /> of pending keys rather than one <c>bool _pending</c> slot. A single
///             slot silently drops conversation A's pending flush the moment conversation B schedules.
///         </item>
///         <item>
///             <b>Failures are caught per key and the loop continues.</b> The original's catch re-arms the
///             pending flag and <i>returns</i>, aborting the whole drain — with N keys that strands every other
///             conversation behind one failing one, and because the failing key is re-armed a restarted drain
///             picks it first and aborts again. One permanently-broken conversation would block the mirror
///             indefinitely.
///         </item>
///         <item>
///             <see cref="Schedule" /> stays strictly non-blocking, and <c>await Task.Yield()</c> stays the
///             drain's first statement (see <see cref="DrainAsync" /> for why that line is load-bearing).
///         </item>
///     </list>
///     <para>
///     <b>Deliberately NOT implemented:</b> there is no <c>FlushAsync()</c> and no disposal-time flush.
///     <c>Schedule()</c> at disposal flushes nothing, and an <i>awaited</i> flush at process shutdown loses a
///     race with <c>SandboxSessionRegistry</c>'s <c>ObjectDisposedException.ThrowIf</c> guard and is swallowed
///     anyway — so the "fixed" version does not reliably work either. A run cancelled or shut down mid-flight is
///     picked up by the next completed run (the mirror's watermark is cumulative); that is an <b>accepted,
///     documented gap</b>, not an oversight.
///     </para>
///     <para>
///     <b>Disposal is synchronous <see cref="IDisposable" /> on purpose</b> — never <c>IAsyncDisposable</c>-only.
///     The sample host is torn down with the synchronous <c>IHost.Dispose()</c> in tests
///     (<c>BrowserWebAppFactory</c>), and a container-tracked <c>IAsyncDisposable</c>-only singleton makes
///     <c>ServiceProvider.Dispose()</c> throw <i>"only implements IAsyncDisposable"</i>, which would break every
///     E2E test. See the verbatim note at <c>Program.cs</c> where <c>SqliteConnectionFactory</c> is constructed
///     inline for exactly this reason.
///     </para>
/// </remarks>
public sealed class TranscriptFlushScheduler : IDisposable
{
    /// <summary>
    ///     How long <see cref="Dispose" /> waits on an in-flight flush before giving up on it. Bounded because
    ///     shutdown must not hang on a gateway call; the abandoned flush's work is recovered by the next run.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string, CancellationToken, Task> _flush;
    private readonly Action<string, Exception>? _onError;
    private readonly TimeSpan _shutdownDrainTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private Task _drain = Task.CompletedTask;

    /// <summary>
    ///     Whether a drain loop is live, tracked explicitly under <see cref="_gate"/> rather than inferred
    ///     from <c>_drain.IsCompleted</c>.
    /// </summary>
    /// <remarks>
    ///     <b>This flag is the fix for a lost wakeup, not a convenience.</b> <see cref="DrainAsync"/>
    ///     decides to stop from INSIDE the lock, but the <see cref="Task"/> it returns only transitions to
    ///     completed after the lock is released and the state machine unwinds. In that window a concurrent
    ///     <see cref="Schedule"/> takes the gate, adds its key, observes <c>_drain.IsCompleted == false</c>
    ///     and starts nothing — while the loop it assumed was running has already committed to exiting and
    ///     will never look at the pending set again. That key then waits for an unrelated future
    ///     <c>Schedule</c>, which for a conversation whose last turn just ended never comes: the transcript
    ///     silently stops at the previous turn. Setting and clearing the flag at the same two points
    ///     INSIDE the lock closes the window, because the decision to exit and the publication of that
    ///     decision become one atomic step.
    /// </remarks>
    private bool _draining;

    private bool _disposed;

    /// <summary>Creates a scheduler that drains pending keys through <paramref name="flush" />.</summary>
    /// <param name="flush">
    ///     Invoked once per pending key on the drain loop. Never invoked concurrently with itself — the drain is
    ///     a single loop — so an implementation may assume flushes are serialized process-wide.
    /// </param>
    /// <param name="onError">
    ///     Reports a flush failure (the key, then the exception) so the owner can log it. The drain continues
    ///     regardless, and a throwing <paramref name="onError" /> is itself swallowed: the error channel must
    ///     never be able to stop the loop.
    /// </param>
    /// <param name="shutdownDrainTimeout">
    ///     Overrides <see cref="DefaultShutdownDrainTimeout" /> (tests use a short one).
    /// </param>
    public TranscriptFlushScheduler(
        Func<string, CancellationToken, Task> flush,
        Action<string, Exception>? onError = null,
        TimeSpan? shutdownDrainTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(flush);
        _flush = flush;
        _onError = onError;
        _shutdownDrainTimeout = shutdownDrainTimeout ?? DefaultShutdownDrainTimeout;
    }

    /// <summary>
    ///     Requests a flush of <paramref name="key" />. <b>Strictly non-blocking</b> and fire-and-forget: it
    ///     takes a short lock and returns, doing no I/O on the caller's thread. Repeat calls for the same key
    ///     coalesce — while a key's flush is in flight the key is already out of the pending set, so a
    ///     re-schedule re-adds it and earns exactly one more flush rather than being lost or duplicated.
    ///     A no-op after <see cref="Dispose" /> (the mirror is best-effort; shutdown must not throw into a
    ///     message-subscriber loop).
    /// </summary>
    public void Schedule(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _ = _pending.Add(key);
            if (!_draining)
            {
                _draining = true;
                _drain = DrainAsync();
            }
        }
    }

    private async Task DrainAsync()
    {
        // LOAD-BEARING, and must stay the first statement. Schedule() calls DrainAsync() while holding _gate,
        // so without this yield the whole synchronous prologue — including the first _flush(...) call, up to
        // its own first real await — would run on the caller's thread inside the lock. That would put gateway
        // I/O straight back on the subscriber hot path, which is the one thing this type exists to prevent.
        await Task.Yield();

        // Captured once: the token is handed to every flush, and re-reading _shutdown.Token after the source
        // is disposed would throw. Dispose() only disposes the source once this loop has finished.
        var shutdownToken = _shutdown.Token;

        while (true)
        {
            string key;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0)
                {
                    // Cleared HERE, under the same lock that made the decision — see _draining. Any
                    // Schedule that reaches the gate after this point starts a fresh loop.
                    _draining = false;
                    return;
                }

                key = _pending.First();
                _ = _pending.Remove(key);
            }

            try
            {
                await _flush(key, shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Shutdown cancelled this flush. Not an error, and not worth reporting: the accepted gap is
                // that a run interrupted mid-flight is flushed by the next completed run instead.
                lock (_gate)
                {
                    _draining = false;
                }

                return;
            }
            catch (Exception ex)
            {
                // THE line this copy exists for: one key's failure must not strand the others. Report it and
                // move on to the next pending key rather than aborting the loop.
                ReportError(key, ex);
            }
        }
    }

    private void ReportError(string key, Exception error)
    {
        try
        {
            _onError?.Invoke(key, error);
        }
        catch (Exception)
        {
            // A throwing error channel would fault the drain task and leave it unobserved. Swallowed on
            // purpose: there is nowhere left to report a failure of the thing that reports failures.
        }
    }

    /// <summary>
    ///     Stops accepting work, drops anything still pending, and waits <b>bounded</b> on the in-flight flush.
    ///     It does not flush pending keys — see the type remarks for why that trigger is deliberately absent.
    /// </summary>
    public void Dispose()
    {
        Task drain;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
            drain = _drain;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (Exception)
        {
            // Cancel() surfaces exceptions thrown by callbacks registered downstream on our token. Shutdown
            // proceeds regardless — Dispose() must not throw out of host teardown.
        }

        var drainFinished = true;
        try
        {
            drainFinished = drain.Wait(_shutdownDrainTimeout);
        }
        catch (Exception)
        {
            // A faulted/cancelled drain is a finished drain. Per-key failures were already reported.
        }

        if (drainFinished)
        {
            // Only safe once nothing can still be holding the token: an abandoned in-flight flush would see
            // ObjectDisposedException from a disposed source. Leaking the source in that case is harmless —
            // it holds no OS handle unless its WaitHandle was taken, and the process is shutting down.
            _shutdown.Dispose();
        }
    }
}
