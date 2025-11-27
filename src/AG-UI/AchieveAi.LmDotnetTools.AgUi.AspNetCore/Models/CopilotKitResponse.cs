using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Models;

/// <summary>
///     Response model for CopilotKit client
///     Returned after initiating an agent run, provides WebSocket connection info
/// </summary>
public sealed record CopilotKitResponse
{
    /// <summary>
    ///     Session identifier for this interaction
    ///     Used to connect to the WebSocket and receive events
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    ///     Thread identifier echoed back from request
    ///     Maintains conversation continuity
    /// </summary>
    [JsonPropertyName("threadId")]
    public string ThreadId { get; init; } = string.Empty;

    /// <summary>
    ///     Run identifier echoed back from request
    ///     Tracks this specific execution
    /// </summary>
    [JsonPropertyName("runId")]
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    ///     WebSocket URL for receiving real-time events
    ///     Client should connect to this URL to stream agent responses
    /// </summary>
    [JsonPropertyName("websocketUrl")]
    public string WebSocketUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Status of the request
    ///     Values: "running", "error", "completed"
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "running";

    /// <summary>
    ///     Optional error message if status is "error"
    /// </summary>
    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    /// <summary>
    ///     Agent name that will handle this request
    /// </summary>
    [JsonPropertyName("agentName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentName { get; init; }

    /// <summary>
    ///     Timestamp when the response was created
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
