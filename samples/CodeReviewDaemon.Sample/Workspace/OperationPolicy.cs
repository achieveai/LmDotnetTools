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

            SandboxOperation.ReadProviderMetadata => DecideApi(request, "GET", "read provider metadata"),

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

    private PolicyDecision DecideApi(OperationRequest request, string expectedMethod, string label)
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
        // under it — host + method alone are not enough, or a review could hit a sibling repo's API.
        if (_scope.ApiRepoPathPrefix is { } prefix && !PathUnderApiPrefix(request.Path, prefix))
        {
            return PolicyDecision.Deny(
                $"{label} path '{StripQuery(request.Path)}' is outside the run's API route '{prefix}'");
        }

        return PolicyDecision.Allow($"{label} on '{_scope.ApiHost}'");
    }

    /// <summary>
    /// True when <paramref name="requestPath"/> targets the run's own provider-API route (begins with
    /// <paramref name="apiPrefix"/> after normalization). Rejects path traversal and a sibling whose
    /// name merely shares the prefix.
    /// </summary>
    private static bool PathUnderApiPrefix(string requestPath, string apiPrefix)
    {
        var path = StripQuery(requestPath);
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedPath = PathCanonicalizer.NormalizeForComparison(path);
        var normalizedPrefix = PathCanonicalizer.NormalizeForComparison(apiPrefix);
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
        var path = StripQuery(requestPath);
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedPath = PathCanonicalizer.NormalizeForComparison(path);
        var normalizedRepo = PathCanonicalizer.NormalizeForComparison(repoPath);
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
