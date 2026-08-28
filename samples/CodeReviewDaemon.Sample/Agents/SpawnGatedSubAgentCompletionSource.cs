using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// An <see cref="IReviewSubAgentCompletionSource"/> decorator that runs every observed sub-agent template
/// through a <see cref="ReviewSpawnGate"/>. It sits on the barrier's polling path deliberately: that is the
/// EARLIEST moment the daemon sees a child at all, so a posting-capable spawn is named while the review is
/// still running rather than after the fact — and it is named once per template, not once per poll, so a
/// twelve-minute barrier does not turn one refusal into two hundred log lines.
/// <para>
/// The snapshot is forwarded UNCHANGED. Filtering refused nodes out of the roster was considered and
/// rejected: the barrier would then open while a refused child was still running, and the notes artifacts
/// would lose the very evidence that it ran. The gate's product is the refusal record, not a smaller tree.
/// </para>
/// <para>
/// <b>This decorator does not stop the spawn.</b> On the S2S path the review host starts sub-agents; the
/// daemon provisions the conversation and then reads the descendant tree over REST. There is no host call
/// that refuses one template while permitting another — the only spawn control on the wire is the
/// all-or-nothing per-turn <c>SuppressSubAgentSpawning</c>, which the review turn cannot use without losing
/// the review specialists it depends on. So what this closes is the AUDIT hole, not the egress one.
/// </para>
/// </summary>
internal sealed class SpawnGatedSubAgentCompletionSource : IReviewSubAgentCompletionSource
{
    private readonly IReviewSubAgentCompletionSource _inner;
    private readonly ReviewSpawnGate _gate;
    private readonly HashSet<string> _decided = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _refused = [];
    private readonly Lock _sync = new();

    public SpawnGatedSubAgentCompletionSource(IReviewSubAgentCompletionSource inner, ReviewSpawnGate gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>The templates this source has refused so far, in first-seen order (for tests/diagnostics).</summary>
    public IReadOnlyList<string> RefusedTemplates
    {
        get
        {
            lock (_sync)
            {
                return [.. _refused];
            }
        }
    }

    public async Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
        ReviewRun run,
        string parentThreadId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        var snapshot = await _inner.GetSnapshotAsync(run, parentThreadId, ct).ConfigureAwait(false);

        foreach (var node in snapshot.Nodes)
        {
            // Decide each template ONCE per review, not once per poll: the barrier re-reads the tree every
            // few seconds for as long as the fan-out runs, and a refusal logged two hundred times is a
            // refusal nobody reads.
            bool firstSighting;
            lock (_sync)
            {
                firstSighting = _decided.Add(node.Template);
            }

            // The run id goes in the TARGET, not just the log line. This is the only place it survives into
            // the durable row: policy_refusal carries no run column (the HTTP seam that shares the table has
            // no run to name), and nothing maps a thread id back to a run, so a target of "thread X (agent Y)"
            // alone would leave an operator with a refusal they cannot attribute to a review.
            if (!firstSighting
                || _gate.IsSpawnAllowed(
                    run.Id,
                    node.Template,
                    $"run {run.Id} thread {node.ThreadId} (agent {node.AgentId})"))
            {
                continue;
            }

            lock (_sync)
            {
                _refused.Add(node.Template);
            }
        }

        return snapshot;
    }
}
