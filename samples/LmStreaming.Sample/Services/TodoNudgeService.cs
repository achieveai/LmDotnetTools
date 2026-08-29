using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.Misc.Utils;
using TaskStatus = AchieveAi.LmDotnetTools.Misc.Utils.TaskManager.TaskStatus;

namespace LmStreaming.Sample.Services;

/// <summary>Where a nudge target's conversation lives.</summary>
public enum TodoNudgeTargetKind
{
    /// <summary>The name resolves to a registered sub-agent — nudged by default.</summary>
    SubAgent,

    /// <summary>
    ///     The name does not resolve to a sub-agent, so any nudge would land in the root
    ///     conversation — gated on <see cref="TodoNudgeOptions.NudgeRootConversation" />.
    /// </summary>
    RootConversation,
}

/// <summary>
///     The board talks back (#583 PR 6, design §7.1): assignment notices (N1) and budgeted
///     stalled-agent nudges (N2 run-end, N3 idle-turns, N4 breakdown), delivered as
///     <see cref="NotifyKinds.TodoNudge" /> notifications into the owning agent's conversation.
/// </summary>
/// <remarks>
///     <para>
///         <b>This must not become a perpetual motion machine.</b> The guardrails are the design:
///         stalled-agent nudges are budgeted at <see cref="MaxStallNudgesPerIdlePeriod" /> per agent
///         per idle period, and the budget resets ONLY on a REAL board change — a change to what the
///         board says (statuses, rows, titles, notes, assignees, blockedBy, artifacts), never on time passing, a
///         claim-refresh heartbeat, or the agent merely replying to the nudge. After the budget is
///         spent the agent is marked stalled and the system stops — silence becomes visible instead of
///         an infinite retry.
///     </para>
///     <para>
///         The assignment notice (N1) is NOT budgeted: it fires once per observed assignment
///         TRANSITION (a task's assignee changing to a new value outside a claim), is triggered by an
///         explicit <c>assign-task</c> call, and cannot loop on its own. Assignments detected in the
///         board this service was CONSTRUCTED over are baseline, not transitions — a manager
///         rehydrated via <c>TaskManager.FromSnapshot</c> must not re-notify every pre-existing
///         assignee on recreate/restart.
///     </para>
///     <para>
///         The service only READS the board (via the reader delegate) and never mutates it or
///         publishes frames — its bookkeeping cannot repaint anyone's panel. Deliveries go through
///         the injected delegate into the target agent's input channel, the same door any
///         conversational input uses.
///     </para>
/// </remarks>
public sealed class TodoNudgeService
{
    /// <summary>
    ///     Decision of record (#583): two stalled-agent nudges per agent per idle period — the first
    ///     asks for the work, the second asks for a reason, then the system surrenders and marks the
    ///     agent stalled. A constant, not config: making it tunable would un-pin the loop bound.
    /// </summary>
    public const int MaxStallNudgesPerIdlePeriod = 2;

    private readonly TodoNudgeOptions _options;
    private readonly Func<IList<TaskManager.TaskItem>> _boardReader;
    private readonly Func<string, TodoNudgeTargetKind> _targetResolver;
    private readonly Func<string, NotifyMessage, CancellationToken, ValueTask<bool>> _deliver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _stallNudgeCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _turnsSinceBoardChange = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stalled = new(StringComparer.Ordinal);
    private Dictionary<string, string?> _assigneeByTaskId;
    private string _fingerprint;

    public TodoNudgeService(
        TodoNudgeOptions options,
        Func<IList<TaskManager.TaskItem>> boardReader,
        Func<string, TodoNudgeTargetKind> targetResolver,
        Func<string, NotifyMessage, CancellationToken, ValueTask<bool>> deliver,
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
        _timeProvider = timeProvider;
        _logger = logger;

        // Baseline capture: everything on the board RIGHT NOW — including assignments hydrated from
        // the persisted projection — is prior state, not a transition, so N1 cannot re-fire for it.
        var flat = Flatten(_boardReader());
        _assigneeByTaskId = ToAssigneeMap(flat);
        _fingerprint = Fingerprint(flat);
    }

    /// <summary>
    ///     Agents whose stall-nudge budget is spent — surfaced so a host can render the amber
    ///     "stalled" marker instead of nudging a third time.
    /// </summary>
    public IReadOnlyCollection<string> StalledAgents
    {
        get
        {
            lock (_sync)
            {
                return [.. _stalled];
            }
        }
    }

    /// <summary>
    ///     Synchronous adapter for <c>TaskManager.OnChanged</c>. The hook runs on the tool-call
    ///     thread, so an incomplete delivery is observed in the background rather than blocking the
    ///     tool result; failures are logged, never thrown into the mutation path.
    /// </summary>
    public void OnBoardChangedHook()
    {
        _ = ObserveAsync(HandleBoardChangedAsync(CancellationToken.None));
    }

