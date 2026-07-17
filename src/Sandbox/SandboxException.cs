namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Thrown by <see cref="SandboxClient"/> for every gateway/transport failure that is NOT caller
/// cancellation (which always surfaces as a plain <see cref="OperationCanceledException"/>). Carries
/// a stable <see cref="Kind"/> so callers can branch on failure category without parsing message
/// text or depending on the underlying transport exception type.
/// </summary>
/// <remarks>
/// The message is always built from information that is safe to surface to callers/logs: the
/// gateway response status code and the local operation being attempted. It never echoes response
/// bodies (which, on an auth-rejection path, are the upstream output most likely to include
/// credential material), a JSON-RPC error's <c>message</c> (or any other error field), or any
/// <see cref="SandboxClientOptions.ClientSecret"/>-derived value.
/// </remarks>
public sealed class SandboxException : Exception
{
    /// <summary>Stable classification of the failure. See <see cref="SandboxErrorKind"/>.</summary>
    public SandboxErrorKind Kind { get; }

    /// <summary>
    /// The gateway's HTTP status code, when the failure originated from a received response.
    /// <c>null</c> when the failure is a transport-level condition with no response (e.g. the
    /// gateway was unreachable, or the client-side transport timeout elapsed before any response
    /// arrived).
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The resolved operation id of a command execution, when this failure is recoverable by re-issuing
    /// the same command with the same operation id (e.g. a <see cref="SandboxErrorKind.TransportTimeout"/>
    /// on a submitted-but-unconfirmed command). <c>null</c> for failures with no recoverable operation
    /// (control-plane calls, or failures that occur before an operation id is resolved). It is a
    /// caller-supplied or SDK-generated identifier, never secret-bearing.
    /// </summary>
    public string? OperationId { get; }

    /// <summary>
    /// The gateway's stable, machine-readable <c>error_code</c> from the direct-API error body (e.g.
    /// <c>path_not_found</c>, <c>session_not_found</c>, <c>mount_not_found</c>), when the failure came from
    /// a direct file/command/directory call that carried one. <c>null</c> for control-plane failures, auth
    /// rejections (whose body is deliberately never read), and any response with no machine-readable body.
    /// It is a closed, gateway-defined vocabulary — safe to surface and branch on, like a JSON-RPC error
    /// <c>code</c> — so a caller can distinguish, for example, a genuinely missing PATH from an evicted
    /// SESSION even though both classify as <see cref="SandboxErrorKind.NotFound"/>. Never the gateway's
    /// free-text <c>error</c> message, which is never copied into this exception. Set via object
    /// initializer at the throw site (deliberately kept OFF the constructor so its 5-argument signature
    /// stays binary-stable for already-compiled callers).
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Whether this failure is a DEFINITIVE "the path is not there" signal a best-effort caller may treat
    /// as a missing file/directory: a <see cref="SandboxErrorKind.NotFound"/> whose gateway
    /// <see cref="ErrorCode"/> is EXPLICITLY <c>path_not_found</c>. A code-less 404 is deliberately NOT
    /// treated as a missing path: the direct API also answers <c>404</c> for an evicted session/mount, so
    /// inferring "missing path" from an absent <see cref="ErrorCode"/> would mask a dead session as a
    /// silent null/empty (and could fire an unintended <c>mkdir -p</c> retry on write). The pinned gateway
    /// always stamps <c>error_code=path_not_found</c> on a genuine miss, so requiring it is exact, not
    /// speculative. An explicit eviction code (<c>session_not_found</c>/<c>mount_not_found</c>) or a
    /// code-less 404 therefore surfaces as a real <see cref="SandboxErrorKind.NotFound"/> error. This is
    /// the single shared signal both the file/directory best-effort degrade paths and the nested-write
    /// <c>mkdir -p</c> self-heal trigger on, so the two never drift.
    /// </summary>
    public bool IsDefiniteMissingPath => Kind == SandboxErrorKind.NotFound && ErrorCode == "path_not_found";

    public SandboxException(SandboxErrorKind kind, string message, int? statusCode = null, Exception? innerException = null, string? operationId = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        OperationId = operationId;
    }
}
