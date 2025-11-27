namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Services;

/// <summary>
///     Service for mapping between CopilotKit's threadId/runId and AG-UI's sessionId
///     Maintains bidirectional mapping to enable conversation continuity
/// </summary>
public interface ICopilotKitSessionMapper
{
    /// <summary>
    ///     Create or resume a session based on threadId and runId
    /// </summary>
    /// <param name="threadId">Optional thread identifier from CopilotKit</param>
    /// <param name="runId">Optional run identifier from CopilotKit</param>
    /// <returns>Session identifier for AG-UI</returns>
    string CreateOrResumeSession(string? threadId, string? runId);

    /// <summary>
    ///     Get thread information for a given session
    /// </summary>
    /// <param name="sessionId">AG-UI session identifier</param>
    /// <returns>Tuple of threadId and runId, or null if not found</returns>
    (string? ThreadId, string? RunId)? GetThreadInfo(string sessionId);

    /// <summary>
    ///     Update the runId for an existing session
    ///     Used when a new run is started in the same thread
    /// </summary>
    /// <param name="sessionId">AG-UI session identifier</param>
    /// <param name="runId">New run identifier</param>
    void UpdateRunId(string sessionId, string runId);

    /// <summary>
    ///     Remove session mapping
    /// </summary>
    /// <param name="sessionId">Session identifier to remove</param>
    /// <returns>True if session was found and removed</returns>
    bool RemoveSession(string sessionId);

    /// <summary>
    ///     Get session ID for a given thread, if it exists
    /// </summary>
    /// <param name="threadId">Thread identifier</param>
    /// <returns>Session ID if found, null otherwise</returns>
    string? GetSessionByThread(string threadId);
}