    private async Task ObserveAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Todo-nudge board-change handling failed");
        }
    }

    /// <summary>
    ///     Board-change bookkeeping: on a REAL change (the board fingerprint — statuses, rows, titles,
    ///     notes, assignees, blockedBy, artifacts; lease timestamps excluded — actually differs) the stall-nudge
    ///     budgets, stalled markers, and idle-turn counters reset, and assignment transitions produce
    ///     N1 notices. A change hook firing without a real change (claim-refresh heartbeat) resets
    ///     nothing.
    /// </summary>
    public async Task HandleBoardChangedAsync(CancellationToken ct = default)
    {
        var notices = new List<(string Assignee, string TaskId, string Title)>();

        lock (_sync)
        {
            // The board read and the fingerprint MUST happen inside the lock (#599 review F-002):
            // TaskManager fires OnChanged after releasing its own lock and sub-agents share one
            // manager, so two hooks can race here. Read outside the lock, a losing thread could
            // commit a snapshot OLDER than the one already committed — rewinding the baseline
            // (later duplicate assignment notice), resetting budgets on stale evidence, and
            // noticing transitions that never happened. Inside the lock, reads and commits are
            // serialized, so a stale snapshot can never overwrite a newer one.
            var flat = Flatten(_boardReader());
            var fingerprint = Fingerprint(flat);

            if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                // Not a real board change (e.g. a lease heartbeat): the budget does NOT reset —
                // an agent cannot earn more nudges without making visible progress.
                return;
            }

            _stallNudgeCounts.Clear();
            _stalled.Clear();
            _turnsSinceBoardChange.Clear();

            if (_options.AssignmentNoticeEnabled)
            {
                foreach (var task in flat)
                {
                    // A transition, not a state: the task existed before this change and its assignee
                    // moved to a new non-null value while NOT in progress (an in-progress assignee
                    // change is a claim — the agent acted, no dispatch notice needed). New rows are
                    // excluded: assign-task only targets existing tasks, and sub-items inheriting an
                    // assignee are created by that assignee's own breakdown.
                    if (
                        task.Assignee is { } next
                        && task.Status != TaskStatus.InProgress
                        && _assigneeByTaskId.TryGetValue(task.Id, out var previous)
                        && !string.Equals(previous, next, StringComparison.Ordinal)
                    )
                    {
                        notices.Add((next, task.Id, task.Title));
                    }
                }
            }

            _assigneeByTaskId = ToAssigneeMap(flat);
            _fingerprint = fingerprint;
        }

        foreach (var (assignee, taskId, title) in notices)
        {
            if (!IsTargetAllowed(assignee))
            {
                continue;
            }

            var message = NotifyMessage.Create(
                NotifyKinds.TodoNudge,
                detail: $"You have been assigned task {taskId}: {title}. Claim it (claim-task) when you start. "
                    + "Break it into sub-items with add-task if it is more than one sitting.",
                label: assignee
            );
            _ = await DeliverAsync(assignee, message, ct);
        }
    }

    /// <summary>
    ///     N2 for a named agent (sub-agents): its run reached a terminal state. Never fires for an
    ///     errored or cancelled run — those are failures to surface, not stalls to nudge.
    /// </summary>
    public Task HandleRunEndedAsync(
        string agentName,
        bool endedWithError,
        bool cancelled,
        CancellationToken ct = default
    )
    {
        if (!_options.RunEndNudgeEnabled || endedWithError || cancelled || string.IsNullOrWhiteSpace(agentName))
        {
            return Task.CompletedTask;
        }

        return TryStallNudgeAsync(agentName, BuildRunEndDetail, ct);
    }

    /// <summary>
    ///     N2 for the root conversation: a root run ended, so every assignee whose conversation IS the
    ///     root gets evaluated. The per-target opt-in gate still applies inside the shared path.
    /// </summary>
    public async Task HandleRootRunEndedAsync(bool endedWithError, CancellationToken ct = default)
    {
        if (!_options.RunEndNudgeEnabled || endedWithError)
        {
            return;
        }

        foreach (var assignee in NonTerminalAssignees())
        {
            if (ResolveTargetKind(assignee) == TodoNudgeTargetKind.RootConversation)
            {
                await TryStallNudgeAsync(assignee, BuildRunEndDetail, ct);
            }
        }
    }

    /// <summary>
    ///     N3 tick: one observed turn for <paramref name="agentName" /> with no board change. At the
    ///     configured threshold the counter re-arms and a budgeted nudge is attempted. Counters reset
    ///     on every real board change.
    /// </summary>
    public async Task HandleTurnCompletedAsync(string agentName, CancellationToken ct = default)
    {
        if (!_options.IdleTurnsNudgeEnabled || string.IsNullOrWhiteSpace(agentName))
        {
            return;
        }

        int turns;
        lock (_sync)
        {
            turns = _turnsSinceBoardChange.GetValueOrDefault(agentName) + 1;
            _turnsSinceBoardChange[agentName] = turns;
        }

        if (turns < _options.IdleTurnThreshold)
        {
            return;
        }

        lock (_sync)
        {
            _turnsSinceBoardChange[agentName] = 0;
        }

        await TryStallNudgeAsync(
            agentName,
            (_, tier) =>
                WithEscalation(
                    tier,
                    $"No task has moved in {_options.IdleTurnThreshold} turns. "
                        + "Update the board — claim, complete, or mark blocked."
                ),
            ct
        );
    }

    /// <summary>
    ///     N3 tick for the root conversation: one observed root run with no board change counts as a
    ///     turn for every assignee whose conversation IS the root. Mirrors
    ///     <see cref="HandleRootRunEndedAsync" />; the per-target opt-in gate applies downstream.
    /// </summary>
    public async Task HandleRootTurnCompletedAsync(CancellationToken ct = default)
    {
        if (!_options.IdleTurnsNudgeEnabled)
        {
            return;
        }

        foreach (var assignee in NonTerminalAssignees())
        {
            if (ResolveTargetKind(assignee) == TodoNudgeTargetKind.RootConversation)
            {
                await HandleTurnCompletedAsync(assignee, ct);
            }
        }
    }

    /// <summary>
    ///     N4 sweep: any in-progress task with no sub-items whose lease is older than the configured
    ///     threshold earns its assignee a breakdown nudge (budgeted). Evaluated on observed events
    ///     only — staleness is derived on read; there is no background timer.
    /// </summary>
    public async Task EvaluateBreakdownAsync(CancellationToken ct = default)
    {
        if (!_options.BreakdownNudgeEnabled)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var threshold = TimeSpan.FromMinutes(_options.BreakdownAfterMinutes);
        var candidates = Flatten(_boardReader())
            .Where(t =>
                t is { Status: TaskStatus.InProgress, Assignee: not null }
                && t.SubTasks.Count == 0
                && t.Times?.ClaimedAt is { } claimed
                && now - claimed > threshold
            )
            .GroupBy(t => t.Assignee!, StringComparer.Ordinal);

        foreach (var group in candidates)
        {
            var task = group.First();
            var active = FormatElapsed(now - task.Times!.ClaimedAt!.Value);
            await TryStallNudgeAsync(
                group.Key,
                (_, tier) =>
                    WithEscalation(
                        tier,
                        $"Task {task.Id} has been active {active} with no sub-items. "
                            + "Break it down with add-task so progress is visible."
                    ),
                ct
            );
        }
    }

    /// <summary>
    ///     The budgeted core every stalled-agent tier funnels through. Never fires when everything the
    ///     agent owns is terminal, or when everything left is blocked on someone else (a correct stop,
    ///     not a stall). At the budget the agent is marked stalled and nothing is delivered.
    /// </summary>
    private async Task TryStallNudgeAsync(
        string agentName,
        Func<IReadOnlyList<TaskManager.TaskItem>, int, string> detailForTier,
        CancellationToken ct
    )
    {
        var owned = Flatten(_boardReader())
            .Where(t => string.Equals(t.Assignee, agentName, StringComparison.Ordinal) && !IsTerminal(t.Status))
            .ToList();

        if (owned.Count == 0 || owned.All(t => t.Status == TaskStatus.Blocked))
        {
            return;
        }

        if (!IsTargetAllowed(agentName))
        {
            return;
        }

        int tier;
        lock (_sync)
        {
            var count = _stallNudgeCounts.GetValueOrDefault(agentName);
            if (count >= MaxStallNudgesPerIdlePeriod)
            {
                // Escalation, then surrender: the budget is spent, so the agent is marked stalled
                // (visible to the host) and the system stops instead of nudging forever.
                if (_stalled.Add(agentName))
                {
                    _logger?.LogWarning(
                        "Agent {AgentName} is stalled: {NudgeCount} nudges delivered with no board change since",
                        agentName,
                        count
                    );
                }

                return;
            }

            tier = count;
        }

        var message = NotifyMessage.Create(NotifyKinds.TodoNudge, detail: detailForTier(owned, tier), label: agentName);
        if (await DeliverAsync(agentName, message, ct))
        {
            lock (_sync)
            {
                _stallNudgeCounts[agentName] = _stallNudgeCounts.GetValueOrDefault(agentName) + 1;
            }
        }
    }

    private string BuildRunEndDetail(IReadOnlyList<TaskManager.TaskItem> owned, int tier)
    {
        var summary = string.Join(", ", owned.Select(t => $"{t.Id} ({DescribeStatus(t.Status)})"));
        return WithEscalation(
            tier,
            $"You stopped with {owned.Count} task(s) unfinished: {summary}. "
                + "Finish them, mark them blocked with a reason, or remove them."
        );
    }

    /// <summary>
    ///     Tier 0 asks for the work; tier 1 asks for a reason and says the system will stop asking —
    ///     the second nudge is the last one this idle period.
    /// </summary>
    private static string WithEscalation(int tier, string body)
    {
        return tier == 0
            ? body
            : body
                + " This is the second and final nudge: if you cannot finish, record WHY on the board "
                + "(block-task with a cause, a note, or remove the task). No further nudges will be sent "
                + "until the board actually changes.";
    }

    private async ValueTask<bool> DeliverAsync(string agentName, NotifyMessage message, CancellationToken ct)
    {
        try
        {
            var delivered = await _deliver(agentName, message, ct);
            if (!delivered)
            {
                _logger?.LogWarning("Todo nudge for {AgentName} was not accepted by its conversation", agentName);
            }

            return delivered;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Todo nudge delivery to {AgentName} failed", agentName);
            return false;
        }
    }

    private bool IsTargetAllowed(string agentName)
    {
        return ResolveTargetKind(agentName) switch
        {
            TodoNudgeTargetKind.SubAgent => true,
            TodoNudgeTargetKind.RootConversation => _options.NudgeRootConversation,
            // CS8524: a switch over an enum still compiles with an unnamed value unhandled, so an
            // unmapped member must fail loudly here rather than silently nudge (or silently drop).
            var unmapped => throw new ArgumentOutOfRangeException(
                nameof(agentName),
                unmapped,
                $"Unmapped {nameof(TodoNudgeTargetKind)} value; add an explicit arm above rather than falling through."
            ),
        };
    }

    private TodoNudgeTargetKind ResolveTargetKind(string agentName)
    {
        return _targetResolver(agentName);
    }

    private List<string> NonTerminalAssignees()
    {
        return
        [
            .. Flatten(_boardReader())
                .Where(t => t.Assignee is not null && !IsTerminal(t.Status))
                .Select(t => t.Assignee!)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static bool IsTerminal(TaskStatus status)
    {
        return status is TaskStatus.Completed or TaskStatus.Removed;
    }

    private static string DescribeStatus(TaskStatus status)
    {
        return status switch
        {
            TaskStatus.NotStarted => "todo",
            TaskStatus.InProgress => "in progress",
            TaskStatus.Blocked => "blocked",
            TaskStatus.Completed => "completed",
            TaskStatus.Removed => "removed",
            var unmapped => throw new ArgumentOutOfRangeException(
                nameof(status),
                unmapped,
                $"Unmapped {nameof(TaskStatus)} value; add an explicit arm above rather than falling through."
            ),
        };
    }

    private static List<TaskManager.TaskItem> Flatten(IList<TaskManager.TaskItem> roots)
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

        return flat;
    }

    private static Dictionary<string, string?> ToAssigneeMap(List<TaskManager.TaskItem> flat)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var task in flat)
        {
            map[task.Id] = task.Assignee;
        }

        return map;
    }

    /// <summary>
    ///     Canonical projection of what the board SAYS: ids, statuses, titles, notes, assignees and
    ///     blockedBy and artifacts, in tree order. Lease/creation/completion timestamps are deliberately excluded —
    ///     a claim-refresh heartbeat touches only <c>ClaimedAt</c> and must not count as progress, and
    ///     "real board change, not time" is the budget's reset contract.
    /// </summary>
    private static string Fingerprint(List<TaskManager.TaskItem> flat)
    {
        // Control-character separators (unit/record/group): they cannot be typed into a title or a
        // note through the tools, so fields and rows can never collide into a false "unchanged".
        const char fieldSeparator = '\u001f';
        const char listSeparator = '\u001e';
        const char rowSeparator = '\u001d';
        var sb = new System.Text.StringBuilder();
        foreach (var task in flat)
        {
            _ = sb.Append(task.Id)
                .Append(fieldSeparator)
                .Append(task.Status)
                .Append(fieldSeparator)
                .Append(task.Title)
                .Append(fieldSeparator)
                .Append(task.Assignee)
                .Append(fieldSeparator)
                .Append(string.Join(listSeparator, task.BlockedBy))
                .Append(fieldSeparator)
                .Append(string.Join(listSeparator, task.Notes))
                .Append(fieldSeparator)
                // Artifacts are board content (#596): attaching a file is visible progress, so it
                // must end an idle period exactly like a note or a status change would.
                .Append(string.Join(listSeparator, task.Artifacts))
                .Append(rowSeparator);
        }

        return sb.ToString();
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1)
        {
            return $"{(int)elapsed.TotalSeconds}s";
        }

        return elapsed.TotalHours < 1
            ? $"{(int)elapsed.TotalMinutes}m"
            : $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
    }
}
