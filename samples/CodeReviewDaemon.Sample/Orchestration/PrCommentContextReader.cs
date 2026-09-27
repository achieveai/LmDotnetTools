using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Reads the exact provider-comment baseline used to freeze a discussion window (plan §11) — the same
/// reader-lookup + own-id + <see cref="PrPollingService.BuildCommentContext"/> sequence
/// <see cref="PrPollingService"/>'s polling loop uses. Extracted so <see cref="RunSinglePrCommand"/>'s
/// operator-approved single-PR pilot run captures the REAL existing comment baseline instead of an empty
/// one (second security-review round, item 1): without this, every comment already on the PR would look
/// "new" on the very next poll, because nothing in the frozen context would say otherwise.
/// </summary>
internal static class PrCommentContextReader
{
    /// <summary>Publication operations whose confirmed effect receipts mark a provider comment as OUR OWN
    /// reply/summary — excluded from the discussion window the same way for every caller (plan §11).</summary>
    public static readonly IReadOnlyList<string> PublicationOperations =
    [
        ReviewPoster.PostReviewCommentOperation,
        ReviewParkNotifier.PostParkNoticeOperation,
        "workflow-publish-summary",
        "workflow-publish-inline",
        "workflow-publish-reply",
    ];

    public static async Task<JsonObject> ReadAsync(
        IReadOnlyList<IReviewCommentReader> commentReaders,
        ReviewStore store,
        RepoIdentity repo,
        string provider,
        long repoId,
        string prId,
        PullRequestDescriptor descriptor,
        IReadOnlyDictionary<string, string> previousVersions,
        CancellationToken cancellationToken
    )
    {
        var reader =
            commentReaders.SingleOrDefault(value =>
                string.Equals(value.Provider, provider, StringComparison.OrdinalIgnoreCase)
            ) ?? throw new InvalidOperationException($"No review comment reader is registered for '{provider}'.");
        var comments = await reader
            .ListExistingReviewCommentsAsync(new ReviewCommentTarget(repo, prId), cancellationToken)
            .ConfigureAwait(false);
        var ownIds = store.GetConfirmedProviderResponseIds(repoId, prId, PublicationOperations);
        return PrPollingService.BuildCommentContext(descriptor, comments, ownIds, previousVersions);
    }
}
