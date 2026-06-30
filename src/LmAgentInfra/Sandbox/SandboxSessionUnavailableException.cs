namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a Workspace Agent sandbox session cannot be created — either the gateway returned a
/// non-success status (e.g. a rejected network policy) or it could not be reached / started. Derives
/// from <see cref="InvalidOperationException"/> so existing callers that catch the broader type keep
/// working, while the WebSocket layer can catch this specific type to surface a clean, structured
/// client error (the <c>error-banner</c>) instead of crashing the connection with an unhandled 500.
/// </summary>
public sealed class SandboxSessionUnavailableException : InvalidOperationException
{
    public SandboxSessionUnavailableException(
        string workspaceId,
        int? statusCode,
        string message,
        Exception? inner = null
    )
        : base(message, inner)
    {
        WorkspaceId = workspaceId;
        StatusCode = statusCode;
    }

    /// <summary>The workspace whose sandbox session could not be created.</summary>
    public string WorkspaceId { get; }

    /// <summary>
    /// The gateway HTTP status when the failure was a non-success response; <c>null</c> when the
    /// gateway was unreachable or could not be started.
    /// </summary>
    public int? StatusCode { get; }
}
