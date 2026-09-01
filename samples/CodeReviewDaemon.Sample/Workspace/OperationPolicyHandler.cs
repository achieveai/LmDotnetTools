using System.Text.Json;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// Raised when the <see cref="OperationPolicy"/> denies an outbound request at the daemon's HTTP seam.
/// The request is blocked before it reaches the network and its credential is stripped, so a denied
/// operation can neither make the call nor leak the bearer/basic token (plan §4, fail closed both ways).
/// </summary>
internal sealed class OperationDeniedException : Exception
{
    public OperationDeniedException(SandboxOperation operation, string reason)
        : base($"Operation '{operation}' was denied by the daemon's OperationPolicy: {reason}")
    {
        Operation = operation;
        Reason = reason;
    }

    /// <summary>The classified operation that was denied.</summary>
    public SandboxOperation Operation { get; }

    /// <summary>The policy's audit-grade rationale for the denial.</summary>
    public string Reason { get; }
}

/// <summary>
/// Tags an <see cref="HttpRequestMessage"/> with the <see cref="SandboxOperation"/> it performs, so the
/// <see cref="OperationPolicyHandler"/> can classify and enforce it. A request that reaches the handler
/// without a tag is treated as unclassified and denied (fail closed) — the daemon's providers/publishers
/// always tag their requests.
/// </summary>
internal static class OperationRequestTagging
{
    private static readonly HttpRequestOptionsKey<SandboxOperation> OperationKey = new("crd.operation");

    /// <summary>Tags <paramref name="request"/> with <paramref name="operation"/> and returns it (fluent).</summary>
    public static HttpRequestMessage WithOperation(this HttpRequestMessage request, SandboxOperation operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(OperationKey, operation);
        return request;
    }

    /// <summary>Reads the operation tag, or <c>null</c> when the request was never tagged.</summary>
    public static SandboxOperation? GetOperation(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Options.TryGetValue(OperationKey, out var operation) ? operation : null;
    }
}

/// <summary>
/// The daemon's outbound HTTP enforcement seam (plan §4). Every provider-API request the daemon issues
/// (post a review comment, read PR/repo metadata) flows through this <see cref="DelegatingHandler"/>,
/// which classifies it via the request's <see cref="SandboxOperation"/> tag and evaluates it against the
/// canonical <see cref="OperationPolicy"/>. A denied operation is BOTH egress-blocked (the request never
/// reaches the inner handler / network) AND credential-denied (the <c>Authorization</c> header is
/// stripped) — the same fail-closed-both-ways guarantee the policy makes for git transport. An
/// unclassified request (no tag) is denied rather than allowed to escape unenforced.
/// </summary>
internal sealed class OperationPolicyHandler : DelegatingHandler
{
    private readonly IReadOnlyList<OperationPolicy> _policies;
    private readonly string _provider;
    private readonly ILogger<OperationPolicyHandler> _logger;
    private readonly IPolicyRefusalRecorder? _refusals;
    private readonly GitHubGraphQlRequestScope? _canonicalGraphQlScope;

    public OperationPolicyHandler(
        OperationPolicy policy,
        string provider,
        ILogger<OperationPolicyHandler> logger,
        IPolicyRefusalRecorder? refusals = null,
        GitHubGraphQlRequestScope? canonicalGraphQlScope = null
    )
        : this(
            [policy ?? throw new ArgumentNullException(nameof(policy))],
            provider,
            logger,
            refusals,
            canonicalGraphQlScope
        ) { }

