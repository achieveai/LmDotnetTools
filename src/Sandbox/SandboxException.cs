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

    public SandboxException(SandboxErrorKind kind, string message, int? statusCode = null, Exception? innerException = null, string? operationId = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        OperationId = operationId;
    }
}
