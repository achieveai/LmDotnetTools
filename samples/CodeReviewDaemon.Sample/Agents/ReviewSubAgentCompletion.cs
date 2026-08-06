using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Lifecycle of one review sub-agent node as observed by a <see cref="IReviewSubAgentCompletionSource"/>.
/// <see cref="Completed"/>, <see cref="Error"/>, and <see cref="Stopped"/> are terminal — they never block
/// <see cref="ReviewSubAgentCompletionBarrier.WaitAsync"/>. <see cref="Running"/> and <see cref="Unknown"/>
/// are not terminal and keep the barrier closed; a malformed/unrecognized wire status must map to
/// <see cref="Unknown"/> rather than a default/terminal value (a source-implementation concern — Task 4 —
/// not this barrier's).
/// <para>
/// The two non-terminal values are not equally strong, and the barrier treats them differently.
/// <see cref="Running"/> asserts the source knows the child is alive, and nothing but a terminal status
/// clears it. <see cref="Unknown"/> asserts only that the source could not resolve the node at all — such a
/// node has no terminal transition to wait for, so it blocks forever under a terminal-only rule. The
/// barrier therefore admits it once it has been demonstrably inactive for a configured window; see
/// <c>ReviewSubAgentCompletionBarrier.IsQuiescedUnknown</c> for why that is the weakest allowance that
/// still terminates.
/// </para>
/// </summary>
internal enum ReviewSubAgentStatus
{
    Running,
    Completed,
    Error,
    Stopped,
    Unknown,
}

/// <summary>
/// One node of the recursive review sub-agent descendant graph, as reported by a
/// <see cref="IReviewSubAgentCompletionSource"/>. Mirrors the schema v1 field list (plan Task 2) so later
/// consumers (Task 5's safe synthesis inventory) can read <see cref="Name"/>/<see cref="Template"/>/
/// <see cref="FailureCode"/> without a second parallel DTO. <see cref="ParentThreadId"/> and
/// <see cref="Depth"/> are required (unlike the flat/non-recursive <c>SubAgentSummary</c> shape) because
/// every node here is, by construction, a descendant of the polled thread.
/// </summary>
internal sealed record ReviewSubAgentNode
{
    public required string AgentId { get; init; }
    public required string ThreadId { get; init; }
    public required string ParentThreadId { get; init; }
    public required int Depth { get; init; }
    public required ReviewSubAgentStatus Status { get; init; }
    public string? Name { get; init; }
    public required string Template { get; init; }
    public DateTimeOffset? TerminalAtUtc { get; init; }
    public string? FailureCode { get; init; }

    /// <summary>
    /// When this node last did anything observable, as the source reports it. The ONLY evidence available
    /// for a node whose <see cref="Status"/> is <see cref="ReviewSubAgentStatus.Unknown"/>: an unresolved
    /// node reports neither a terminal instant nor a running flag, so without this the barrier cannot
    /// distinguish a child still working from one that finished and was never stamped. A child that is
    /// genuinely working keeps advancing it; an abandoned one freezes. Optional because a source may not
    /// report it, and a node that does not carry it is never treated as quiesced (see
    /// <c>ReviewSubAgentCompletionBarrier</c>) — absence must not be read as inactivity.
    /// </summary>
    public DateTimeOffset? LastActivityUtc { get; init; }
}

/// <summary>
/// Provider-neutral source of the current descendant snapshot for one review run's sub-agent tree. Task 4
/// supplies the concrete in-process and S2S implementations; this barrier only ever depends on this
/// abstraction so it never needs to know which one is behind it.
/// </summary>
internal interface IReviewSubAgentCompletionSource
{
    Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(ReviewRun run, string parentThreadId, CancellationToken ct);
}

