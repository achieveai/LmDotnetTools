namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The canonical set of network operations the daemon may perform on behalf of a review. This is the
/// single enforcement vocabulary shared by the sandbox network rules and the webhook token resolver
/// (plan §4): a request is matched to exactly one operation, and the same decision governs both
/// whether the outbound request is allowed <i>and</i> whether a credential is injected.
/// </summary>
internal enum SandboxOperation
{
    /// <summary>Fetch (clone/pull) the target repository being reviewed. Read-only (upload-pack).</summary>
    FetchTarget,

    /// <summary>Fetch the head commit of a fork PR from the fork remote. Read-only (upload-pack).</summary>
    FetchForkHead,

    /// <summary>Fetch an allow-listed submodule. Read-only (upload-pack).</summary>
    FetchSubmodule,

    /// <summary>Post a review comment via the provider API. Write (POST).</summary>
    PostReviewComment,

    /// <summary>Push review artifacts to the ReviewBot repository. Write (receive-pack).</summary>
    PushReviewBot,

    /// <summary>Read PR/repository metadata via the provider API. Read-only (GET).</summary>
    ReadProviderMetadata,
}

/// <summary>Whether an operation request is permitted.</summary>
internal enum PolicyOutcome
{
    Deny = 0,
    Allow = 1,
}

/// <summary>The result of evaluating an <see cref="OperationRequest"/> against the policy.</summary>
/// <param name="Outcome">Allow or deny.</param>
/// <param name="Reason">Human-readable rationale, suitable for an audit log.</param>
internal sealed record PolicyDecision(PolicyOutcome Outcome, string Reason)
{
    public bool IsAllowed => Outcome == PolicyOutcome.Allow;

    public static PolicyDecision Allow(string reason) => new(PolicyOutcome.Allow, reason);

    public static PolicyDecision Deny(string reason) => new(PolicyOutcome.Deny, reason);
}

/// <summary>
/// A single outbound request to be evaluated. <paramref name="Path"/> is the URL path component and
/// may carry a query string (e.g. <c>/owner/repo.git/info/refs?service=git-upload-pack</c>) so git
/// smart-HTTP can be classified.
/// </summary>
internal sealed record OperationRequest(
    SandboxOperation Operation,
    string Provider,
    string Host,
    string Method,
    string Path);

/// <summary>
/// An allow-listed submodule destination: the host plus the repository path prefix that may be
/// fetched. Both are matched case-insensitively after normalization.
/// </summary>
internal sealed record SubmoduleAllowRule(string Host, string RepoPath);

