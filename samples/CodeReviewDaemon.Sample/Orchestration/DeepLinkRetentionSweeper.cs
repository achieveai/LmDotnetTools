using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Enforces the maximum lifetime of a review deep-link. Every posted comment carries
/// <c>{LmStreamingBaseUrl}/?threadId={threadId}&amp;focus=1</c>, so the hosted conversation behind it has to
/// outlive the review that produced it — that link is the whole reason the S2S path exists. What it must not
/// do is live forever: this sweeper discards a conversation once it has been reachable for the configured
/// window (default 24h), after which the link stops resolving.
/// <para>
/// <b>A ceiling, never a teardown hook.</b> Nothing here is triggered by a review finishing, a slot being
/// returned, or a PR closing. The only input is age since the conversation was minted, so a review's link is
/// live for its whole window regardless of how quickly the run ended.
/// </para>
/// <para>
/// The ledger (<c>deep_link_conversation</c>) is written at the mint choke point, so this covers the judge and
/// A/B arms too — conversations whose thread ids never reach a persisted artifact and would otherwise be
/// invisible to any policy keyed off review runs.
/// </para>
/// </summary>
internal sealed class DeepLinkRetentionSweeper
{
    private readonly ReviewStore _store;
    private readonly LmStreamingS2SClient _client;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeepLinkRetentionSweeper> _logger;

    /// <summary>
    /// Builds the sweeper. <c>retention</c> is how long a deep-link stays live after its conversation was
    /// minted, and must be positive — a zero/negative window would discard conversations the instant they are
    /// recorded, killing the link before the review that minted it has even answered. "Keep them forever" is
    /// expressed by not registering this sweeper at all, not by passing a nonsense window.
    /// </summary>
    public DeepLinkRetentionSweeper(
        ReviewStore store,
        LmStreamingS2SClient client,
        TimeSpan retention,
        ILogger<DeepLinkRetentionSweeper> logger,
        TimeProvider? timeProvider = null
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                retention,
                "The deep-link retention window must be positive; to keep conversations forever, do not "
                    + "register the sweeper."
            );
        }

        _retention = retention;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Discards every conversation minted before <c>now - retention</c>. Runs on the poller's maintenance
    /// tick, so it is naturally retried: a conversation whose delete fails (host down, transient 5xx) keeps
    /// its ledger row and is attempted again next cycle, while one the host reports as already gone (404) is
    /// dropped from the ledger — that is the state we wanted, reached by another route.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = _timeProvider.GetUtcNow() - _retention;
        var expired = _store.ListDeepLinkConversationsMintedBefore(cutoff);
        if (expired.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Discarding {Count} review deep-link conversation(s) minted before {Cutoff:O} (retention {Retention}).",
            expired.Count,
            cutoff,
            _retention
        );

        foreach (var conversation in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var deleted = await _client
                    .DeleteConversationAsync(conversation.ThreadId, cancellationToken)
                    .ConfigureAwait(false);
                _store.RemoveDeepLinkConversation(conversation.ThreadId);

                _logger.LogInformation(
                    deleted
                        ? "Discarded expired review conversation {ThreadId} ({Title}), minted {MintedAt:O}."
                        : "Expired review conversation {ThreadId} ({Title}), minted {MintedAt:O}, was already "
                            + "gone from the review host; dropped from the retention ledger.",
                    conversation.ThreadId,
                    conversation.Title ?? "untitled",
                    conversation.MintedAt
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep the row: a failed discard is a retry next cycle, not a leak. One unreachable
                // conversation must not stop the rest of the batch from being discarded.
                _logger.LogWarning(
                    ex,
                    "Could not discard expired review conversation {ThreadId}; leaving it in the retention "
                        + "ledger to retry on the next sweep.",
                    conversation.ThreadId
                );
            }
        }
    }
}
