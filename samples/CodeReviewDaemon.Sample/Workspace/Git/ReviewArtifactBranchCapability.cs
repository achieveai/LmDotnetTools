using System.Globalization;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>
/// The one thing that can authorize a review-artifact branch push (task #82, requirement 1).
/// <para>
/// The daemon's steady state is <see cref="Denied"/>: retention validates, serializes and hashes its files
/// exactly as before, and then refuses before it touches git. Normal daemon startup and
/// <c>--workflow-operation</c> both construct <see cref="Denied"/>, and in THIS codebase as it stands
/// nothing constructs anything else — <see cref="Grant"/> has no production caller yet. The one-shot
/// single-PR run command that will call it (task #81's <c>--run-pr</c>) is a separate change; until it
/// lands, the pilot push is unreachable from any command line, which is the correct default.
/// </para>
/// <para>
/// <b>There is deliberately no command-line flag.</b> An earlier revision minted the grant from its own
/// <c>--artifact-branch-pilot &lt;repo&gt; &lt;pr&gt; &lt;head&gt;</c> flag, which meant two independently-typed
/// descriptions of "which run" had to agree: the operator could name one PR in the run command and a
/// different one in the grant, and any composition bug between the two parsers was a grant that outlived
/// its run. The flag is gone. The grant is to be DERIVED from the run command's own already-validated
/// repo/PR/head, so the two cannot disagree — there is nothing to mismatch and nothing to fall through.
/// </para>
/// <para>
/// It is not a write capability. <see cref="AuthorizesSourcePrWrite"/> is a constant <c>false</c> and there
/// is no member that could ever return otherwise. Comment posting stays governed by <c>EnableCommentPosting</c>
/// and <c>OperationPolicy.AllowsWriteOperations</c>, neither of which this type participates in: it is read
/// in exactly one place, <c>WorkflowArtifactOperations.RetainArtifactsAsync</c>, and it opens exactly one
/// thing, the push of this run's own derived artifact branch.
/// </para>
/// </summary>
internal sealed record ReviewArtifactBranchCapability
{
    // Each async admission carries its own grant through the existing singleton services.
    // ExecutionContext isolates concurrent runs; child workflow calls inherit only their parent's grant.
    private static readonly AsyncLocal<ReviewArtifactBranchCapability?> CurrentGrant = new();

    public static ReviewArtifactBranchCapability Current => CurrentGrant.Value ?? Denied;

    public static IDisposable EnterScope()
    {
        var previous = CurrentGrant.Value;
        CurrentGrant.Value = null;
        return new GrantScope(previous);
    }

    public static void SetCurrent(ReviewArtifactBranchCapability capability) => CurrentGrant.Value = capability;

    private sealed class GrantScope(ReviewArtifactBranchCapability? previous) : IDisposable
    {
        public void Dispose() => CurrentGrant.Value = previous;
    }

    private const int ShaLength = 40;

    /// <summary>The default posture — no push, no export, for any run.</summary>
    public static ReviewArtifactBranchCapability Denied { get; } = new();

    /// <summary>The repository the grant names, as either <c>NormalizedKey</c> or <c>DisplayName</c>.</summary>
    public string RepoKey { get; private init; } = string.Empty;

    /// <summary>The single PR id the grant names.</summary>
    public string PrId { get; private init; } = string.Empty;

    /// <summary>The single head SHA the grant names; a new push to the PR invalidates the grant.</summary>
    public string HeadSha { get; private init; } = string.Empty;

    /// <summary>Whether this instance authorizes anything at all.</summary>
    public bool IsGranted => HeadSha.Length == ShaLength;

    /// <summary>
    /// Whether the retained bundle carries the run's complete review output rather than only the fixed
    /// metadata summary (task #82, requirement 2). Tied to the grant on purpose: the wider export exists for
    /// the pilot, and the daemon's steady-state posture — canonical records stay in the private store, only
    /// fixed host metadata reaches public git — is what <see cref="Denied"/> preserves.
    /// </summary>
    public bool ExportsFullReviewOutput { get; private init; }

    /// <summary>Always false. Nothing here can be spent on the reviewed pull request.</summary>
    public bool AuthorizesSourcePrWrite => false;

    /// <summary>
    /// Mints a grant from an ALREADY-VALIDATED run identity, refusing anything that does not name exactly
    /// one run.
    /// <para>
    /// The caller contract is the whole security argument and is not checkable from here: the repo, PR and
    /// head passed in must be the ones the calling command just re-read from the provider and matched
    /// against the operator's approval — not values typed alongside it. Called from anywhere else this
    /// would be a grant for a PR nobody re-checked.
    /// </para>
    /// </summary>
    /// <param name="repoKey">The repository, as either its normalized key or its display name.</param>
    /// <param name="prId">The single PR id.</param>
    /// <param name="headSha">The single full head SHA, as freshly read from the provider.</param>
    /// <param name="exportsFullReviewOutput">
    /// Whether this grant also widens what reaches the branch. The pilot asks for the full export; the two
    /// are separate so a caller can authorize the push without also widening the export.
    /// </param>
    public static ReviewArtifactBranchCapability Grant(
        string repoKey,
        string prId,
        string headSha,
        bool exportsFullReviewOutput = true
    )
    {
        if (string.IsNullOrWhiteSpace(repoKey))
        {
            throw new ArgumentException("An artifact-branch grant must name a repository.", nameof(repoKey));
        }
        if (!int.TryParse(prId, NumberStyles.None, CultureInfo.InvariantCulture, out var prNumber) || prNumber <= 0)
        {
            throw new ArgumentException("An artifact-branch grant must name one positive PR id.", nameof(prId));
        }
        if (headSha is not { Length: ShaLength } || !headSha.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An artifact-branch grant must name one full head SHA.", nameof(headSha));
        }
        return new ReviewArtifactBranchCapability
        {
            RepoKey = repoKey,
            PrId = prId,
            HeadSha = headSha,
            ExportsFullReviewOutput = exportsFullReviewOutput,
        };
    }

    /// <summary>
    /// Whether this grant covers <paramref name="run"/> in <paramref name="repo"/>. Every axis has to
    /// match: a different repository, a different PR, or a head that moved since the operator approved it
    /// is not this run.
    /// </summary>
    public bool AuthorizesPush(RepoIdentity repo, ReviewRun run)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(run);
        return IsGranted
            && NamesRepo(repo)
            && string.Equals(PrId, run.PrId, StringComparison.Ordinal)
            && string.Equals(HeadSha, run.HeadSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The single branch this grant can reach — derived, never supplied by the operator.</summary>
    public string AuthorizedBranch(RepoIdentity repo) =>
        ReviewBranchManager.BuildReviewBranchName(repo, int.Parse(PrId, CultureInfo.InvariantCulture));

    private bool NamesRepo(RepoIdentity repo) =>
        string.Equals(RepoKey, repo.NormalizedKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(RepoKey, repo.DisplayName, StringComparison.OrdinalIgnoreCase);
}
