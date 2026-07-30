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

/// <summary>One observation of the whole descendant roster, flattened across every depth.</summary>
internal sealed record ReviewSubAgentTreeSnapshot(IReadOnlyList<ReviewSubAgentNode> Nodes);

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
