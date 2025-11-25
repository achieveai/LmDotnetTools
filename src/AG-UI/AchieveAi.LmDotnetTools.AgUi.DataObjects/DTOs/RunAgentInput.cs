using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs;

/// <summary>
///     Primary request object for agent execution
/// </summary>
public record RunAgentInput
{
    /// <summary>
    ///     The user's message/input
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    ///     Conversation history (previous messages)
    /// </summary>
    [JsonPropertyName("history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableList<Message>? History { get; init; }

    /// <summary>
    ///     Additional context for the agent
    /// </summary>
    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, object>? Context { get; init; }

    /// <summary>
    ///     Configuration for this run
    /// </summary>
    [JsonPropertyName("configuration")]
    public RunConfiguration? Configuration { get; init; }

    /// <summary>
    ///     Session ID to continue existing session (null for new session)
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}