/// <summary>
/// The concrete identities for one review run that the <see cref="OperationPolicy"/> matches against.
/// Built per run from the PR + ReviewBot configuration so the policy is scoped to exactly the repos
/// this review legitimately touches — nothing else is reachable or credential-injected.
/// </summary>
internal sealed record ReviewScope(
    string Provider,
    string TargetHost,
    string TargetRepoPath,
    string? ForkHost,
    string? ForkRepoPath,
    string ReviewBotHost,
    string ReviewBotRepoPath,
    string ApiHost,
    IReadOnlyList<SubmoduleAllowRule> AllowedSubmodules)
{
    /// <summary>
    /// The path prefix every provider-API request for this run must fall under (e.g.
    /// <c>/repos/acme/widgets/</c> for GitHub, <c>/contoso/Platform/_apis/git/repositories/core/</c> for
    /// ADO). When non-<c>null</c>, <see cref="OperationPolicy.Decide"/> validates the request's path is
    /// under it, so a review can never coax the daemon into an <i>off-repo</i> API route with the bot
    /// credential (PR #121 H2). When <c>null</c> only host + method are checked (the host-only seam used
    /// where the concrete repo route is not yet known).
    /// </summary>
    public string? ApiRepoPathPrefix { get; init; }

    /// <summary>
    /// The one provider-API route outside <see cref="ApiRepoPathPrefix"/> this run may READ: its own
    /// project's metadata (ADO <c>/{org}/_apis/projects/{project}</c>). It exists because ADO's PR-list
    /// payload omits <c>repository.project.visibility</c>, so the confidentiality trust signal the
    /// cross-repo sibling gate depends on can only be established from the org-scoped project API — which
    /// by construction cannot sit under a per-repo prefix. <c>null</c> for GitHub, which has no project
    /// layer and therefore nothing to except.
    /// <para>
    /// Scoped to exactly one project and honoured only for the read-only
    /// <see cref="SandboxOperation.ReadProviderMetadata"/>. Widening either would hand back what the repo
    /// confinement exists to protect: that untrusted PR code cannot steer the daemon to another project's
    /// route, or to a write, carrying the bot credential.
    /// </para>
    /// </summary>
    public string? ApiProjectMetadataPath { get; init; }

    /// <summary>
    /// The provider-API route roots outside <see cref="ApiRepoPathPrefix"/> this run may READ to establish
    /// its PR's CI verdict (ADO: the policy evaluation that names the build, the build itself plus its
    /// timeline, and the build's test summary). They exist for the same structural reason
    /// <see cref="ApiProjectMetadataPath"/> does: ADO publishes builds and test results per PROJECT
    /// (<c>/{org}/{project}/_apis/build/…</c>), never under a repository, so by construction they cannot sit
    /// under a per-repo prefix. Empty for GitHub, whose check-runs hang off the repo route already in scope.
    /// <para>
    /// Without them the reviewer cannot see CI at all, and the cost is not theoretical: PR 5505458's pipeline
    /// reported 45,051 tests with 1 failure — named down to <c>TagService.UnitTests</c> — while the review it
    /// produced said nothing about it and cited the PR's own commit message for build health instead.
    /// </para>
    /// <para>
    /// Each entry is a route ROOT, not the project's <c>_apis</c> surface: another repo's blobs are still out
    /// of scope. Scoped to exactly one project (the run's) and honoured only for the read-only
    /// <see cref="SandboxOperation.ReadProviderMetadata"/>, on the same terms as
    /// <see cref="ApiProjectMetadataPath"/> — widening the method or the project would hand back exactly what
    /// the repo confinement protects.
    /// </para>
    /// <para>
    /// This paragraph used to name a work-item query as an example of what stays out of scope. That stopped
    /// being true when <see cref="ApiWorkItemPaths"/> was added, and the sentence was corrected rather than
    /// left standing: a comment that misdescribes a security boundary is worse than no comment, because it is
    /// what the next reader checks INSTEAD of the code.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ApiCiStatusPaths { get; init; } = [];

    /// <summary>
    /// The provider-API route roots outside <see cref="ApiRepoPathPrefix"/> this run may READ to establish
    /// what its PR was ASKED to do (ADO: the work-item batch route, walked up the
    /// <c>System.LinkTypes.Hierarchy-Reverse</c> chain to the Epic). Empty for GitHub, whose linked issues
    /// hang off the repo route already in scope.
    /// <para>
    /// One root, and deliberately only one. The PR's own list of linked items
    /// (<c>_apis/git/repositories/{repo}/pullRequests/{id}/workitems</c>) already sits UNDER
    /// <see cref="ApiRepoPathPrefix"/> and needed nothing added; only the work items THEMSELVES
    /// (<c>_apis/wit/workitems</c>) are project-scoped and unreachable from a per-repo prefix, exactly as
    /// build results are.
    /// </para>
    /// <para>
    /// Without it the reviewer cannot judge whether a diff does what was asked, and the gap was structural
    /// rather than a model choice: the capability was offered to the reviewer in its PROMPT, which told it to
    /// dispatch a context gatherer, while across 644 observed review sub-agent spawns ZERO carried any tool
    /// that could reach ADO. It was dispatched once in 698 spawns, and that one had nothing to do the job with.
    /// </para>
    /// <para>
    /// Scoped to exactly one project (the run's) and honoured only for the read-only
    /// <see cref="SandboxOperation.ReadProviderMetadata"/>, on the same terms as the two above. READ only: no
    /// work item can be created, updated, commented on or linked through this, because the write arm never
    /// passes the flag that makes these roots reachable at all.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ApiWorkItemPaths { get; init; } = [];
}

