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
    string Path
);

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
    IReadOnlyList<SubmoduleAllowRule> AllowedSubmodules
)
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
    /// The provider-API route roots outside <see cref="ApiRepoPathPrefix"/> this run may READ to establish
    /// what its PR was ASKED to do (ADO: the work-item batch route, walked up the
    /// <c>System.LinkTypes.Hierarchy-Reverse</c> chain to the Epic). Empty for GitHub, whose linked issues
    /// hang off the repo route already in scope.
    /// <para>
    /// One root, and deliberately only one. The PR's own list of linked items
    /// (<c>_apis/git/repositories/{repo}/pullRequests/{id}/workitems</c>) already sits UNDER
    /// <see cref="ApiRepoPathPrefix"/> and needed nothing added; only the work items THEMSELVES
    /// (<c>_apis/wit/workitems</c>) are project-scoped and unreachable from a per-repo prefix, because ADO
    /// keys work items to a PROJECT and not to the repository a PR happens to live in.
    /// </para>
    /// <para>
    /// Without it the reviewer cannot judge whether a diff does what was asked, and the gap was structural
    /// rather than a model choice: the capability was offered to the reviewer in its PROMPT, which told it to
    /// dispatch a context gatherer, while across 644 observed review sub-agent spawns ZERO carried any tool
    /// that could reach ADO. It was dispatched once in 698 spawns, and that one had nothing to do the job with.
    /// </para>
    /// <para>
    /// Each entry is a route ROOT, not the project's <c>_apis</c> surface: <c>_apis/wit/wiql</c>,
    /// <c>_apis/wit/queries</c> and every other <c>wit</c> sibling stay outside it, because
    /// <see cref="OperationPolicy"/> matches a root at a directory boundary. Scoped to exactly one project
    /// (the run's) and honoured only for the read-only <see cref="SandboxOperation.ReadProviderMetadata"/>
    /// arm — widening the method or the project would hand back exactly what the repo confinement protects.
    /// READ only: no work item can be created, updated, commented on or linked through this, because the
    /// write arm never passes the flag that makes these roots reachable at all.
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
    /// <summary>
    /// GitHub's sole GraphQL endpoint. Every GraphQL request — reads included — is an HTTP POST, which is
    /// why <see cref="SandboxOperation.ReadProviderMetadata"/>'s otherwise GET-only arm needs a named
    /// exception for exactly this path rather than a blanket allowance for any POST.
    /// </summary>
    private const string GitHubGraphQlPath = "/graphql";

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
    /// <para>
    /// The same flag also carries the daemon's COLLECT-ONLY posture (<c>EnableCommentPosting</c>): a run the
    /// operator has not authorized to post gets <c>false</c> here, which is what turns "the daemon declines
    /// to call the publisher" into "the daemon cannot make a provider write at all". The two meanings
    /// coincide deliberately — both are "this policy has no write capability" — so there is one switch to
    /// reason about rather than two that can disagree.
    /// </para>
    /// <para>
    /// The default is <c>false</c>: a caller that says nothing about write capability gets none. The grant
    /// is what has to be spelled out, because the failure mode of the opposite default is silent — a new
    /// construction site inherits full write capability and nothing in the code reads as wrong.
    /// </para>
    /// </param>
    public OperationPolicy(ReviewScope scope, bool allowWriteOperations = false)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _allowWriteOperations = allowWriteOperations;
    }

    /// <summary>Whether this policy grants provider-API / ReviewBot write operations at all.</summary>
    public bool AllowsWriteOperations => _allowWriteOperations;

    /// <summary>Evaluates an outbound request. Unknown shapes fall through to a deny.</summary>
    public PolicyDecision Decide(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The collect-only METHOD gate, evaluated before the operation is dispatched at all.
        //
        // The per-operation arms below already deny PostReviewComment without the capability, so for the
        // operations that exist today this is belt over braces. It is here because the failure it guards is
        // not "PostReviewComment was allowed" — it is "a request that mutates the PR arrived under some
        // OTHER classification". ReadProviderMetadata is the live example: it is reachable from the
        // read-only project/CI/work-item route exceptions, and its own arm rejects a non-GET only because
        // DecideApi is told to expect GET. A future operation added to this enum inherits nothing from that
        // — it would reach the provider API with whatever method its author passed, and a collect-only run
        // would be silently write-capable again.
        //
        // Scoped to the provider-API operations on purpose: git transport legitimately POSTs
        // (git-upload-pack is a POST), so a blanket method ban would break every fetch. The distinction is
        // the operation's ARM, not the host — on ADO the API host and the git host are the same name.
        if (
            !_allowWriteOperations
            && IsProviderApiOperation(request.Operation)
            && IsMutatingMethod(request.Method)
            && !IsGitHubGraphQlMetadataRequest(request)
        )
        {
            return PolicyDecision.Deny(
                $"this policy is collect-only and has no provider-API write capability; refusing "
                    + $"{request.Method.ToUpperInvariant()} '{StripQuery(request.Path)}' "
                    + $"({request.Operation})"
            );
        }

        return request.Operation switch
        {
            SandboxOperation.FetchTarget => DecideUploadPack(
                request,
                _scope.TargetHost,
                _scope.TargetRepoPath,
                "target repository"
            ),

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
                : DecideApi(request, "POST", "post review comment", allowReadOnlyProjectRoutes: false),

            // GitHub's linked-issues read (issue #647) is GraphQL, and GraphQL is POST by protocol — the
            // one carved-out route the read-only arm still has to recognize as a read.
            SandboxOperation.ReadProviderMetadata => IsGitHubGraphQlMetadataRequest(request)
                ? PolicyDecision.Allow($"read provider metadata (GraphQL) on '{_scope.ApiHost}'")
                : DecideApi(request, "GET", "read provider metadata", allowReadOnlyProjectRoutes: true),

            _ => PolicyDecision.Deny($"unknown operation '{request.Operation}'"),
        };
    }

    /// <summary>
    /// Whether a credential may be injected for this request. Deliberately identical to
    /// <see cref="Decide"/> so a denied operation can never be credential-injected (fail closed
    /// both ways, plan §4).
    /// </summary>
    public bool ShouldInjectCredential(OperationRequest request) => Decide(request).IsAllowed;

    /// <summary>
    /// The operations that address the provider REST API (as opposed to git transport). Enumerated
    /// positively so adding a case to <see cref="SandboxOperation"/> forces a decision here rather than
    /// defaulting into "not an API operation, therefore ungated".
    /// </summary>
    private static bool IsProviderApiOperation(SandboxOperation operation) =>
        operation switch
        {
            SandboxOperation.PostReviewComment or SandboxOperation.ReadProviderMetadata => true,
            SandboxOperation.FetchTarget
            or SandboxOperation.FetchForkHead
            or SandboxOperation.FetchSubmodule
            or SandboxOperation.PushReviewBot => false,
            // An operation nobody classified is treated as API-addressing, which is the conservative side of
            // this predicate: it only ever makes the collect-only gate FIRE, never makes it stand down.
            _ => true,
        };

    /// <summary>
    /// Whether <paramref name="method"/> mutates provider-side state. Anything that is not one of the
    /// read methods counts, so an unfamiliar verb is treated as a write.
    /// </summary>
    public static bool IsMutatingMethod(string method) =>
        !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    private PolicyDecision DecideUploadPack(
        OperationRequest request,
        string expectedHost,
        string expectedRepoPath,
        string label
    )
    {
        if (!HostMatches(request.Host, expectedHost))
        {
            return PolicyDecision.Deny($"host '{request.Host}' is not the {label} host '{expectedHost}'");
        }

        if (!PathUnderRepo(request.Path, expectedRepoPath))
        {
            return PolicyDecision.Deny($"path '{request.Path}' is outside the {label} '{expectedRepoPath}'");
        }

        var service = ClassifyGitService(request.Path);
        if (service != GitService.UploadPack)
        {
            return PolicyDecision.Deny($"only fetch (git-upload-pack) is permitted on the {label}; got {service}");
        }

        return PolicyDecision.Allow($"fetch from {label} '{expectedRepoPath}'");
    }

    private PolicyDecision DecideReceivePack(OperationRequest request, string expectedHost, string expectedRepoPath)
    {
        if (!HostMatches(request.Host, expectedHost))
        {
            return PolicyDecision.Deny($"push host '{request.Host}' is not the ReviewBot host '{expectedHost}'");
        }

        if (!PathUnderRepo(request.Path, expectedRepoPath))
        {
            return PolicyDecision.Deny(
                $"push path '{request.Path}' is outside the ReviewBot repo '{expectedRepoPath}'"
            );
        }

        var service = ClassifyGitService(request.Path);
        if (service != GitService.ReceivePack)
        {
            return PolicyDecision.Deny(
                $"only push (git-receive-pack) is permitted on the ReviewBot repo; got {service}"
            );
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
                        $"only fetch is permitted on submodule '{rule.RepoPath}'; got {service}"
                    );
                }

                return PolicyDecision.Allow($"fetch allow-listed submodule '{rule.RepoPath}'");
            }
        }

        return PolicyDecision.Deny($"submodule '{request.Host}{StripQuery(request.Path)}' is not on the allow-list");
    }

    /// <summary>
    /// Whether <paramref name="request"/> targets GitHub's GraphQL metadata endpoint (a POST to
    /// <c>/graphql</c> on the run's API host, for the GitHub provider). Checks provider/host/method/path
    /// only, deliberately not the operation — the caller (the
    /// <see cref="SandboxOperation.ReadProviderMetadata"/> arm, and the collect-only gate above it) is what
    /// restricts which operation may reach this exception, so a write operation routed through some other
    /// classification cannot bootstrap itself into it here.
    /// <para>
    /// The provider check matters on its own: an ADO run's <see cref="ReviewScope.ApiHost"/> is never
    /// literally <c>"/graphql"</c>-shaped, but nothing stops a same-shaped POST from being misclassified
    /// under a differently-provider-scoped policy in a multi-repo host — this check is what keeps the
    /// carve-out GitHub-only rather than "any provider whose API host happens to answer a POST to
    /// <c>/graphql</c>".
    /// </para>
    /// <para>
    /// This is a transport-level classification only: provider, host, method, and path are all this policy
    /// layer ever sees. It cannot inspect the GraphQL request body, so it cannot verify which repository's
    /// issues a query actually asks about — that identity is established by the caller's own scoped token
    /// and query variables, not by anything checked here.
    /// </para>
    /// </summary>
    private bool IsGitHubGraphQlMetadataRequest(OperationRequest request) =>
        string.Equals(request.Provider, "github", StringComparison.Ordinal)
        && HostMatches(request.Host, _scope.ApiHost)
        && string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
        && string.Equals(StripQuery(request.Path), GitHubGraphQlPath, StringComparison.Ordinal);

    /// <summary>
    /// Evaluates a provider-API request. <paramref name="allowReadOnlyProjectRoutes"/> lets the run's
    /// project-scoped exception — <see cref="ReviewScope.ApiWorkItemPaths"/> — count as an in-scope route
    /// alongside the repo prefix; it is passed only by the read-only
    /// <see cref="SandboxOperation.ReadProviderMetadata"/> arm, so no write can ever reach it.
    /// </summary>
    private PolicyDecision DecideApi(
        OperationRequest request,
        string expectedMethod,
        string label,
        bool allowReadOnlyProjectRoutes
    )
    {
        if (!HostMatches(request.Host, _scope.ApiHost))
        {
            return PolicyDecision.Deny($"host '{request.Host}' is not the provider API host '{_scope.ApiHost}'");
        }

        if (!string.Equals(request.Method, expectedMethod, StringComparison.OrdinalIgnoreCase))
        {
            return PolicyDecision.Deny($"{label} requires {expectedMethod}; got {request.Method}");
        }

        // When the concrete repo route is known (per-run policy, PR #121 H2), the request path must fall
        // under it — host + method alone are not enough, or a review could hit a sibling repo's API. The
        // run's OWN project-scoped read routes are the only exception (see ApiWorkItemPaths): ADO publishes
        // work items nowhere else, and a reviewer that cannot tell whether the diff does what was asked is a
        // reviewer the review is wrong without.
        if (
            _scope.ApiRepoPathPrefix is { } prefix
            && !PathUnderApiPrefix(request.Path, prefix)
            && !IsReadOnlyProjectRoute(request.Path, allowReadOnlyProjectRoutes)
        )
        {
            return PolicyDecision.Deny(
                $"{label} path '{StripQuery(request.Path)}' is outside the run's API route '{prefix}'"
            );
        }

        return PolicyDecision.Allow($"{label} on '{_scope.ApiHost}'");
    }

    /// <summary>
    /// Whether <paramref name="requestPath"/> targets one of the run's own project-scoped READ routes. Always
    /// false when <paramref name="allowed"/> is false, which is what keeps the exception unreachable from the
    /// write arm regardless of how the route roots are configured.
    /// </summary>
    private bool IsReadOnlyProjectRoute(string requestPath, bool allowed)
    {
        if (!allowed)
        {
            return false;
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
            StringComparison.Ordinal
        );

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
        return normalizedPath.StartsWith(prefix, StringComparison.Ordinal)
            && SuffixIsAPlainGitEndpoint(normalizedPath[prefix.Length..]);
    }

    /// <summary>
    /// Screens what follows the matched <c>{repo}.git/</c> prefix. The literal <c>..</c> test above only
    /// sees the bytes THIS process holds, and this process deliberately never decodes — but the upstream
    /// server does, so <c>%2e%2e</c> written into an attacker's <c>.gitmodules</c> sails past a byte-exact
    /// prefix match and then escapes the allow-listed repo once dev.azure.com/github.com decodes it, with
    /// the daemon's credential attached (<see cref="ShouldInjectCredential"/> mirrors
    /// <see cref="Decide"/>). Decoding here to compare would reintroduce exactly the hazard the no-decode
    /// stance exists to avoid, so the escape is REFUSED instead.
    /// <para>
    /// Refusing every <c>%</c> — not a blocklist of <c>%2e</c>/<c>%2f</c>/<c>%5c</c> and their
    /// double-encodings — is what makes this safe-closed, and it costs nothing because the suffix is a
    /// CLOSED set: <c>ClassifyGitService</c> already requires the path (query stripped) to end in
    /// <c>info/refs</c>, <c>git-upload-pack</c>, or <c>git-receive-pack</c>, and both producers spell
    /// exactly those — <c>SubmoduleInitializer.DecideFetch</c> appends the literal
    /// <c>.git/info/refs?service=git-upload-pack</c>, and <c>OperationPolicyHandler</c> passes
    /// <c>Uri.PathAndQuery</c>, which .NET has already decoded and dot-segment-compressed. No legitimate
    /// suffix contains a percent-escape, a backslash, or an empty segment. Percent-encoded NAMES (a spaced
    /// Azure DevOps org/project/repo, <c>Microsoft%20Orleans</c>) live entirely in the PREFIX, which is
    /// compared byte-exactly against the operator-built rule and is untouched by this screen.
    /// </para>
    /// </summary>
    private static bool SuffixIsAPlainGitEndpoint(string suffix) =>
        !suffix.Contains('%', StringComparison.Ordinal)
        && !suffix.Contains('\\', StringComparison.Ordinal)
        && !suffix.Contains("//", StringComparison.Ordinal);

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
