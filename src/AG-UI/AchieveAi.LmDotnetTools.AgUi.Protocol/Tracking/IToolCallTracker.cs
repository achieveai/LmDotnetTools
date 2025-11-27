namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Tracking;

/// <summary>
///     Tracks tool calls and manages ID mapping between LmCore and AG-UI
/// </summary>
public interface IToolCallTracker
{
    /// <summary>
    ///     Gets or creates an AG-UI tool call ID for an LmCore tool call ID
    /// </summary>
    /// <param name="lmCoreToolCallId">LmCore tool call ID</param>
    /// <returns>AG-UI tool call ID</returns>
    string GetOrCreateToolCallId(string? lmCoreToolCallId);

    /// <summary>
    ///     Gets the AG-UI tool call ID for an LmCore tool call ID (if it exists)
    /// </summary>
    /// <param name="lmCoreToolCallId">LmCore tool call ID</param>
    /// <returns>AG-UI tool call ID, or the original ID if not found</returns>
    string GetToolCallId(string? lmCoreToolCallId);

    /// <summary>
    ///     Tracks when a tool call starts
    /// </summary>
    /// <param name="toolCallId">Tool call ID</param>
    /// <param name="toolName">Name of the tool</param>
    void StartToolCall(string toolCallId, string toolName);

    /// <summary>
    ///     Tracks when a tool call ends
    /// </summary>
    /// <param name="toolCallId">Tool call ID</param>
    /// <returns>Duration of the tool call</returns>
    TimeSpan EndToolCall(string toolCallId);
}