    /// <summary>
    /// Enforces a set of per-repo policies (PR #121 H2): a request is allowed when <b>any</b> policy
    /// permits it (the request matches one allow-listed repo's route), and denied only when every policy
    /// denies it. An empty set denies everything (a daemon with no allow-listed repos issues no calls).
    /// </summary>
    /// <param name="policies">One policy per allow-listed repo for this provider.</param>
    /// <param name="provider">Provider key (<c>github</c>/<c>ado</c>) stamped onto each classified request.</param>
    /// <param name="logger">Where denials are logged.</param>
    /// <param name="refusals">
    /// Where denials are recorded. Optional, and the enforcement never depends on it: a handler wired
    /// without a recorder still blocks and still strips the credential, it just leaves no durable trace.
    /// Production wiring always supplies one, because a refusal nothing recorded cannot be told apart from
    /// an attempt nobody made — and that distinction is the whole question a collect-only posture raises.
    /// </param>
    /// <param name="canonicalGraphQlScope">
    /// The one GraphQL identity (owner, repo, PR number) this client instance was built to ask about,
    /// bound once at construction and immutable afterward. <c>null</c> — the default, and what every
    /// ordinary shared client gets — makes GraphQL unconditionally denied by this handler, independently
    /// of every configured policy's own verdict: a request can never supply or widen this value, only the
    /// caller that constructed the handler can.
    /// </param>
    public OperationPolicyHandler(
        IReadOnlyList<OperationPolicy> policies,
        string provider,
        ILogger<OperationPolicyHandler> logger,
        IPolicyRefusalRecorder? refusals = null,
        GitHubGraphQlRequestScope? canonicalGraphQlScope = null
    )
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = [.. policies];
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _refusals = refusals;
        _canonicalGraphQlScope = canonicalGraphQlScope;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var operation = request.GetOperation();
        if (operation is null)
        {
            // No operation tag → unclassified. Strip the credential and fail closed.
            request.Headers.Authorization = null;
            _logger.LogWarning(
                "Blocked an untagged {Method} request to {Uri}: no SandboxOperation classification.",
                request.Method,
                request.RequestUri
            );
            RecordRefusal("(untagged)", request, "request was not classified with a SandboxOperation");
            throw new OperationDeniedException(
                SandboxOperation.ReadProviderMetadata,
                "request was not classified with a SandboxOperation"
            );
        }

        var host = request.RequestUri?.Host ?? string.Empty;
        var method = request.Method.Method;
        var path = request.RequestUri is null ? string.Empty : request.RequestUri.PathAndQuery;

        // Only a request BOTH tagged ReadProviderMetadata AND shaped like a GraphQL POST ever has its body
        // read — every other operation's content (git transport payloads, provider-API write bodies) is
        // left completely untouched, so this can never become a blanket "buffer every outbound request"
        // cost or hazard. StringContent.ReadAsStringAsync reads from its own internal buffer, so this does
        // not consume anything base.SendAsync below still needs.
        var isGraphQlBodyCandidate =
            operation.Value == SandboxOperation.ReadProviderMetadata
            && OperationPolicy.IsGraphQlPostCandidate(method, path);
        var (graphQlBody, graphQlVariables) = isGraphQlBodyCandidate
            ? await TryReadGraphQlBodyAsync(request.Content, cancellationToken).ConfigureAwait(false)
            : (null, null);

        var operationRequest = new OperationRequest(
            operation.Value,
            _provider,
            host,
            method,
            path,
            graphQlBody,
            graphQlVariables
        );

        // GraphQL's mandatory gate: this client instance's own constructor-bound canonical scope (set
        // once, at construction — see <see cref="_canonicalGraphQlScope"/>) must match the request's own
        // body exactly (owner, repo, number). The configured/allow-listed policies below only ever check
        // the body against their own owner/repo boundary — none of them carries a PR number, because the
        // same policy set is shared across every concurrent review of a repo. A client built with no
        // canonical scope (the ordinary shared client) denies every GraphQL candidate here, before any
        // policy is even consulted — no policy allow can substitute for it.
        string? graphQlReason = null;
        if (isGraphQlBodyCandidate && !ActiveGraphQlScopeMatches(_canonicalGraphQlScope, graphQlVariables))
        {
            graphQlReason = _canonicalGraphQlScope is null
                ? "this client has no canonical GraphQL scope bound; GraphQL is unconditionally denied"
                : "GraphQL request body does not match this client's canonical GraphQL scope";
        }

