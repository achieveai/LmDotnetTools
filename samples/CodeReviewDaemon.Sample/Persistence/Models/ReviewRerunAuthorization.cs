namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// Durable, single-use evidence that an operator deleted a review-artifact branch and may therefore
/// re-review the pull request (task #82, requirement 4).
/// <para>
/// It exists because deletion and re-admission are separate intents that nonetheless have to compose. The
/// redo command never contacts the provider, so it cannot re-validate the PR's identity and must not put a
/// run back in the pipeline itself. The one-shot run command does re-read the PR, but on its own it has no
/// way to tell "this review already completed, do nothing" from "this review completed and its output was
/// deliberately destroyed, so redo it" — and the completed run looks identical in both cases. This row is
/// the missing fact, recorded by the side that established it and consumed by the side that can act on it.
/// </para>
/// <para>
/// It is written ONLY after a verified deletion: the remote ref was proven absent at the recorded SHA and
/// every retention receipt attesting to that commit was invalidated. An unknown or quarantined outcome
/// authorizes nothing, because the branch may still be published and re-running would push a second
/// artifact history onto it.
/// </para>
/// <para>
/// Nothing here mutates the run it supersedes. <see cref="PriorReviewRunId"/>, the deleted branch and its
/// SHA are all recorded alongside, so the completed run, its artifacts and its receipts remain exactly as
/// they were — the point of the redo was to remove the published copy, not the daemon's record of it.
/// </para>
/// </summary>
/// <param name="Id">Primary key; the handle a consumer passes to claim this authorization.</param>
/// <param name="RepoId">The repository the authorization is scoped to.</param>
/// <param name="PrId">The single pull request it authorizes.</param>
/// <param name="HeadSha">
/// The head the prior run reviewed. A consumer must re-read the PR and find this same head, or the
/// authorization does not describe the thing it is about to review.
/// </param>
/// <param name="BaseSha">The base the prior run reviewed, checked the same way.</param>
/// <param name="PriorReviewRunId">The completed run whose published output was deleted.</param>
/// <param name="DeletedBranch">The artifact branch that was removed.</param>
/// <param name="DeletedBranchSha">The commit that branch was proven to be at when it was removed.</param>
/// <param name="RerunWatermark">
/// The <c>trigger_watermark</c> a consumer must give the NEW run. <c>review_run</c> is unique over
/// (repo, pr, head, base, watermark, kind, variant, mode) and a rerun usually matches the prior run on
/// every column but this one, so a per-authorization watermark is what lets the new review be a new row
/// instead of colliding with — or overwriting — the old one.
/// </param>
/// <param name="CreatedAt">When the verified deletion recorded this.</param>
/// <param name="ConsumedAt">When it was claimed, or <c>null</c> while it is still outstanding.</param>
/// <param name="ConsumedByRunId">The run created from it, once claimed.</param>
internal sealed record ReviewRerunAuthorization(
    long Id,
    long RepoId,
    string PrId,
    string HeadSha,
    string BaseSha,
    long PriorReviewRunId,
    string DeletedBranch,
    string DeletedBranchSha,
    string RerunWatermark,
    string CreatedAt,
    string? ConsumedAt,
    long? ConsumedByRunId
)
{
    /// <summary>True while this authorization has not been claimed.</summary>
    public bool IsOutstanding => ConsumedAt is null;

    /// <summary>
    /// Whether a freshly-read head and base are the ones this authorization was issued against. Advisory
    /// only — the same comparison is made again inside the conditional UPDATE that claims the row, which
    /// is the one that actually decides, because only there is it atomic with the claim.
    /// </summary>
    /// <param name="headSha">The head just read from the provider.</param>
    /// <param name="baseSha">The base just read from the provider.</param>
    public bool Describes(string headSha, string baseSha) =>
        string.Equals(HeadSha, headSha, StringComparison.OrdinalIgnoreCase)
        && string.Equals(BaseSha, baseSha, StringComparison.OrdinalIgnoreCase);
}
