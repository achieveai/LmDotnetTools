using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Task #85 — the scoped authority <see cref="HostGitCommandRunner"/> consults before executing any
/// <c>git push</c>. Replaces the all-or-nothing <c>EnableGitPush</c> boolean gate that used to sit directly
/// in the runner: that gate only ever asked "is push on at all", so a capability minted elsewhere (task
/// #82's <c>ReviewArtifactBranchCapability</c>) could authorize a run's push at the retention layer while
/// this shared boundary still rejected the literal git process wholesale under a restrictive profile — or,
/// the opposite defect, waved through ANY push once the flag was on, including one that named the wrong
/// branch or remote.
/// <para>
/// This type is mutable and late-bound by design: it is constructed once, at daemon/CLI-arm start (before
/// the identity that authorizes anything has been read), and handed by reference into every
/// <see cref="HostGitCommandRunner"/> that shares it. A grant is set on it only after the specific event
/// that earns it — the second identity revalidation for a plain push, the redo command's own full refusal
/// gate sequence for a guarded delete — so a runner constructed eagerly still sees the grant the moment it
/// is minted, the same "mutable local read fresh on each call" shape <c>artifactBranchCapability</c> already
/// relies on in <c>Program.cs</c>.
/// </para>
/// <para>
/// Two grants, kept deliberately separate so neither can stand in for the other: <see cref="AuthorizePush"/>
/// opens exactly one plain branch push (<c>git push origin &lt;branch&gt;</c>), and
/// <see cref="AuthorizeGuardedDelete"/> opens exactly one compare-and-swap delete
/// (<c>git push --force-with-lease=refs/heads/&lt;branch&gt;:&lt;sha&gt; origin --delete &lt;branch&gt;</c>).
/// A plain-push grant can never authorize a delete of any shape, and a delete grant can never authorize a
/// plain push — each is matched against its own exact argv shape in <see cref="IsAuthorized"/>. The
/// unguarded delete (<c>git push origin --delete &lt;branch&gt;</c>, used only for best-effort cleanup after
/// a successful merge) is NEVER authorized by either grant; it can only ever succeed under
/// <see cref="AllowAllPushes"/>.
/// </para>
/// </summary>
internal sealed class HostGitPushAuthorization
{
    private readonly AsyncLocal<HostGitPushAuthorization?> _scoped = new();

    public IDisposable EnterScope()
    {
        var previous = _scoped.Value;
        _scoped.Value = new HostGitPushAuthorization(AllowAllPushes);
        return new AuthorizationScope(() => _scoped.Value = previous);
    }

    private sealed class AuthorizationScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private readonly Lock _gate = new();
    private (string Branch, string Remote)? _pushGrant;
    private (string Branch, string Sha, string Remote)? _deleteGrant;

    /// <param name="allowAllPushes">
    /// The escape hatch that preserves pre-existing, unrestricted behavior for any profile that has not
    /// opted into the narrower default (sourced from <c>CodeReviewDaemonOptions.EnableGitPush</c>). When
    /// <c>true</c>, <see cref="IsAuthorized"/> returns <c>true</c> unconditionally and the two grants below
    /// are never consulted.
    /// </param>
    public HostGitPushAuthorization(bool allowAllPushes) => AllowAllPushes = allowAllPushes;

    /// <summary>
    /// Whether every push is authorized unconditionally, bypassing both scoped grants below. Fixed at
    /// construction — an operator flag, not something a run mints for itself.
    /// </summary>
    public bool AllowAllPushes { get; }

    /// <summary>
    /// Authorizes exactly one plain branch push, <c>git push &lt;remote&gt; &lt;branch&gt;</c> — the shape
    /// <see cref="ReviewBranchManager"/> uses both for the review-artifact-branch push and for the
    /// default-branch merge push, distinguished only by which branch is named here. Replaces any earlier
    /// grant: only the most recently authorized branch is ever open, matching the single mint site in
    /// <c>Program.cs</c>'s <c>onIdentityRevalidated</c> callback.
    /// </summary>
    public void AuthorizePush(string branch, string remote = "origin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        if (_scoped.Value is { } scoped)
        {
            scoped.AuthorizePush(branch, remote);
            return;
        }
        lock (_gate)
        {
            _pushGrant = (branch, remote);
        }
    }

    /// <summary>
    /// Authorizes exactly one guarded (compare-and-swap) delete, the shape
    /// <see cref="ReviewBranchManager.DeleteReviewBranchAsync"/> uses. The caller — currently only
    /// <c>RedoReviewArtifactBranchCommand</c> — must call this ONLY after its own ownership/SHA/lock/
    /// quarantine refusal gates have already passed; this type has no way to enforce that sequencing
    /// itself and trusts its one caller to hold the obligation.
    /// </summary>
    public void AuthorizeGuardedDelete(string branch, string expectedSha, string remote = "origin")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        lock (_gate)
        {
            _deleteGrant = (branch, expectedSha, remote);
        }
    }

    /// <summary>
    /// Whether the tokens AFTER the <c>push</c> verb (as <see cref="HostGitCommandRunner"/> receives them,
    /// with the shared <c>-c</c> hardening/identity flags already stripped) match a live grant. Fails
    /// closed: an unrecognized shape, a mismatched branch/remote/SHA, or the unguarded delete form are all
    /// refused unless <see cref="AllowAllPushes"/> is set.
    /// </summary>
    public bool IsAuthorized(IReadOnlyList<string> pushArgs)
    {
        ArgumentNullException.ThrowIfNull(pushArgs);
        if (_scoped.Value is { } scoped)
        {
            return scoped.IsAuthorized(pushArgs);
        }
        if (AllowAllPushes)
        {
            return true;
        }

        (string Branch, string Remote)? pushGrant;
        (string Branch, string Sha, string Remote)? deleteGrant;
        lock (_gate)
        {
            pushGrant = _pushGrant;
            deleteGrant = _deleteGrant;
        }

        // Guarded delete: ["--force-with-lease=refs/heads/<branch>:<sha>", "<remote>", "--delete", "<branch>"].
        // Checked first and on an exact 4-token shape so it can never be confused with the 2-token plain push
        // below, regardless of grant order.
        if (
            deleteGrant is { } delete
            && pushArgs.Count == 4
            && string.Equals(
                pushArgs[0],
                ReviewBranchManager.DeleteExpectedShaArgument(delete.Branch, delete.Sha),
                StringComparison.Ordinal
            )
            && string.Equals(pushArgs[1], delete.Remote, StringComparison.Ordinal)
            && string.Equals(pushArgs[2], "--delete", StringComparison.Ordinal)
            && string.Equals(pushArgs[3], delete.Branch, StringComparison.Ordinal)
        )
        {
            return true;
        }

        // Plain branch push: ["<remote>", "<branch>"] — exactly two tokens. Neither the unguarded delete
        // (3 tokens: "<remote>", "--delete", "<branch>") nor the guarded delete (4 tokens) can ever match
        // this shape, so a push grant can never double as authorization for either delete form.
        if (
            pushGrant is { } push
            && pushArgs.Count == 2
            && string.Equals(pushArgs[0], push.Remote, StringComparison.Ordinal)
            && string.Equals(pushArgs[1], push.Branch, StringComparison.Ordinal)
        )
        {
            return true;
        }

        return false;
    }
}
