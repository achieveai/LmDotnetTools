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

    public OperationPolicyHandler(
        OperationPolicy policy,
        string provider,
        ILogger<OperationPolicyHandler> logger,
        IPolicyRefusalRecorder? refusals = null
    )
        : this([policy ?? throw new ArgumentNullException(nameof(policy))], provider, logger, refusals) { }

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
    public OperationPolicyHandler(
        IReadOnlyList<OperationPolicy> policies,
        string provider,
        ILogger<OperationPolicyHandler> logger,
        IPolicyRefusalRecorder? refusals = null
    )
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = [.. policies];
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _refusals = refusals;
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
        var graphQlBody =
            operation.Value == SandboxOperation.ReadProviderMetadata
            && OperationPolicy.IsGraphQlPostCandidate(method, path)
                ? await TryReadGraphQlQueryAsync(request.Content, cancellationToken).ConfigureAwait(false)
                : null;

        var operationRequest = new OperationRequest(operation.Value, _provider, host, method, path, graphQlBody);

        // Allow when ANY allow-listed repo's policy both permits AND would inject the credential; deny
        // only when every policy denies. Both halves of the fail-closed-both-ways guarantee are required.
        PolicyDecision? lastDeny = null;
        foreach (var policy in _policies)
        {
            var decision = policy.Decide(operationRequest);
            if (decision.IsAllowed && policy.ShouldInjectCredential(operationRequest))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            lastDeny = decision;
        }

        // No policy permitted the request. Withhold the credential the moment it is denied, then block egress.
        request.Headers.Authorization = null;
        var reason =
            _policies.Count == 0
                ? "no repository is allow-listed for this provider"
                : lastDeny?.Reason ?? "denied by every per-repo policy";
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
    /// The largest body this reviewed-safe GraphQL document could ever legitimately produce, with generous
    /// headroom for its <c>variables</c> envelope (owner/repo/number/page-size/cursor are all short
    /// scalars). Anything bigger cannot be the safe document, so it is rejected before it is even parsed —
    /// this is a cap on what this handler will READ, not a general request-size policy.
    /// </summary>
    private const int MaxGraphQlBodyBytes = 16 * 1024;

    /// <summary>
    /// Extracts the JSON <c>"query"</c> field from a candidate GraphQL request body, or <c>null</c> on
    /// anything that stops this from being read as exactly that: no content, an oversized or unreadable
    /// body, invalid JSON, or a document without a string <c>"query"</c> property. Every one of those
    /// collapses to the same <c>null</c>, which is the only value
    /// <see cref="OperationPolicy.IsGitHubGraphQlMetadataRequest"/> can never match against — fail closed,
    /// not fail open with a best-effort guess.
    /// <para>
    /// <c>variables</c> is deliberately excluded: the reader's own request body is
    /// <c>{"query": Query, "variables": {...}}</c>, and <c>variables</c> legitimately differs on every
    /// page/PR the reader ever asks about, so only <c>query</c> is a candidate for an exact-document
    /// comparison.
    /// </para>
    /// </summary>
    private static async Task<string?> TryReadGraphQlQueryAsync(
        HttpContent? content,
        CancellationToken cancellationToken
    )
    {
        if (content is null)
        {
            return null;
        }

        if (content.Headers.ContentLength is { } declaredLength && declaredLength > MaxGraphQlBodyBytes)
        {
            return null;
        }

        string raw;
        try
        {
            raw = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (raw.Length > MaxGraphQlBodyBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return
                document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("query", out var queryEl)
                && queryEl.ValueKind is JsonValueKind.String
                ? queryEl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
