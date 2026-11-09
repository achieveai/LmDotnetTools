using System.Runtime.ExceptionServices;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     Writes a loop's rows to the store one at a time, in the order they were issued, so the store is always a
///     prefix of the in-memory history (#950). Without it a row whose save failed left the store behind that
///     history for good, and a later retry would have stored a tool call after its own result.
/// </summary>
/// <remarks>
///     <para>
///         A failed row stays first in the queue and every later row waits behind it. There is no timer: the row
///         is tried again when the next row is issued, when compaction loads rows, and at disposal. One attempt
///         runs at a time, so a locked store cannot pile up writers.
///     </para>
///     <para>
///         There is no cap, because dropping a row is the defect this class exists to prevent. The queue cannot
///         outgrow the in-memory history it mirrors, and compaction skips while a row is unsaved, so a store that
///         stays locked ends in the context-window refusal rather than unbounded growth.
///     </para>
/// </remarks>
internal sealed class OrderedRowSaver(IConversationStore store, string threadId, ILogger logger)
{
    private const int FlushAttempts = 3;
    private static readonly TimeSpan FlushPause = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<Entry> _queue = [];
    private Exception? _lastFailure;
    private Task? _pump;

    /// <summary>Rows not yet durable.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Queues a row without waiting. The returned task completes once the row is durable; it never faults.</summary>
    public Task Enqueue(PersistedMessage row)
    {
        var entry = new Entry(row);
        lock (_gate)
        {
            _queue.Add(entry);
            _ = StartPump();
        }

        return entry.Saved.Task;
    }

    /// <summary>
    ///     Writes everything queued, trying a failed row once more. A pass already running may be about to fail, so
    ///     it is waited for and does not count as the retry. Completes when the queue is empty or a row still cannot
    ///     be written; it never faults, so the caller reads <see cref="Waiting" /> to tell which.
    /// </summary>
    public async Task DrainAsync()
    {
        Task? running;
        lock (_gate)
        {
            running = _pump;
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }

        // Keeps going only while a pass moves the queue: a locked store fails the first attempt and ends this.
        while (true)
        {
            int before;
            Task retry;
            lock (_gate)
            {
                before = _queue.Count;
                if (before == 0)
                {
                    return;
                }

                retry = StartPump();
            }

            await retry.ConfigureAwait(false);

            lock (_gate)
            {
                if (_queue.Count >= before)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    ///     Writes a row after everything queued before it and waits for it, for a caller that must not go on until
    ///     the row is durable. If the row cannot be written it is withdrawn and the failure is thrown.
    /// </summary>
    public async Task SaveNowAsync(PersistedMessage row, CancellationToken ct)
    {
        var entry = new Entry(row);
        Task pump;
        lock (_gate)
        {
            _queue.Add(entry);
            pump = StartPump();
        }

        // The pump may already have been on its way to a failure when this row joined, so one failed pass does
        // not yet mean the store refused a fresh attempt.
        var retries = 1;
        while (true)
        {
            try
            {
                await pump.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    if (!IsBeingWritten(entry))
                    {
                        _ = _queue.Remove(entry);
                    }
                }

                throw;
            }

            Exception failure;
            lock (_gate)
            {
                if (entry.Saved.Task.IsCompleted)
                {
                    return;
                }

                // A later row restarted the pump and it reached this row: its outcome is not known yet.
                if (IsBeingWritten(entry))
                {
                    pump = _pump!;
                    continue;
                }

                if (retries-- > 0)
                {
                    pump = StartPump();
                    continue;
                }

                _ = _queue.Remove(entry);
                failure = _lastFailure!;
            }

            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>
    ///     The last chance to keep queued rows, for disposal: a few attempts inside a fixed budget. Never throws.
    ///     Rows it cannot write are reported at Error, because they are lost when the process ends.
    /// </summary>
    public async Task FlushAsync()
    {
        using var budget = new CancellationTokenSource(FlushBudget);
        try
        {
            for (var attempt = 0; attempt < FlushAttempts && Waiting > 0; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(FlushPause, budget.Token).ConfigureAwait(false);
                }

                await DrainAsync().WaitAsync(budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The budget ran out; the check below reports what is left.
        }

        string? first;
        int left;
        lock (_gate)
        {
            left = _queue.Count;
            first = left == 0 ? null : _queue[0].Row.Id;
        }

        if (left > 0)
        {
            logger.LogError(
                "{Count} rows for thread {ThreadId} could not be saved before shutdown; the first is {RowId}",
                left,
                threadId,
                first
            );
        }
    }

    // Callers hold _gate.
    private Task StartPump() => _pump ??= Task.Run(PumpAsync);

    // Callers hold _gate.
    private bool IsBeingWritten(Entry entry) => _pump is not null && _queue.Count > 0 && _queue[0] == entry;

    private async Task PumpAsync()
    {
        while (true)
        {
            Entry head;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _pump = null;
                    return;
                }

                head = _queue[0];
            }

            try
            {
                await store.AppendMessagesAsync(threadId, [head.Row], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!await IsAlreadyStoredAsync(head.Row.Id).ConfigureAwait(false))
                {
                    int waiting;
                    lock (_gate)
                    {
                        _lastFailure = ex;
                        _pump = null;
                        waiting = _queue.Count - 1;
                    }

                    logger.LogWarning(
                        ex,
                        "Could not save row {RowId} for thread {ThreadId}; it stays first, with {Waiting} later rows behind it",
                        head.Row.Id,
                        threadId,
                        waiting
                    );
                    return;
                }

                logger.LogInformation(
                    "Row {RowId} for thread {ThreadId} is already stored; treating its failed save as done",
                    head.Row.Id,
                    threadId
                );
            }

            lock (_gate)
            {
                _queue.RemoveAt(0);
            }

            _ = head.Saved.TrySetResult();
        }
    }

    /// <summary>
    ///     A tool result's row id is derived from its call id, so writing the same result twice is refused for good.
    ///     Without this check that one row would block every row behind it forever.
    /// </summary>
    private async Task<bool> IsAlreadyStoredAsync(string rowId)
    {
        try
        {
            var rows = await store.LoadMessagesAsync(threadId, CancellationToken.None).ConfigureAwait(false);
            return rows.Any(r => string.Equals(r.Id, rowId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read thread {ThreadId} to check row {RowId}", threadId, rowId);
            return false;
        }
    }

    private sealed class Entry(PersistedMessage row)
    {
        public PersistedMessage Row { get; } = row;

        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
