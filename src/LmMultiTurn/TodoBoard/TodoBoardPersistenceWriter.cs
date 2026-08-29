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
///         Two policy guards live here, at the call site, exactly where
///         <see cref="ConversationTodoProjection.SaveAsync" /> says they belong:
///     </para>
///     <list type="bullet">
///         <item>
///             An <b>empty</b> capture is never persisted. From this writer's seat "empty" is
///             indistinguishable from "this process has not seen the board yet", and persisting it would
///             clear a non-empty board another process wrote.
///         </item>
///         <item>
///             A thread with <b>no metadata row</b> is skipped rather than minted: metadata rows are
///             created by the conversation lifecycle with their ownership stamp, and a projection writer
///             must never be the thing that brings an unstamped row into existence.
///         </item>
///     </list>
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

        // Never mint a metadata row: rows are created (and ownership-stamped) by the conversation
        // lifecycle. A thread that has no row yet gets its board persisted by a later change, after
        // the row exists. The read-then-write gap here is closed by SaveAsync's transform running
        // under the store's write serialization once #586's no-mint guard lands there.
        var metadata = await _store.LoadMetadataAsync(_threadId, ct);
        if (metadata is null)
        {
            _logger?.LogDebug(
                "Skipping todo-board persistence for thread {ThreadId}: no metadata row exists yet",
                _threadId
            );
            return;
        }

        try
        {
            await ConversationTodoProjection.SaveAsync(_store, snapshot, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The store's update callback DECLINED the write — the conversation was deleted between the
            // probe above and the write. There is no no-op return value a callback can hand back (every
            // IConversationStore persists whatever it returns), so the projection declines by throwing.
            // A decline is final, not a transient fault: rethrowing would keep the write pending and
            // retry a deleted conversation forever, and would surface on the background drain rather
            // than at any caller. Swallow it, once, with a record.
            _logger?.LogDebug(
                ex,
                "Todo-board persistence for thread {ThreadId} was declined; the conversation no longer exists",
                _threadId
            );
        }
    }
}
