using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.Misc.Utils;
using TaskStatus = AchieveAi.LmDotnetTools.Misc.Utils.TaskManager.TaskStatus;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Subtree-scoped todo-board change digests (#609, part of #606 item 3): the primary (root)
///     conversation receives a debounced digest of EVERY board change; an assigned agent receives a
///     digest of changes to any task assigned to it or any subtask below such a task. Delivered as
///     <see cref="NotifyKinds.TodoDigest" /> notifications — informational, unbudgeted, and entirely
///     separate from <see cref="TodoNudgeService" />'s budgeted nudges.
/// </summary>
/// <remarks>
///     <para>
///         <b>Debounce is a fixed window, not a sliding one.</b> The first change after a quiet
///         period opens a <see cref="DebounceWindow" /> window; every change inside it is folded into
///         one flush when the window closes. A sliding window would let a continuous stream of
///         changes postpone the flush forever; the fixed window bounds digest latency at exactly one
///         window. The window is a constant, not config, for the same reason
///         <see cref="TodoNudgeService.MaxStallNudgesPerIdlePeriod" /> is: making it tunable would
///         un-pin that bound.
///     </para>
///     <para>
///         <b>Diffing is flush-time, against the last flushed baseline.</b> A flush reads the board,
///         compares per-row timestamp-free fingerprints against the baseline, and commits the new
///         snapshot — all inside the sync root (#599 review F-002: OnChanged fires outside
///         TaskManager's lock and sub-agents share one manager, so an unserialized read could commit
///         a stale snapshot over a newer one). A flush whose diff is empty — a claim-refresh
///         heartbeat, or changes that net out inside one window — delivers nothing.
///     </para>
///     <para>
///         <b>Subtree membership is computed at diff time, never by assignee equality.</b> An agent
///         hears a change to row R when R is the assigned task itself or lies below it by dotted-path
///         prefix (<c>R == T or R starts with "T."</c> — the trailing dot is what keeps task 11 out
///         of task 1's subtree). Assignee inheritance is a create-time snapshot, so a child overridden
///         to another agent still notifies the parent-task assignee (it is below their task) AND its
///         own assignee. Assigned roots are collected from the old and the new snapshot, so an agent
///         also hears the change that reassigned or removed its own task.
///     </para>
///     <para>
///         <b>Known limitation:</b> <c>TaskManager.OnChanged</c> carries no actor, so the agent whose
///         own tool call made the change hears its own digest too. Deliberate — inventing attribution
///         plumbing is out of scope for #609.
///     </para>
///     <para>
///         The service only READS the board (via the reader delegate). Hydration is silent: the board
///         as of construction is the baseline, exactly like <see cref="TodoNudgeService" />. Deliveries
///         go through the injected delegate into the target conversation's input channel.
///     </para>
/// </remarks>
public sealed class TodoDigestService : IAsyncDisposable
{
    /// <summary>
    ///     Decision of record (#609 HITL): a 3-second per-window debounce, inside the decided 2–5s
    ///     band. A constant, not config: the window is the digest-latency bound AND the burst
    ///     coalescing bound, and making it tunable would un-pin both.
    /// </summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(3);

    private readonly TodoDigestOptions _options;
    private readonly Func<IList<TaskManager.TaskItem>> _boardReader;
    private readonly Func<string, TodoNudgeTargetKind> _targetResolver;
    private readonly Func<string?, NotifyMessage, CancellationToken, ValueTask<bool>> _deliver;
    private readonly ILogger? _logger;
    private readonly ITimer _timer;

    private readonly object _sync = new();
    private BoardView _baseline;
    private bool _windowOpen;

    /// <param name="options">Feature flags; see <see cref="TodoDigestOptions" />.</param>
    /// <param name="boardReader">Read-only view of the board (e.g. <c>TaskManager.GetTasks</c>).</param>
    /// <param name="targetResolver">
    ///     Where a name's conversation lives. Assignee digests are delivered ONLY to names resolving
    ///     to <see cref="TodoNudgeTargetKind.SubAgent" /> — a name that would land in the root
    ///     conversation is skipped, because the root already hears everything via the primary digest
    ///     and a second copy would be a duplicate.
    /// </param>
    /// <param name="deliver">
    ///     Delivery into a conversation's input channel. A null target name addresses the primary
    ///     (root) conversation; a non-null name addresses that sub-agent.
    /// </param>
    /// <param name="timeProvider">Clock seam — the debounce timer comes from here, so tests drive it.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public TodoDigestService(
        TodoDigestOptions options,
        Func<IList<TaskManager.TaskItem>> boardReader,
        Func<string, TodoNudgeTargetKind> targetResolver,
        Func<string?, NotifyMessage, CancellationToken, ValueTask<bool>> deliver,
        TimeProvider timeProvider,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(boardReader);
        ArgumentNullException.ThrowIfNull(targetResolver);
        ArgumentNullException.ThrowIfNull(deliver);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        _boardReader = boardReader;
        _targetResolver = targetResolver;
        _deliver = deliver;
        _logger = logger;

        // Baseline capture: everything on the board RIGHT NOW — including rows hydrated from the
        // persisted projection — is prior state, not a change. A recreate/restart digests nothing.
        _baseline = BoardView.Capture(_boardReader());

        // One-shot, armed by the first change of each window; never periodic.
        _timer = timeProvider.CreateTimer(
            static state => ((TodoDigestService)state!).OnTimerFired(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>
    ///     Synchronous adapter for <c>TaskManager.OnChanged</c>. Runs on the tool-call thread, so it
    ///     does the cheapest possible thing: open the debounce window if it is not already open. The
    ///     board is not read here — the flush reads it once, when the window closes.
    /// </summary>
    public void OnBoardChangedHook()
    {
        lock (_sync)
        {
            if (_windowOpen)
            {
                // Fixed window: changes landing inside an open window ride the already-armed flush.
                // Re-arming here would make the window sliding and the flush postponable forever.
                return;
            }

            _windowOpen = true;
            _ = _timer.Change(DebounceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerFired()
    {
        _ = ObserveAsync(FlushAsync(CancellationToken.None));
    }

    private async Task ObserveAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Todo-digest flush failed");
        }
    }

    /// <summary>
    ///     Closes the current window: snapshot the board, diff it against the last flushed baseline,
    ///     commit, and deliver at most one digest message per audience. A diff with zero changed rows
    ///     (fingerprint-equal — heartbeats, or edits that net out) delivers nothing. Public so tests
    ///     can drive the flush deterministically without a real clock.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<RowChange> changes;
        BoardView before;
        BoardView after;

        lock (_sync)
        {
            // Compare-and-commit inside the lock (#599 F-002): flushes and hooks are serialized
            // here, so a stale board read can never overwrite a newer committed baseline.
            _windowOpen = false;
            before = _baseline;
            after = BoardView.Capture(_boardReader());
            changes = Diff(before, after);
            if (changes.Count == 0)
            {
                // Net-zero window: nothing to say, and the baseline needs no commit (equal rows).
                return;
            }

            _baseline = after;
        }

        var lines = changes.Select(c => c.Line).ToList();

        if (_options.PrimaryDigestEnabled)
        {
            await DeliverAsync(targetName: null, BuildMessage(lines, label: "todo-board"), ct);
        }

        if (!_options.AssigneeDigestEnabled)
        {
            return;
        }

        // Assigned subtree roots from BOTH snapshots: the old side is what lets an agent hear the
        // very change that reassigned or removed its own task.
        var rootsByAgent = CollectAssignedRoots(before, after);
        foreach (var (agentName, roots) in rootsByAgent)
        {
            var relevant = changes.Where(c => roots.Any(root => IsInSubtree(c.Id, root))).Select(c => c.Line).ToList();
            if (relevant.Count == 0)
            {
                continue;
            }

            if (_targetResolver(agentName) != TodoNudgeTargetKind.SubAgent)
            {
                // The name's conversation IS the root conversation — which already got the full
                // digest above. A scoped second copy would be a duplicate, so it is skipped.
                continue;
            }

            await DeliverAsync(agentName, BuildMessage(relevant, label: agentName), ct);
        }
    }

    /// <summary>
    ///     Dotted-path subtree membership: <paramref name="candidateId" /> is
    ///     <paramref name="rootId" /> itself or lies below it. The appended dot is load-bearing —
    ///     without it task "11" would read as part of task "1"'s subtree.
    /// </summary>
    internal static bool IsInSubtree(string candidateId, string rootId)
    {
        return string.Equals(candidateId, rootId, StringComparison.Ordinal)
            || candidateId.StartsWith(rootId + ".", StringComparison.Ordinal);
    }

    private static NotifyMessage BuildMessage(IReadOnlyList<string> lines, string label)
    {
        return NotifyMessage.Create(
            NotifyKinds.TodoDigest,
            detail: "Todo board changes: " + string.Join("; ", lines),
            label: label
        );
    }

    private async ValueTask DeliverAsync(string? targetName, NotifyMessage message, CancellationToken ct)
    {
        var described = targetName ?? "the primary conversation";
        try
        {
            if (!await _deliver(targetName, message, ct))
            {
                _logger?.LogWarning("Todo digest for {Target} was not accepted by its conversation", described);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Todo digest delivery to {Target} failed", described);
        }
    }

    private static Dictionary<string, List<string>> CollectAssignedRoots(BoardView before, BoardView after)
    {
        var rootsByAgent = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Collect(BoardView view)
        {
            foreach (var task in view.Flat)
            {
                if (task.Assignee is not { } agentName)
                {
                    continue;
                }

                if (!rootsByAgent.TryGetValue(agentName, out var roots))
                {
                    roots = [];
                    rootsByAgent[agentName] = roots;
                }

                if (!roots.Contains(task.Id))
                {
                    roots.Add(task.Id);
                }
            }
        }

        Collect(before);
        Collect(after);
        return rootsByAgent;
    }

    private static List<RowChange> Diff(BoardView before, BoardView after)
    {
        var changes = new List<RowChange>();

        foreach (var task in after.Flat)
        {
            if (!before.ById.TryGetValue(task.Id, out var old))
            {
                changes.Add(new RowChange(task.Id, DescribeAdded(task)));
            }
            else if (
                !string.Equals(
                    before.FingerprintById[task.Id],
                    after.FingerprintById[task.Id],
                    StringComparison.Ordinal
                )
            )
            {
                changes.Add(new RowChange(task.Id, DescribeChanged(old, task)));
            }
        }

        foreach (var task in before.Flat)
        {
            if (!after.ById.ContainsKey(task.Id))
            {
                changes.Add(new RowChange(task.Id, $"{task.Id} removed"));
            }
        }

        return changes;
    }

    private static string DescribeAdded(TaskManager.TaskItem task)
    {
        var line = $"{task.Id} added: {Truncate(task.Title)}";
        return task.Assignee is { } agentName ? $"{line} (assignee {agentName})" : line;
    }

    private static string DescribeChanged(TaskManager.TaskItem old, TaskManager.TaskItem task)
    {
        var parts = new List<string>();
        var statusChanged = old.Status != task.Status;

        if (statusChanged)
        {
            parts.Add(
                task.Status switch
                {
                    TaskStatus.InProgress => $"claimed by {task.Assignee ?? "unknown"}",
                    TaskStatus.Completed => "completed",
                    TaskStatus.Blocked => task.BlockedBy.Count > 0
                        ? $"blocked by {string.Join(", ", task.BlockedBy)}"
                        : "blocked",
                    TaskStatus.Removed => "removed",
                    TaskStatus.NotStarted => "reopened",
                    var unmapped => throw new ArgumentOutOfRangeException(
                        nameof(task),
                        unmapped,
                        $"Unmapped {nameof(TaskStatus)} value; add an explicit arm above rather than falling through."
                    ),
                }
            );
        }

        // A claim sets the assignee too; "claimed by X" already says so.
        var claimCoversAssignee = statusChanged && task.Status == TaskStatus.InProgress;
        if (!claimCoversAssignee && !string.Equals(old.Assignee, task.Assignee, StringComparison.Ordinal))
        {
            parts.Add(task.Assignee is { } agentName ? $"assigned to {agentName}" : "unassigned");
        }

        if (!string.Equals(old.Title, task.Title, StringComparison.Ordinal))
        {
            parts.Add($"retitled: {Truncate(task.Title)}");
        }

        var blockCoveredByStatus = statusChanged && task.Status == TaskStatus.Blocked;
        if (!blockCoveredByStatus && !old.BlockedBy.SequenceEqual(task.BlockedBy, StringComparer.Ordinal))
        {
            parts.Add(task.BlockedBy.Count > 0 ? $"blocked by {string.Join(", ", task.BlockedBy)}" : "block cleared");
        }

        if (!old.Notes.SequenceEqual(task.Notes, StringComparer.Ordinal))
        {
            parts.Add(
                task.Notes.Count > old.Notes.Count ? "note added"
                : task.Notes.Count < old.Notes.Count ? "note removed"
                : "notes edited"
            );
        }

        if (!old.Artifacts.SequenceEqual(task.Artifacts, StringComparer.Ordinal))
        {
            parts.Add(task.Artifacts.Count > old.Artifacts.Count ? "artifact attached" : "artifacts updated");
        }

        // Unreachable when fingerprints differ, but a digest must never emit a dangling id.
        return parts.Count == 0 ? $"{task.Id} updated" : $"{task.Id} {string.Join(", ", parts)}";
    }

    private static string Truncate(string title)
    {
        const int maxLength = 60;
        return title.Length <= maxLength ? title : title[..(maxLength - 1)] + "…";
    }

    public ValueTask DisposeAsync()
    {
        // Teardown deliberately does NOT flush: delivering into a conversation that is being torn
        // down (eviction, provider swap, shutdown) is noise at best. Pending changes are simply
        // dropped with the timer.
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record RowChange(string Id, string Line);

    /// <summary>
    ///     An immutable flush-time view of the board: flattened rows in tree order plus per-row
    ///     timestamp-free fingerprints. <see cref="TaskManager.TaskItem" /> is init-only all the way
    ///     down, so holding the rows across flushes is safe.
    /// </summary>
    private sealed class BoardView
    {
        public required List<TaskManager.TaskItem> Flat { get; init; }

        public required Dictionary<string, TaskManager.TaskItem> ById { get; init; }

        public required Dictionary<string, string> FingerprintById { get; init; }

        public static BoardView Capture(IList<TaskManager.TaskItem> roots)
        {
            var flat = new List<TaskManager.TaskItem>();

            void Visit(TaskManager.TaskItem task)
            {
                flat.Add(task);
                foreach (var sub in task.SubTasks)
                {
                    Visit(sub);
                }
            }

            foreach (var task in roots)
            {
                Visit(task);
            }

            var byId = new Dictionary<string, TaskManager.TaskItem>(StringComparer.Ordinal);
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var task in flat)
            {
                byId[task.Id] = task;
                fingerprints[task.Id] = RowFingerprint(task);
            }

            return new BoardView
            {
                Flat = flat,
                ById = byId,
                FingerprintById = fingerprints,
            };
        }

        /// <summary>
        ///     Canonical projection of what one row SAYS: status, title, assignee, blockedBy, notes,
        ///     artifacts. Timestamps are deliberately excluded — a claim-refresh heartbeat touches
        ///     only <c>ClaimedAt</c> and must read as "no change" (same contract as
        ///     <c>TodoNudgeService.Fingerprint</c>, per row instead of board-wide). Control-character
        ///     separators cannot be typed into a title or note through the tools, so fields and list
        ///     entries can never collide into a false "unchanged".
        /// </summary>
        private static string RowFingerprint(TaskManager.TaskItem task)
        {
            const char fieldSeparator = '\u001f';
            const char listSeparator = '\u001e';
            return string.Join(
                fieldSeparator,
                task.Status.ToString(),
                task.Title,
                task.Assignee,
                string.Join(listSeparator, task.BlockedBy),
                string.Join(listSeparator, task.Notes),
                string.Join(listSeparator, task.Artifacts)
            );
        }
    }
}