/// <summary>
/// One row of a collaborating agent's transcript, as the agent directory's read half publishes it.
/// <para>
/// <b>Untrusted by construction.</b> <see cref="Body"/> is model- and tool-produced text from an agent the
/// daemon does not control: it can carry ANSI escapes, unbalanced markdown fences, and text shaped like
/// tool-call markers. It may only ever reach a file through
/// <see cref="Orchestration.UntrustedTranscriptText"/>, and must never be concatenated into a prompt.
/// </para>
/// </summary>
/// <param name="MessageType">Persisted message type (<c>TextMessage</c>, <c>ToolCallMessage</c>, …).</param>
/// <param name="Role">Who produced it (<c>User</c>/<c>Assistant</c>/<c>System</c>/<c>Tool</c>).</param>
/// <param name="FromAgent">The agent that authored it, when the host recorded one.</param>
/// <param name="TimestampUtc">When it was recorded, when the host recorded one.</param>
/// <param name="Body">The message payload, already reduced to text where the shape allowed it.</param>
internal sealed record ReviewAgentTranscriptEntry(
    string MessageType,
    string Role,
    string? FromAgent,
    DateTimeOffset? TimestampUtc,
    string Body);

/// <summary>
/// Provider-neutral read half of the agent directory: the transcript of one agent named by a
/// <see cref="ReviewSubAgentNode.AgentId"/> from the roster the barrier settled on.
/// <para>
/// Separate from <see cref="IReviewSubAgentCompletionSource"/> on purpose, mirroring the collaboration
/// directory's own split: knowing an agent exists and being able to read what it said are two different
/// privileges, and the barrier needs only the first. Optional at the call site — a daemon whose review
/// host predates the transcript route still reviews and still retains <c>review.md</c>; it just cannot
/// enrich the per-PR artifacts.
/// </para>
/// </summary>
internal interface IReviewAgentTranscriptSource
{
    Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetTranscriptAsync(
        string rootThreadId,
        string agentId,
        CancellationToken ct);

    /// <summary>
    /// The lead reviewer's own transcript — the root conversation itself, not one of its descendants.
    /// <para>
    /// A separate member because the roster this interface's other half is addressed by contains
    /// <b>descendants only</b>: the review host builds it by walking down from the root thread, so the
    /// primary agent is by construction not a node in it and cannot be named to
    /// <see cref="GetTranscriptAsync"/>. Without this the artifacts would carry every specialist's
    /// reasoning and nothing from the reviewer that actually decided the verdict.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
        string rootThreadId,
        CancellationToken ct);
}

/// <summary>One observation of the whole descendant roster, flattened across every depth.</summary>
internal sealed record ReviewSubAgentTreeSnapshot(IReadOnlyList<ReviewSubAgentNode> Nodes)
{
    /// <summary>What <see cref="ToSafeInventory"/> renders for a review that dispatched no sub-agents. A
    /// blank inventory would read as a truncated prompt; this says plainly there is nothing to fold in.</summary>
    public const string NoSubAgents = "No sub-agents were dispatched.";

    /// <summary>
    /// Renders the settled roster for the synthesis prompt: one line per child carrying ONLY its
    /// <see cref="ReviewSubAgentNode.Name"/>, <see cref="ReviewSubAgentNode.Template"/>,
    /// <see cref="ReviewSubAgentNode.Status"/> and <see cref="ReviewSubAgentNode.FailureCode"/>.
    /// <para>
    /// Deliberately impoverished. The synthesis turn reads what the children actually produced through the
    /// delivered-result tools, so the prompt needs only the roster's shape — enough for the model to notice
    /// a reviewer that failed and to weigh its own analysis accordingly. Agent ids, thread ids, prompts and
    /// raw failure text are all omitted: they are execution handles and untrusted content, and embedding
    /// them would put transcript detail into the authoritative review's context. Output is sorted (not
    /// snapshot order) so the same roster always renders the same prompt.
    /// </para>
    /// </summary>
    public string ToSafeInventory()
    {
        var lines = Nodes
            .Select(static n => new
            {
                Name = string.IsNullOrWhiteSpace(n.Name) ? n.Template : n.Name,
                n.Template,
                Status = n.Status.ToString(),
                n.FailureCode,
            })
            .OrderBy(static n => n.Name, StringComparer.Ordinal)
            .ThenBy(static n => n.Template, StringComparer.Ordinal)
            .ThenBy(static n => n.Status, StringComparer.Ordinal)
            .ThenBy(static n => n.FailureCode, StringComparer.Ordinal)
            .Select(static n => string.IsNullOrWhiteSpace(n.FailureCode)
                ? $"- {n.Name} ({n.Template}): {n.Status}"
                : $"- {n.Name} ({n.Template}): {n.Status} — failure: {n.FailureCode}")
            .ToArray();

        return lines.Length == 0 ? NoSubAgents : string.Join("\n", lines);
    }
}