        // The configured/allow-listed policy set is ALWAYS what is evaluated for repo/query/write
        // authority — a request never replaces or extends which policies run, and never carries any
        // authority of its own beyond what its body says.
        //
        // Allow when ANY allow-listed repo's policy both permits AND would inject the credential; deny
        // only when every policy denies. Both halves of the fail-closed-both-ways guarantee are required.
        PolicyDecision? lastDeny = null;
        if (graphQlReason is null)
        {
            foreach (var policy in _policies)
            {
                var decision = policy.Decide(operationRequest);
                if (decision.IsAllowed && policy.ShouldInjectCredential(operationRequest))
                {
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }

                lastDeny = decision;
            }
        }

        // No policy permitted the request (or the GraphQL active-scope gate above already denied it).
        // Withhold the credential the moment it is denied, then block egress.
        request.Headers.Authorization = null;
        var reason =
            graphQlReason
            ?? (
                _policies.Count == 0
                    ? "no repository is allow-listed for this provider"
                    : lastDeny?.Reason ?? "denied by every per-repo policy"
            );
        _logger.LogWarning(
            "Denied {Operation} {Method} {Uri}: {Reason}",
            operation,
            request.Method,
            request.RequestUri,
            reason
        );
        RecordRefusal(operation.Value.ToString(), request, reason);
        throw new OperationDeniedException(operation.Value, reason);
    }

    /// <summary>
    /// The mandatory active-scope gate: <paramref name="expected"/> is this client's own
    /// constructor-bound canonical scope; <paramref name="actual"/> is what the request body itself
    /// claims. Both must be present and agree on owner, repo, AND number — a null canonical scope, an
    /// unparsed body, or any one field disagreeing denies. No configured policy can substitute for this:
    /// it carries no PR to compare.
    /// </summary>
    private static bool ActiveGraphQlScopeMatches(
        GitHubGraphQlRequestScope? expected,
        GitHubGraphQlRequestScope? actual
    ) =>
        expected is not null
        && actual is not null
        && expected.Number > 0
        && string.Equals(expected.Owner, actual.Owner, StringComparison.Ordinal)
        && string.Equals(expected.Repo, actual.Repo, StringComparison.Ordinal)
        && expected.Number == actual.Number;

    /// <summary>
    /// The largest body this reviewed-safe GraphQL document could ever legitimately produce, with generous
    /// headroom for its <c>variables</c> envelope (owner/repo/number/page-size/cursor are all short
    /// scalars). Anything bigger cannot be the safe document, so it is rejected before it is even parsed —
    /// this is a cap on what this handler will READ, not a general request-size policy.
    /// </summary>
    private const int MaxGraphQlBodyBytes = 16 * 1024;

    /// <summary>
    /// Extracts the JSON <c>"query"</c> field and, in the same bounded parse pass, the
    /// <c>variables.owner</c>/<c>repo</c>/<c>number</c> scope from a candidate GraphQL request body.
    /// <c>Query</c> is <c>null</c> on anything that stops this from being read as exactly that: no
    /// content, an oversized or unreadable body, invalid JSON, or a document without a string
    /// <c>"query"</c> property. Every one of those collapses to the same <c>null</c>, which is the only
    /// value <see cref="OperationPolicy.IsGitHubGraphQlMetadataRequest"/> can never match against — fail
    /// closed, not fail open with a best-effort guess.
    /// <para>
    /// <c>Variables</c> is parsed all-or-nothing: <c>variables</c> must be a JSON object, <c>owner</c> and
    /// <c>repo</c> must be non-empty JSON strings, and <c>number</c> must be a JSON number that fits an
    /// <c>int</c> and is positive. Any one of those missing or the wrong shape collapses <c>Variables</c>
    /// to <c>null</c> too — never a partially-populated scope built from whichever fields happened to
    /// parse.
    /// </para>
    /// <para>
    /// Read failures (a torn connection) and malformed JSON are logged at Debug with only the exception's
    /// TYPE — never the raw body or any header/credential — so an operator can tell "the read failed" from
    /// "the JSON was malformed" without this becoming a body-content leak. Cancellation is not logged and
    /// still propagates via the <c>when</c> guard below.
    /// </para>
    /// </summary>
    private async Task<(string? Query, GitHubGraphQlRequestScope? Variables)> TryReadGraphQlBodyAsync(
        HttpContent? content,
        CancellationToken cancellationToken
    )
    {
        if (content is null)
        {
            return (null, null);
        }

        if (content.Headers.ContentLength is { } declaredLength && declaredLength > MaxGraphQlBodyBytes)
        {
            return (null, null);
        }

        string raw;
        try
        {
            raw = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Failed to read a candidate GraphQL request body ({ExceptionType}).",
                ex.GetType().Name
            );
            return (null, null);
        }

        if (raw.Length > MaxGraphQlBodyBytes)
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return (null, null);
            }

            var query =
                document.RootElement.TryGetProperty("query", out var queryEl)
                && queryEl.ValueKind is JsonValueKind.String
                    ? queryEl.GetString()
                    : null;

            return (query, TryParseGraphQlScope(document.RootElement));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to parse a candidate GraphQL request body as JSON ({ExceptionType}).",
                ex.GetType().Name
            );
            return (null, null);
        }
    }

    /// <summary>
    /// Parses <c>variables.owner</c>/<c>repo</c>/<c>number</c> off an already-parsed GraphQL request
    /// body's root element. All-or-nothing: <c>null</c> unless <c>variables</c> is an object carrying a
    /// non-empty string <c>owner</c>, a non-empty string <c>repo</c>, and a positive <c>int</c>-fitting
    /// <c>number</c>.
    /// </summary>
    private static GitHubGraphQlRequestScope? TryParseGraphQlScope(JsonElement root)
    {
        if (!root.TryGetProperty("variables", out var variablesEl) || variablesEl.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (
            !variablesEl.TryGetProperty("owner", out var ownerEl)
            || ownerEl.ValueKind is not JsonValueKind.String
            || string.IsNullOrEmpty(ownerEl.GetString())
        )
        {
            return null;
        }

        if (
            !variablesEl.TryGetProperty("repo", out var repoEl)
            || repoEl.ValueKind is not JsonValueKind.String
            || string.IsNullOrEmpty(repoEl.GetString())
        )
        {
            return null;
        }

        if (
            !variablesEl.TryGetProperty("number", out var numberEl)
            || numberEl.ValueKind is not JsonValueKind.Number
            || !numberEl.TryGetInt32(out var number)
            || number <= 0
        )
        {
            return null;
        }

        return new GitHubGraphQlRequestScope(ownerEl.GetString()!, repoEl.GetString()!, number);
    }

    /// <summary>
    /// Records a denial, classifying it by the request's METHOD rather than by its operation tag. That is
    /// the point: the tag is what the caller claimed, the method is what the request would have DONE, and
    /// only the second answers "did anything try to write on a collect-only run?".
    /// </summary>
    private void RecordRefusal(string subject, HttpRequestMessage request, string reason)
    {
        if (_refusals is null)
        {
            return;
        }

        var method = request.Method.Method;
        _refusals.Record(
            new PolicyRefusalRecord(
                DateTimeOffset.UtcNow,
                OperationPolicy.IsMutatingMethod(method)
                    ? PolicyRefusalKind.ProviderWrite
                    : PolicyRefusalKind.ProviderRead,
                _provider,
                subject,
                method,
                request.RequestUri?.ToString() ?? "(no uri)",
                reason
            )
        );
    }
}
