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

    public OperationPolicyHandler(OperationPolicy policy, string provider, ILogger<OperationPolicyHandler> logger)
        : this([policy ?? throw new ArgumentNullException(nameof(policy))], provider, logger)
    {
    }

    /// <summary>
    /// Enforces a set of per-repo policies (PR #121 H2): a request is allowed when <b>any</b> policy
    /// permits it (the request matches one allow-listed repo's route), and denied only when every policy
    /// denies it. An empty set denies everything (a daemon with no allow-listed repos issues no calls).
    /// </summary>
    public OperationPolicyHandler(
        IReadOnlyList<OperationPolicy> policies, string provider, ILogger<OperationPolicyHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = [.. policies];
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
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
                request.RequestUri);
            throw new OperationDeniedException(
                SandboxOperation.ReadProviderMetadata,
                "request was not classified with a SandboxOperation");
        }

        var operationRequest = new OperationRequest(
            operation.Value,
            _provider,
            request.RequestUri?.Host ?? string.Empty,
            request.Method.Method,
            request.RequestUri is null ? string.Empty : request.RequestUri.PathAndQuery);

        // Allow when ANY allow-listed repo's policy both permits AND would inject the credential; deny
        // only when every policy denies. Both halves of the fail-closed-both-ways guarantee are required.
        PolicyDecision? lastDeny = null;
        foreach (var policy in _policies)
        {
            var decision = policy.Decide(operationRequest);
            if (decision.IsAllowed && policy.ShouldInjectCredential(operationRequest))
            {
                return base.SendAsync(request, cancellationToken);
            }

            lastDeny = decision;
        }

        // No policy permitted the request. Withhold the credential the moment it is denied, then block egress.
        request.Headers.Authorization = null;
        var reason = _policies.Count == 0
            ? "no repository is allow-listed for this provider"
            : lastDeny?.Reason ?? "denied by every per-repo policy";
        _logger.LogWarning(
            "Denied {Operation} {Method} {Uri}: {Reason}",
            operation,
            request.Method,
            request.RequestUri,
            reason);
        throw new OperationDeniedException(operation.Value, reason);
    }
}
