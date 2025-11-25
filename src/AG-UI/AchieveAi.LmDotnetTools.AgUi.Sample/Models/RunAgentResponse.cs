namespace AchieveAi.LmDotnetTools.AgUi.Sample.Models;

/// <summary>
///     Response model for agent execution
/// </summary>
public class RunAgentResponse
{
    /// <summary>
    ///     Session ID for this conversation
    ///     Use this to continue the conversation
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    ///     Agent that handled the request
    /// </summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    ///     Status of the execution
    /// </summary>
    public string Status { get; set; } = "success";

    /// <summary>
    ///     Any error message if status is "error"
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     WebSocket URL for streaming events
    /// </summary>
    public string? WebSocketUrl { get; set; }

    /// <summary>
    ///     Timestamp of the response
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
