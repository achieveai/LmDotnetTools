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
}
