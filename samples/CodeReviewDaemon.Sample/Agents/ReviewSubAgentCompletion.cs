using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Lifecycle of one review sub-agent node as observed by a <see cref="IReviewSubAgentCompletionSource"/>.
/// <see cref="Completed"/>, <see cref="Error"/>, and <see cref="Stopped"/> are terminal — they never block
/// <see cref="ReviewSubAgentCompletionBarrier.WaitAsync"/>. <see cref="Running"/> and <see cref="Unknown"/>
/// are not terminal and keep the barrier closed; a malformed/unrecognized wire status must map to
/// <see cref="Unknown"/> rather than a default/terminal value (a source-implementation concern — Task 4 —
/// not this barrier's).
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
    /// The concrete model this sub-agent's provider was built with, or null when the host did not report
    /// one — either because it predates the field or because the child was never routed. Null is a fact and
    /// must be rendered as such; it is NOT an invitation to substitute the run-level model, which would
    /// present a guess and a measurement identically.
    /// </summary>
    public string? EffectiveModelId { get; init; }

    /// <summary>The intelligence tier that selected <see cref="EffectiveModelId"/>, when tier-based.</summary>
    public int? EffectiveModelIntelligence { get; init; }

    /// <summary>Which routing input won — spawn-model, spawn-tier, template-model, template-tier, parent.</summary>
    public string? ModelSelectionSource { get; init; }
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
    private readonly ILogger<ReviewSubAgentCompletionBarrier> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Builds the barrier. <paramref name="quietPeriod"/> is how long two observations must be separated by
    /// (and be identical across) before the barrier considers the roster stable; it must be positive.
    /// </summary>
    public ReviewSubAgentCompletionBarrier(
        IReviewSubAgentCompletionSource source,
        TimeSpan quietPeriod,
        ILogger<ReviewSubAgentCompletionBarrier> logger,
        TimeProvider? timeProvider = null
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
        var backoffIndex = 0;

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            if (now >= deadlineUtc)
            {
                throw new ReviewBarrierDeadlineException();
            }

            ct.ThrowIfCancellationRequested();
            var snapshot = await _source.GetSnapshotAsync(run, parentThreadId, ct).ConfigureAwait(false);
            var canonical = Canonicalize(snapshot.Nodes);

            if (AllTerminal(canonical))
            {
                if (candidate is not null && SameIdentity(candidate, canonical))
                {
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

    private static bool AllTerminal(IReadOnlyList<ReviewSubAgentNode> canonical) => canonical.All(IsTerminal);

    private static bool IsTerminal(ReviewSubAgentNode node) =>
        node.Status is ReviewSubAgentStatus.Completed or ReviewSubAgentStatus.Error or ReviewSubAgentStatus.Stopped;

    /// <summary>
    /// Compares two already-canonicalized rosters for identity — same count, and for every position the
    /// same <see cref="ReviewSubAgentNode.AgentId"/>, <see cref="ReviewSubAgentNode.ThreadId"/>,
    /// <see cref="ReviewSubAgentNode.ParentThreadId"/>, <see cref="ReviewSubAgentNode.Depth"/>, and
    /// <see cref="ReviewSubAgentNode.Status"/>. Deliberately NOT record default equality: descriptive
    /// fields (<see cref="ReviewSubAgentNode.Name"/>, <see cref="ReviewSubAgentNode.Template"/>,
    /// <see cref="ReviewSubAgentNode.TerminalAtUtc"/>, <see cref="ReviewSubAgentNode.FailureCode"/>) must
    /// NOT reset stability — e.g. a freshly-stamped <c>TerminalAtUtc</c> would otherwise never compare
    /// equal to itself across the candidate/confirmation pair.
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
