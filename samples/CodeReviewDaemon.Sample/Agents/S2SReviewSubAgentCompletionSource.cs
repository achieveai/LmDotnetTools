using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The daemon's S2S view of the review host's agent directory, adapting
/// <see cref="LmStreamingS2SClient"/> onto both halves the daemon consumes:
/// <see cref="IReviewSubAgentCompletionSource"/> (the roster the completion barrier watches) and
/// <see cref="IReviewAgentTranscriptSource"/> (what a named agent actually said, for the per-PR
/// artifacts). All schema-v1 mapping, version-skew fail-closed behavior, and malformed-status-to-Unknown
/// mapping already live on the client — this adapter only supplies the polling target (the parent thread
/// id the barrier is watching) and forwards the client's snapshot or exception unchanged; it never treats
/// an unavailable or incompatible response as an empty success.
/// <para>
/// <b>Deployment order:</b> like the client it wraps, this source depends on the review host already
/// serving the versioned recursive endpoint — deploy the host first, then enable the daemon's
/// completion barrier for S2S review mode.
/// </para>
/// </summary>
internal sealed class S2SReviewSubAgentCompletionSource
    : IReviewSubAgentCompletionSource, IReviewAgentTranscriptSource
{
    private readonly LmStreamingS2SClient _client;

    public S2SReviewSubAgentCompletionSource(LmStreamingS2SClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary><paramref name="run"/> is unused: the S2S poll target is entirely determined by
    /// <paramref name="parentThreadId"/>, exactly like the in-process source.</summary>
    public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
        ReviewRun run,
        string parentThreadId,
        CancellationToken ct) =>
        _client.GetSubAgentTreeAsync(parentThreadId, ct);

    public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetTranscriptAsync(
        string rootThreadId,
        string agentId,
        CancellationToken ct) =>
        _client.GetAgentTranscriptAsync(rootThreadId, agentId, ct);

    public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
        string rootThreadId,
        CancellationToken ct) =>
        _client.GetConversationTranscriptAsync(rootThreadId, ct);
}