/// <summary>
/// Thrown when <see cref="ReviewSubAgentCompletionBarrier.WaitAsync"/> reaches its caller-supplied absolute
/// deadline without observing two stable, identical, all-terminal snapshots. Fails closed: the caller must
/// never treat this as "probably done" — it is exactly what it says, a timeout.
/// </summary>
internal sealed class ReviewBarrierDeadlineException : TimeoutException;

/// <summary>
/// The provider-neutral shared-deadline barrier every review-completion source (in-process, S2S) waits
/// behind before the daemon may synthesize or post. It polls <see cref="IReviewSubAgentCompletionSource"/>
/// until it observes an all-terminal snapshot (a "candidate"), then waits exactly the configured quiet
/// period and re-polls once more: only if that second snapshot is identical to the candidate — same node
/// ids, parent relationships, and statuses, comparing after canonicalizing both sides by
/// <c>(Depth, ParentThreadId, AgentId)</c> — does the barrier accept it as stable. Any growth, shrinkage,
/// re-parenting, or status change between the two observations resets stability and the search for a
/// candidate resumes.
/// <para>
/// This class never creates or resets a deadline of its own: <c>deadlineUtc</c> (see
/// <see cref="WaitAsync"/>) is the single absolute point in time this call obeys, however much of a wider
/// budget the caller has already spent. Every internal wait (poll backoff or quiet-period wait) is capped
/// by whatever remains until that deadline, so the deadline is detected within one loop iteration of
/// expiry rather than overshooting by up to a whole backoff/quiet interval.
/// </para>
/// <para>
/// Immediately before returning a confirmed candidate, <c>validateReviewStillCurrent</c> runs exactly once
/// — this is the lifecycle/head check that guards against posting against a PR that has since moved on.
/// A failing validator propagates instead of the barrier opening.
/// </para>
/// </summary>
internal sealed class ReviewSubAgentCompletionBarrier
{
    // Nonterminal poll backoff, in seconds: 1, 2, 4, then capped/repeating at 5 thereafter. Monotonic for
    // the whole WaitAsync call — a roster reverting to nonterminal (invalidating a pending candidate) does
    // not rewind it. Quiet-period waits (once a candidate is found) deliberately do NOT use this schedule;
    // they always wait exactly the configured quiet period.
    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(5),
    ];

    private readonly IReviewSubAgentCompletionSource _source;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _unknownQuiescence;
    private readonly ILogger<ReviewSubAgentCompletionBarrier> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Builds the barrier. <paramref name="quietPeriod"/> is how long two observations must be separated by
    /// (and be identical across) before the barrier considers the roster stable; it must be positive.
    /// <paramref name="unknownQuiescence"/> is how long a node whose status is
    /// <see cref="ReviewSubAgentStatus.Unknown"/> must have shown no activity before it stops blocking; null
    /// or non-positive disables that allowance entirely, restoring strict terminal-only settlement.
    /// </summary>
    public ReviewSubAgentCompletionBarrier(
        IReviewSubAgentCompletionSource source,
        TimeSpan quietPeriod,
        ILogger<ReviewSubAgentCompletionBarrier> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? unknownQuiescence = null
    )
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (quietPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quietPeriod),
                quietPeriod,
                "The stability quiet period must be positive."
            );
        }

        _quietPeriod = quietPeriod;
        _unknownQuiescence = unknownQuiescence ?? TimeSpan.Zero;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Waits until every sub-agent descendant of <paramref name="parentThreadId"/> has reached a terminal
    /// status and stayed that way (same roster, identical statuses) across two observations separated by
    /// the configured quiet period, then returns the confirmed snapshot. Throws
    /// <see cref="ReviewBarrierDeadlineException"/> if <paramref name="deadlineUtc"/> is reached first, and
    /// propagates whatever <paramref name="validateReviewStillCurrent"/> throws if the lifecycle/head check
    /// fails right before an otherwise-confirmed candidate would be accepted.
    /// </summary>
    public async Task<ReviewSubAgentTreeSnapshot> WaitAsync(
        ReviewRun run,
        string parentThreadId,
        DateTimeOffset deadlineUtc,
        Func<CancellationToken, Task> validateReviewStillCurrent,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(validateReviewStillCurrent);

        IReadOnlyList<ReviewSubAgentNode>? candidate = null;
        IReadOnlyList<ReviewSubAgentNode> lastObserved = [];
        var backoffIndex = 0;

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            if (now >= deadlineUtc)
            {
                // The barrier's only other outcome is a silent throw, and a silent throw is how run 277
                // burned thirteen full review cycles without anyone being able to name the node that held
                // it open. The roster is the entire diagnosis, so it is logged before the throw, not
                // reconstructed afterwards from a database.
                _logger.LogError(
                    "Run {RunId}: sub-agent barrier timed out on thread {ThreadId} after observing "
                        + "{Count} node(s); unsettled: {Unsettled}. Full roster: {Roster}",
                    run.Id,
                    parentThreadId,
                    lastObserved.Count,
                    Describe(lastObserved.Where(node => !IsSettled(node, now))),
                    Describe(lastObserved)
                );
                throw new ReviewBarrierDeadlineException();
            }

            ct.ThrowIfCancellationRequested();
            var snapshot = await _source.GetSnapshotAsync(run, parentThreadId, ct).ConfigureAwait(false);
            var canonical = Canonicalize(snapshot.Nodes);
            lastObserved = canonical;

            // The snapshot is a round trip to the review host, and it can take longer than whatever budget
            // was left when it started. Judging what came back against the clock reading taken before it
            // would accept a tree confirmed AFTER the deadline had passed, with the overrun bounded only by
            // how long the source took to answer. The deadline this barrier is given is absolute, so the
            // reading has to be refreshed here; looping back rather than throwing in place lets the check
            // above do the logging, and it now has the roster this call just fetched.
            now = _timeProvider.GetUtcNow();
            if (now >= deadlineUtc)
            {
                continue;
            }

            if (AllSettled(canonical, now))
            {
                if (candidate is not null && SameIdentity(candidate, canonical))
                {
                    WarnIfOpeningOverQuiescedUnknowns(run, parentThreadId, canonical, now);
                    await validateReviewStillCurrent(ct).ConfigureAwait(false);
                    return new ReviewSubAgentTreeSnapshot(canonical);
                }

                candidate = canonical;
                await DelayAsync(Cap(_quietPeriod, deadlineUtc - now), ct).ConfigureAwait(false);
                continue;
            }

            candidate = null;
            var backoff = BackoffSchedule[Math.Min(backoffIndex, BackoffSchedule.Length - 1)];
            backoffIndex++;
            await DelayAsync(Cap(backoff, deadlineUtc - now), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sorts a flattened roster by <c>(Depth, ParentThreadId, AgentId)</c> so two observations of
    /// the same logical roster compare equal regardless of the order the source happened to return them in.</summary>
    private static IReadOnlyList<ReviewSubAgentNode> Canonicalize(IReadOnlyList<ReviewSubAgentNode> nodes) =>
        [.. nodes.OrderBy(n => n.Depth).ThenBy(n => n.ParentThreadId, StringComparer.Ordinal).ThenBy(n => n.AgentId, StringComparer.Ordinal)];

    private bool AllSettled(IReadOnlyList<ReviewSubAgentNode> canonical, DateTimeOffset now) =>
        canonical.All(node => IsSettled(node, now));

    /// <summary>
    /// Whether a node may stop blocking the barrier: either it reached a terminal status, or it is an
    /// <see cref="ReviewSubAgentStatus.Unknown"/> node that has demonstrably done nothing for the whole
    /// quiescence window.
    /// </summary>
    private bool IsSettled(ReviewSubAgentNode node, DateTimeOffset now) =>
        IsTerminal(node) || IsQuiescedUnknown(node, now);

    private static bool IsTerminal(ReviewSubAgentNode node) =>
        node.Status is ReviewSubAgentStatus.Completed or ReviewSubAgentStatus.Error or ReviewSubAgentStatus.Stopped;

    /// <summary>
    /// Whether an <see cref="ReviewSubAgentStatus.Unknown"/> node has been inactive long enough to stop
    /// blocking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ReviewSubAgentStatus.Unknown"/> does not mean "running" — it means the source could not
    /// resolve this node's identity at all, so its status was never stamped. Such a node has no terminal
    /// instant to wait for and no running flag to clear, so under a strict terminal-only rule it blocks
    /// forever: the barrier burns its entire deadline, the completed review is discarded, the retry
    /// manufactures another one, and nothing ever converges (run 277, thirteen cycles).
    /// </para>
    /// <para>
    /// Activity is the only honest evidence left. A child that is genuinely working keeps advancing
    /// <see cref="ReviewSubAgentNode.LastActivityUtc"/>; an abandoned one freezes. So an unknown node is
    /// treated as settled only once that timestamp has stood still for the whole window — and it re-blocks
    /// if it ever moves again, whether it moves back INTO the window, which this check sees because
    /// settlement is re-evaluated on every poll, or forward but still outside it, which only
    /// <see cref="SameIdentity"/> sees because such a node stays quiesced. Both halves are needed; each one
    /// alone leaves the other's case open.
    /// </para>
    /// <para>
    /// Three deliberate restrictions keep this from eroding the fail-closed guarantee.
    /// <see cref="ReviewSubAgentStatus.Running"/> is excluded: it is a positive assertion that the source
    /// knows the child is alive, and no amount of silence overrides that. A node carrying no
    /// <see cref="ReviewSubAgentNode.LastActivityUtc"/> at all is excluded: absence of a timestamp is not
    /// evidence of inactivity, and reading it as such would let a source that simply does not report the
    /// field open the barrier over live work. And a non-positive window disables the allowance outright, so
    /// the strict behaviour remains reachable by configuration.
    /// </para>
    /// </remarks>
    private bool IsQuiescedUnknown(ReviewSubAgentNode node, DateTimeOffset now) =>
        node.Status is ReviewSubAgentStatus.Unknown
        && _unknownQuiescence > TimeSpan.Zero
        && node.LastActivityUtc is { } lastActivity
        && now - lastActivity >= _unknownQuiescence;

    /// <summary>
    /// Announces, once, that the barrier opened over one or more unresolved nodes rather than over a fully
    /// terminal roster. This is a weaker guarantee than the barrier's headline contract, so it is never
    /// silent.
    /// </summary>
    private void WarnIfOpeningOverQuiescedUnknowns(
        ReviewRun run,
        string parentThreadId,
        IReadOnlyList<ReviewSubAgentNode> canonical,
        DateTimeOffset now
    )
    {
        var quiesced = canonical.Where(node => IsQuiescedUnknown(node, now)).ToList();
        if (quiesced.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Run {RunId}: sub-agent barrier on thread {ThreadId} opened over {Count} unresolved node(s) "
                + "that showed no activity for {QuiescenceSeconds}s: {Quiesced}. Their status was never "
                + "stamped, so completion is inferred from inactivity rather than observed.",
            run.Id,
            parentThreadId,
            quiesced.Count,
            _unknownQuiescence.TotalSeconds,
            Describe(quiesced)
        );
    }

    /// <summary>
    /// Renders nodes for an operator log: execution handles and lifecycle facts only. Model-authored text
    /// (the task prompt) is deliberately excluded — this string exists to identify which node held the
    /// barrier, not to reproduce what it was asked to do.
    /// </summary>
    private static string Describe(IEnumerable<ReviewSubAgentNode> nodes)
    {
        var rendered = nodes
            .Select(node =>
                $"{node.AgentId}[{node.Status.ToString().ToLowerInvariant()}"
                + $" template={node.Template}"
                + $" depth={node.Depth}"
                + $" lastActivity={node.LastActivityUtc?.ToString("O") ?? "none"}]"
            )
            .ToList();

        return rendered.Count == 0 ? "(none)" : string.Join(", ", rendered);
    }

    /// <summary>
    /// Compares two already-canonicalized rosters for identity — same count, and for every position the
    /// same <see cref="ReviewSubAgentNode.AgentId"/>, <see cref="ReviewSubAgentNode.ThreadId"/>,
    /// <see cref="ReviewSubAgentNode.ParentThreadId"/>, <see cref="ReviewSubAgentNode.Depth"/>, and
    /// <see cref="ReviewSubAgentNode.Status"/>. Deliberately NOT record default equality: descriptive
    /// fields (<see cref="ReviewSubAgentNode.Name"/>, <see cref="ReviewSubAgentNode.Template"/>,
    /// <see cref="ReviewSubAgentNode.TerminalAtUtc"/>, <see cref="ReviewSubAgentNode.FailureCode"/>) must
    /// NOT reset stability — e.g. a freshly-stamped <c>TerminalAtUtc</c> would otherwise never compare
    /// equal to itself across the candidate/confirmation pair.
    /// <para>
    /// <see cref="ReviewSubAgentNode.LastActivityUtc"/> IS compared, but only on a node that is not terminal,
    /// and the scope is the whole point. This comparison is reached only once <see cref="AllSettled"/> holds,
    /// so every non-terminal node in the pair is one <see cref="IsQuiescedUnknown"/> admitted — on the
    /// strength of that timestamp and nothing else. Settling a node from a field the stability check then
    /// ignores leaves a seam: re-evaluating settlement against the confirmation snapshot does catch a node
    /// that woke up INTO the window, but one whose activity moved forward and is STILL older than the window
    /// stays quiesced, and the pair below would then have called it unchanged and opened the barrier over a
    /// child that demonstrably did work between the two observations. The two checks have to read the same
    /// evidence.
    /// </para>
    /// <para>
    /// Terminal nodes are excluded for the same reason the descriptive fields are: a source that re-stamps
    /// last-activity on a child it has already reported as finished — a heartbeat, a clock rounding to a
    /// coarser tick — would reset stability forever and hang the barrier the way run 277 did, and a terminal
    /// node's settlement does not rest on the timestamp, so ignoring it there costs nothing. On a quiesced
    /// unknown that same movement is not noise; it is the only evidence the barrier has that the child is
    /// alive, and heeding it costs at most the deadline the caller already chose. See
    /// <c>WaitAsync_UnknownNodeWhoseActivityAdvancesButStaysOutsideTheWindow_DoesNotOpenTheBarrier</c> and
    /// <c>WaitAsync_UnknownNodeThatWakesUpBetweenTheTwoObservations_DoesNotOpenTheBarrier</c>.
    /// </para>
    /// </summary>
    private static bool SameIdentity(
        IReadOnlyList<ReviewSubAgentNode> candidate,
        IReadOnlyList<ReviewSubAgentNode> confirmation
    )
    {
        if (candidate.Count != confirmation.Count)
        {
            return false;
        }

        for (var i = 0; i < candidate.Count; i++)
        {
            var a = candidate[i];
            var b = confirmation[i];
            if (
                a.AgentId != b.AgentId
                || a.ThreadId != b.ThreadId
                || a.ParentThreadId != b.ParentThreadId
                || a.Depth != b.Depth
                || a.Status != b.Status
                || (!IsTerminal(a) && a.LastActivityUtc != b.LastActivityUtc)
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Clips <paramref name="desired"/> to whatever remains until the deadline, never negative —
    /// the last wait before expiry is always exactly the remaining time, so the deadline is detected within
    /// one loop iteration rather than overshooting by up to a whole backoff/quiet interval.</summary>
    private static TimeSpan Cap(TimeSpan desired, TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? TimeSpan.Zero : (desired < remaining ? desired : remaining);

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _timeProvider, ct);
}
