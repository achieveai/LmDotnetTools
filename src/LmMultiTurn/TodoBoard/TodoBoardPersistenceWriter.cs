using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;

/// <summary>
///     Change-driven durability for one conversation's todo board (#583, PR 2). The host calls
///     <see cref="Schedule" /> from the board's change hook; the writer captures the LATEST board at
///     write time and persists it through <see cref="ConversationTodoProjection.SaveAsync" />. Because
///     every change schedules a write and disposal flushes, a board evicted or swapped out of the agent
///     pool is never lost — the pool's read-path write-through is a cache warmer, not a durability
///     mechanism.
/// </summary>
/// <remarks>
///     <para>
///         Coalescing is delegated to <see cref="UsagePersistenceWriter" /> — the same no-timer
///         schedule/drain engine the usage ledger uses (#196): a burst of <see cref="Schedule" /> calls
///         collapses into one in-flight write plus at most one trailing write, and a failed write stays
///         pending so the next schedule or flush retries it.
///     </para>
///     <para>
///         One policy guard lives here, at the call site, exactly where
///         <see cref="ConversationTodoProjection.SaveAsync" /> says it belongs: an <b>empty</b> capture is
///         never persisted. From this writer's seat "empty" is indistinguishable from "this process has
///         not seen the board yet", and persisting it would clear a non-empty board another process wrote.
///     </para>
///     <para>
///         The no-mint policy for a thread with <b>no metadata row</b> is inherited from
///         <see cref="ConversationTodoProjection.SaveAsync" /> (#586): it skips silently when the row is
///         absent, and declines by <b>throwing</b> when the conversation vanishes mid-write. The writer
///         treats that decline as final — swallowed and logged — never as a transient failure to retry.
///     </para>
///     <para>
///         The snapshot is re-stamped with the writer's own thread id before saving — the same hazard the
///         push frame guards against: a sub-agent mutating the shared board yields captures stamped with
///         the acting agent's id, and the save must land under the root conversation's row.
///     </para>
/// </remarks>
public sealed class TodoBoardPersistenceWriter : IAsyncDisposable
{
    private readonly IConversationStore _store;
    private readonly string _threadId;
    private readonly Func<TodoBoardSnapshot> _capture;
    private readonly ILogger? _logger;
    private readonly UsagePersistenceWriter _writer;
    private volatile bool _disposed;

    public TodoBoardPersistenceWriter(
        IConversationStore store,
        string threadId,
        Func<TodoBoardSnapshot> capture,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(capture);

        _store = store;
        _threadId = threadId;
        _capture = capture;
        _logger = logger;
        _writer = new UsagePersistenceWriter(
            PersistLatestAsync,
            ex => _logger?.LogWarning(ex, "Failed to persist todo board for thread {ThreadId}", threadId)
        );
    }

    /// <summary>
    ///     Requests a durable write of the current board. Non-blocking; coalesces into the in-flight
    ///     write when one is already running. A no-op after disposal.
    /// </summary>
    public void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        _writer.Schedule();
    }

    /// <summary>
    ///     Awaits any pending or in-flight write. Returns <c>true</c> when nothing remains pending
    ///     (durable, or nothing was ever scheduled), <c>false</c> when a write failed and is still
    ///     outstanding.
    /// </summary>
    public Task<bool> FlushAsync()
    {
        return _writer.FlushAsync();
    }

    /// <summary>
    ///     Flushes any pending write, then goes inert. Wired into the pool entry's owned resources, this
    ///     is the eviction / swap / shutdown capture point: whatever the board looked like after its last
    ///     change is durable before the entry is torn down.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        var durable = await _writer.FlushAsync();
        _disposed = true;

        if (!durable)
        {
            _logger?.LogWarning(
                "Todo board for thread {ThreadId} could not be persisted before disposal; the last change is lost",
                _threadId
            );
        }
    }

    private async Task PersistLatestAsync(CancellationToken ct)
    {
        // Capture at write time, not schedule time: a burst of changes persists its FINAL state once.
        // Re-stamp with this writer's thread id — the capture may carry an acting sub-agent's stamp,
        // and SaveAsync keys the row off snapshot.ThreadId.
        var snapshot = _capture() with
        {
            ThreadId = _threadId,
        };

        if (snapshot.IsEmpty)
        {
            // Not a failure: swallowing (rather than throwing) marks the schedule satisfied, so a
            // flush after "no rows yet" reports a clean boundary instead of an error.
            return;
        }

        // The no-mint policy lives in SaveAsync, not here: it probes for the metadata row and skips
        // silently when none exists (rows are created, ownership-stamped, by the conversation
        // lifecycle — a projection writer is not entitled to create one). Duplicating that probe here
        // would be a redundant conjunct whose removal no test could catch.
        try
        {
            await ConversationTodoProjection.SaveAsync(_store, snapshot, ct);
        }
        catch (TodoBoardDeclinedException ex)
        {
            // SaveAsync's update callback DECLINED the write — the conversation was deleted between
            // its probe and the write. A decline is final, not a transient fault: rethrowing would
            // keep the write pending and retry a deleted conversation forever on the background
            // drain. Caught by EXACT type (#590 review F-003): the store infrastructure throws
            // InvalidOperationException subtypes of its own — ObjectDisposedException from the SQLite
            // connection factory derives from it — and those are genuine faults that must stay
            // pending, fail the flush boundary, and be retried, not be swallowed under a false
            // "conversation no longer exists" record.
            _logger?.LogDebug(
                ex,
                "Todo-board persistence for thread {ThreadId} was declined; the conversation no longer exists",
                _threadId
            );
        }
    }
}
