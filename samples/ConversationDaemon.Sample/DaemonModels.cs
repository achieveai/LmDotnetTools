namespace ConversationDaemon.Sample;

/// <summary>
/// Live run state for a conversation, as reported by
/// <c>GET /api/conversations/{threadId}/run-state</c>.
/// </summary>
internal sealed record RunState(bool IsInProgress, string? CurrentRunId);

/// <summary>
/// Resolved status of a run, as reported by <c>GET /api/conversations/{threadId}/status</c>.
/// </summary>
internal sealed record StatusResult(string Status, string? RunId);

/// <summary>
/// Result of a provider switch (<c>POST /api/conversations/{threadId}/provider</c>).
/// <see cref="Warning"/> is non-null when the switch discarded a pending <c>Wait</c> armed on the
/// conversation.
/// </summary>
internal sealed record SwitchResult(string? Warning);

/// <summary>
/// Thrown when the daemon cannot establish a TCP connection to the server (the server is not
/// running). Carries the actionable guidance from
/// <see cref="DaemonMessages.ConnectionRefused(string)"/> and is caught at the top level so the
/// daemon prints a clean message instead of an unhandled socket exception.
/// </summary>
internal sealed class DaemonConnectionException : Exception
{
    public DaemonConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
