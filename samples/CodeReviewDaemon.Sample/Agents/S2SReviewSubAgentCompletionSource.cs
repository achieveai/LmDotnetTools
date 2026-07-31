using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Thin adapter of <see cref="LmStreamingS2SClient.GetSubAgentTreeAsync"/> onto
/// <see cref="IReviewSubAgentCompletionSource"/> for the daemon's S2S review mode. All schema-v1 mapping,
/// version-skew fail-closed behavior, and malformed-status-to-Unknown mapping already live on the client
/// (<see cref="LmStreamingS2SClient.GetSubAgentTreeAsync"/>) — this adapter only supplies the polling
/// target (the parent thread id the barrier is watching) and forwards the client's snapshot or exception
/// unchanged; it never treats an unavailable or incompatible response as an empty success.
/// <para>
/// <b>Deployment order:</b> like the client it wraps, this source depends on the review host already
/// serving the versioned recursive endpoint — deploy the host first, then enable the daemon's
/// completion barrier for S2S review mode.
/// </para>
/// </summary>
internal sealed class S2SReviewSubAgentCompletionSource : IReviewSubAgentCompletionSource
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
}
