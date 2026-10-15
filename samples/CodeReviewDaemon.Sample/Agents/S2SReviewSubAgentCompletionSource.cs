using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The daemon's S2S view of the review host's agent directory, adapting
/// <see cref="LmStreamingS2SClient"/> onto both halves the daemon consumes:
/// <see cref="IReviewSubAgentCompletionSource"/> (the roster the completion barrier watches) and
/// <see cref="IReviewAgentTranscriptSource"/> (what a named agent actually said, for the per-PR
/// artifacts). All schema-v1 mapping, version-skew fail-closed behavior, and malformed-status-to-Unknown
/// mapping already live on the client. Workflow settlement also samples the parent's activity before
/// and after the descendant roster; other callers retain descendant-only observations. An unavailable
/// or incompatible response is never treated as an empty success.
/// <para>
/// <b>Deployment order:</b> like the client it wraps, this source depends on the review host already
/// serving the versioned recursive endpoint — deploy the host first, then enable the daemon's
/// completion barrier for S2S review mode.
/// </para>
/// </summary>
internal sealed class S2SReviewSubAgentCompletionSource : IReviewSubAgentCompletionSource, IReviewAgentTranscriptSource
{
    private readonly LmStreamingS2SClient _client;
    private readonly bool _includeParentActivity;

    public S2SReviewSubAgentCompletionSource(LmStreamingS2SClient client, bool includeParentActivity = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _includeParentActivity = includeParentActivity;
    }

    /// <summary><paramref name="run"/> is unused: the S2S poll target is entirely determined by
    /// <paramref name="parentThreadId"/>, exactly like the in-process source.</summary>
    public async Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
        ReviewRun run,
        string parentThreadId,
        CancellationToken ct
    )
    {
        if (!_includeParentActivity)
            return await _client.GetSubAgentTreeAsync(parentThreadId, ct).ConfigureAwait(false);

        // A parent can resume while its descendants settle. Sample both sides of the roster read,
        // rather than treating an earlier idle observation as authority for a later phase transition.
        var idleBefore = await _client.IsConversationIdleAsync(parentThreadId, ct).ConfigureAwait(false);
        var snapshot = await _client.GetSubAgentTreeAsync(parentThreadId, ct).ConfigureAwait(false);
        var childrenIdle = true;
        foreach (var child in snapshot.Nodes)
        {
            if (
                child.Status
                is not (ReviewSubAgentStatus.Completed or ReviewSubAgentStatus.Error or ReviewSubAgentStatus.Stopped)
            )
            {
                childrenIdle = false;
                continue;
            }
            childrenIdle &= await _client.IsConversationIdleAsync(child.ThreadId, ct).ConfigureAwait(false);
        }
        var idleAfter = await _client.IsConversationIdleAsync(parentThreadId, ct).ConfigureAwait(false);
        return snapshot with { ParentIsIdle = idleBefore && childrenIdle && idleAfter };
    }

    public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetTranscriptAsync(
        string rootThreadId,
        string agentId,
        CancellationToken ct
    ) => _client.GetAgentTranscriptAsync(rootThreadId, agentId, ct);

    public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
        string rootThreadId,
        CancellationToken ct
    ) => _client.GetConversationTranscriptAsync(rootThreadId, ct);
}
