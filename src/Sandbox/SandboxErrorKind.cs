namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Stable classification of failures a <see cref="SandboxClient"/> operation can raise. Callers
/// switch on this instead of parsing exception messages or inspecting transport-specific
/// exception types, which keeps error handling stable even as the underlying wire details evolve.
/// Caller-initiated cancellation is deliberately NOT one of these kinds — it always surfaces as a
/// plain <see cref="OperationCanceledException"/> so cancellation-aware call sites keep working
/// with the standard .NET pattern.
/// </summary>
public enum SandboxErrorKind
{
    /// <summary>
    /// The gateway rejected the presented credential (e.g. an empty or non-empty <c>401</c>/<c>403</c>
    /// response). Distinct from <see cref="TransportTimeout"/>: the gateway was reachable and
    /// responded, it just refused the request.
    /// </summary>
    Authorization,

    /// <summary>
    /// The gateway reported the target sandbox/session does not exist. Foreign (owned by a
    /// different app id) and genuinely missing sessions both map here uniformly — the SDK never
    /// tries to distinguish the two from the response alone, so a caller cannot probe for another
    /// app's sessions by observing a different error shape.
    /// </summary>
    NotFound,

    /// <summary>
    /// The gateway-side execution deadline for a command elapsed. Raised by
    /// <see cref="SandboxClient.ExecuteAsync"/> when the gateway's Bash execution timeout elapses
    /// before the command completes — distinct from <see cref="TransportTimeout"/> (the client-side
    /// call deadline).
    /// </summary>
    ExecutionTimeout,

    /// <summary>
    /// The SDK's own HTTP/MCP transport failed: the configured <see cref="SandboxClientOptions.TransportTimeout"/>
    /// elapsed, or the gateway could not be reached at all (DNS/connection failure). Distinct from
    /// <see cref="ExecutionTimeout"/>, which is a gateway-side deadline on the remote operation
    /// itself, not the client-side transport call.
    /// </summary>
    TransportTimeout,

    /// <summary>
    /// The gateway returned a response the SDK could not make sense of: an unexpected status code,
    /// a malformed body, a missing required field, or a JSON-RPC error envelope.
    /// </summary>
    Protocol,

    /// <summary>
    /// A transferred payload failed a verification check (e.g. a length/digest mismatch). Raised by
    /// <see cref="SandboxClient.ExecuteAsync"/> when reassembled command output fails its length/SHA-256
    /// check, or when an operation id is reused with a different command (canonical digest mismatch);
    /// also reserved for later exact-byte file transfer.
    /// </summary>
    Integrity,

    /// <summary>
    /// The gateway refused a request because it conflicts with existing server-side state: an
    /// operation id reused with a different request payload (<c>409 idempotency_conflict</c>), a
    /// delete of a still-running operation (<c>409 operation_running</c>), or a write blocked by a
    /// concurrent holder of the target file (<c>409 target_locked</c>). Distinct from
    /// <see cref="Integrity"/> (a local/verification failure) — here the gateway is authoritative.
    /// </summary>
    Conflict,

    /// <summary>
    /// The gateway could not service the request right now for a reason that is not the caller's
    /// fault and may succeed on retry: the sandbox agent predates the command-operation protocol
    /// (<c>424 operation_api_unavailable</c>), a transient probe failure (<c>503
    /// operation_probe_failed</c>), a concurrency/capacity limit (<c>503 operation_concurrency_limit</c>
    /// / <c>operation_capacity_exhausted</c> / <c>too_many_concurrent_requests</c>), or a briefly
    /// quiescing session (<c>503 sandbox_busy</c>). The <c>424</c> case is NOT retryable without a
    /// newer agent image; the <c>503</c> cases are.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The operation requires a writable workspace mount the session does not have
    /// (<c>409 workspace_required</c>): operation stdout/stderr artifacts must live under a writable
    /// workspace, so a session created without one cannot host <see cref="SandboxClient.ExecuteAsync"/>.
    /// </summary>
    WorkspaceRequired,

    /// <summary>
    /// A command's combined stdout+stderr exceeded the operation's output cap and the gateway
    /// terminalized it rather than silently truncating (<c>status: output_limit_exceeded</c>). Raised
    /// by <see cref="SandboxClient.ExecuteAsync"/>; the output is intentionally not returned, since the
    /// result would be incomplete.
    /// </summary>
    OutputLimitExceeded,
}