/// <summary>
/// Fail-closed authorization for every network operation the daemon performs while reviewing
/// untrusted PR code (plan §4). One <see cref="OperationPolicy"/> instance is the shared source of
/// truth: a denied operation both blocks the outbound request <b>and</b> withholds the credential
/// (never "credential omitted but request allowed", never "request blocked but credential leaked").
/// </summary>
internal sealed class OperationPolicy
{
    private readonly ReviewScope _scope;
    private readonly bool _allowWriteOperations;

    /// <param name="scope">The repos this review may legitimately touch.</param>
    /// <param name="allowWriteOperations">
    /// Whether this policy grants the two write operations (<see cref="SandboxOperation.PushReviewBot"/>
    /// and <see cref="SandboxOperation.PostReviewComment"/>). The primary variant gets <c>true</c>; an
    /// A/B comparison (B) variant is collect-only and gets <c>false</c>, which makes push and post a
    /// <b>hard capability denial</b> regardless of host/path (plan §5) — and because
    /// <see cref="ShouldInjectCredential"/> mirrors <see cref="Decide"/>, the B variant is also never
    /// handed a write credential (fail closed both ways).
    /// </param>
    public OperationPolicy(ReviewScope scope, bool allowWriteOperations = true)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _allowWriteOperations = allowWriteOperations;
    }

    /// <summary>Evaluates an outbound request. Unknown shapes fall through to a deny.</summary>
    public PolicyDecision Decide(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Operation switch
        {
            SandboxOperation.FetchTarget => DecideUploadPack(
                request,
                _scope.TargetHost,
                _scope.TargetRepoPath,
                "target repository"),

            SandboxOperation.FetchForkHead => _scope.ForkHost is null || _scope.ForkRepoPath is null
                ? PolicyDecision.Deny("no fork remote is in scope for this review")
                : DecideUploadPack(request, _scope.ForkHost, _scope.ForkRepoPath, "fork remote"),

            SandboxOperation.FetchSubmodule => DecideSubmodule(request),

            // Write operations are gated by the capability FIRST: a collect-only (B) variant is denied
            // before any host/path is even considered, so isolation cannot be defeated by a scope quirk.
            SandboxOperation.PushReviewBot => !_allowWriteOperations
                ? PolicyDecision.Deny("this variant is collect-only and has no push capability")
                : DecideReceivePack(request, _scope.ReviewBotHost, _scope.ReviewBotRepoPath),

            SandboxOperation.PostReviewComment => !_allowWriteOperations
                ? PolicyDecision.Deny("this variant is collect-only and has no post capability")
                : DecideApi(request, "POST", "post review comment"),

            SandboxOperation.ReadProviderMetadata => DecideApi(
                request, "GET", "read provider metadata", allowReadOnlyProjectRoutes: true),

            _ => PolicyDecision.Deny($"unknown operation '{request.Operation}'"),
        };
    }

    /// <summary>
    /// Whether a credential may be injected for this request. Deliberately identical to
    /// <see cref="Decide"/> so a denied operation can never be credential-injected (fail closed
    /// both ways, plan §4).
    /// </summary>
    public bool ShouldInjectCredential(OperationRequest request) => Decide(request).IsAllowed;

    private PolicyDecision DecideUploadPack(
        OperationRequest request,
        string expectedHost,
        string expectedRepoPath,
        string label
    )
    {
        if (!HostMatches(request.Host, expectedHost))
        {
            return PolicyDecision.Deny(
                $"host '{request.Host}' is not the {label} host '{expectedHost}'");
        }

        if (!PathUnderRepo(request.Path, expectedRepoPath))
        {
            return PolicyDecision.Deny(
                $"path '{request.Path}' is outside the {label} '{expectedRepoPath}'");
        }

        var service = ClassifyGitService(request.Path);
        if (service != GitService.UploadPack)
        {
            return PolicyDecision.Deny(
                $"only fetch (git-upload-pack) is permitted on the {label}; got {service}");
        }

        return PolicyDecision.Allow($"fetch from {label} '{expectedRepoPath}'");
    }

    private PolicyDecision DecideReceivePack(
        OperationRequest request,
        string expectedHost,
        string expectedRepoPath
    )
    {
        if (!HostMatches(request.Host, expectedHost))
        {
            return PolicyDecision.Deny(
                $"push host '{request.Host}' is not the ReviewBot host '{expectedHost}'");
        }

        if (!PathUnderRepo(request.Path, expectedRepoPath))
        {
            return PolicyDecision.Deny(
                $"push path '{request.Path}' is outside the ReviewBot repo '{expectedRepoPath}'");
        }

        var service = ClassifyGitService(request.Path);
        if (service != GitService.ReceivePack)
        {
            return PolicyDecision.Deny(
                $"only push (git-receive-pack) is permitted on the ReviewBot repo; got {service}");
        }

        return PolicyDecision.Allow($"push to ReviewBot repo '{expectedRepoPath}'");
    }

    private PolicyDecision DecideSubmodule(OperationRequest request)
    {
        foreach (var rule in _scope.AllowedSubmodules)
        {
            if (HostMatches(request.Host, rule.Host) && PathUnderRepo(request.Path, rule.RepoPath))
            {
                var service = ClassifyGitService(request.Path);
                if (service != GitService.UploadPack)
                {
                    return PolicyDecision.Deny(
                        $"only fetch is permitted on submodule '{rule.RepoPath}'; got {service}");
                }

                return PolicyDecision.Allow($"fetch allow-listed submodule '{rule.RepoPath}'");
            }
        }

        return PolicyDecision.Deny(
            $"submodule '{request.Host}{StripQuery(request.Path)}' is not on the allow-list");
    }

    /// <summary>
    /// Evaluates a provider-API request. <paramref name="allowReadOnlyProjectRoutes"/> lets the three
    /// project-scoped exceptions — <see cref="ReviewScope.ApiProjectMetadataPath"/>,
    /// <see cref="ReviewScope.ApiCiStatusPaths"/> and <see cref="ReviewScope.ApiWorkItemPaths"/> — count as
    /// in-scope routes alongside the repo prefix; it is passed only by the read-only
    /// <see cref="SandboxOperation.ReadProviderMetadata"/> arm, so no write can ever reach them.
    /// </summary>
    private PolicyDecision DecideApi(
        OperationRequest request,
        string expectedMethod,
        string label,
        bool allowReadOnlyProjectRoutes = false)
    {
        if (!HostMatches(request.Host, _scope.ApiHost))
        {
            return PolicyDecision.Deny(
                $"host '{request.Host}' is not the provider API host '{_scope.ApiHost}'");
        }

        if (!string.Equals(request.Method, expectedMethod, StringComparison.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny(
                $"{label} requires {expectedMethod}; got {request.Method}");
        }

        // When the concrete repo route is known (per-run policy, PR #121 H2), the request path must fall
        // under it — host + method alone are not enough, or a review could hit a sibling repo's API. The
        // run's OWN project-scoped read routes are the only exceptions (see ApiProjectMetadataPath,
        // ApiCiStatusPaths and ApiWorkItemPaths): ADO publishes project visibility, build/test results and
        // work items nowhere else, and all three are things the review is wrong without — a closed cross-repo
        // gate in the first case, a reviewer that cannot see a failing pipeline in the second, and one that
        // cannot tell whether the diff does what was asked in the third.
        if (_scope.ApiRepoPathPrefix is { } prefix
            && !PathUnderApiPrefix(request.Path, prefix)
            && !IsReadOnlyProjectRoute(request.Path, allowReadOnlyProjectRoutes))
        {
            return PolicyDecision.Deny(
                $"{label} path '{StripQuery(request.Path)}' is outside the run's API route '{prefix}'");
        }

        return PolicyDecision.Allow($"{label} on '{_scope.ApiHost}'");
    }

    /// <summary>
    /// True when <paramref name="requestPath"/> is one of the run's own project-scoped READ exceptions.
    /// Returns false outright when <paramref name="allowed"/> is false, which is what keeps the exceptions
    /// unreachable from the write arm no matter what the scope carries.
    /// </summary>
    private bool IsReadOnlyProjectRoute(string requestPath, bool allowed)
    {
        if (!allowed)
        {
            return false;
        }

        if (_scope.ApiProjectMetadataPath is { } projectRoute
            && PathUnderApiPrefix(requestPath, projectRoute))
        {
            return true;
        }

        foreach (var ciRoute in _scope.ApiCiStatusPaths)
        {
            if (PathUnderApiPrefix(requestPath, ciRoute))
            {
                return true;
            }
        }

        foreach (var workItemRoute in _scope.ApiWorkItemPaths)
        {
            if (PathUnderApiPrefix(requestPath, workItemRoute))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="requestPath"/> targets the run's own provider-API route (begins with
    /// <paramref name="apiPrefix"/> after normalization). Rejects path traversal and a sibling whose
    /// name merely shares the prefix.
    /// </summary>
    private static bool PathUnderApiPrefix(string requestPath, string apiPrefix)
    {
        // Decode BEFORE the traversal check: decoding is what turns '%2e%2e' into '..', so checking the
        // raw text would inspect a string in which the traversal is still hidden.
        var normalizedPath = PathCanonicalizer.NormalizePathForComparison(StripQuery(requestPath));
        if (normalizedPath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedPrefix = PathCanonicalizer.NormalizePathForComparison(apiPrefix);
        if (!normalizedPrefix.StartsWith('/'))
        {
            normalizedPrefix = "/" + normalizedPrefix;
        }

        // A trailing slash makes the prefix a directory boundary: '/repos/acme/widgets/' matches
        // '/repos/acme/widgets/pulls' but not '/repos/acme/widgets-2/pulls'. The bare route itself
        // (prefix without the trailing slash) is also a legal target.
        var withSlash = normalizedPrefix.EndsWith('/') ? normalizedPrefix : normalizedPrefix + "/";
        var bare = withSlash[..^1];
        return normalizedPath.StartsWith(withSlash, StringComparison.Ordinal)
            || string.Equals(normalizedPath, bare, StringComparison.Ordinal);
    }

    private static bool HostMatches(string actual, string expected) =>
        string.Equals(
            PathCanonicalizer.NormalizeForComparison(actual),
            PathCanonicalizer.NormalizeForComparison(expected),
            StringComparison.Ordinal);

    /// <summary>
    /// True when the request path targets the git smart-HTTP endpoints of <paramref name="repoPath"/>
    /// (i.e. begins with <c>{repoPath}.git/</c> after normalization). Rejects path traversal and any
    /// path that merely has the repo as a prefix of a longer sibling name.
    /// </summary>
    private static bool PathUnderRepo(string requestPath, string repoPath)
    {
        // Decode BEFORE the traversal check, for the reason spelled out on
        // PathCanonicalizer.NormalizePathForComparison: the decode is what turns '%2e%2e' into '..'.
        var normalizedPath = PathCanonicalizer.NormalizePathForComparison(StripQuery(requestPath));
        if (normalizedPath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedRepo = PathCanonicalizer.NormalizePathForComparison(repoPath);
        if (!normalizedRepo.StartsWith('/'))
        {
            normalizedRepo = "/" + normalizedRepo;
        }

        var prefix = normalizedRepo + ".git/";
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string StripQuery(string path)
    {
        var q = path.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? path : path[..q];
    }

    private enum GitService
    {
        None,
        UploadPack,
        ReceivePack,
    }

    private static GitService ClassifyGitService(string path)
    {
        var lower = path.ToLowerInvariant();
        var pathOnly = StripQuery(lower);

        if (pathOnly.EndsWith("/git-upload-pack", StringComparison.Ordinal))
        {
            return GitService.UploadPack;
        }

        if (pathOnly.EndsWith("/git-receive-pack", StringComparison.Ordinal))
        {
            return GitService.ReceivePack;
        }

        if (pathOnly.EndsWith("/info/refs", StringComparison.Ordinal))
        {
            if (lower.Contains("service=git-upload-pack", StringComparison.Ordinal))
            {
                return GitService.UploadPack;
            }

            if (lower.Contains("service=git-receive-pack", StringComparison.Ordinal))
            {
                return GitService.ReceivePack;
            }
        }

        return GitService.None;
    }
}
