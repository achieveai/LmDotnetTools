using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Models;

/// <summary>
///     Standard AG-UI protocol request model
///     Represents the incoming request from AG-UI compatible clients
/// </summary>
public sealed record AgUiRequest
{
    /// <summary>
    ///     Thread identifier for conversation continuity
    ///     Maps to a persistent conversation session
    /// </summary>
    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    /// <summary>
    ///     Run identifier for this specific execution
    ///     Used to track individual agent runs within a thread
    /// </summary>
    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    /// <summary>
    ///     Messages in the conversation
    ///     Array of message objects with role and content
    /// </summary>
    [JsonPropertyName("messages")]
    public List<AgUiMessage>? Messages { get; init; }

    /// <summary>
    ///     Optional agent identifier to select which agent to use
    /// </summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>
    ///     Optional tool definitions for function calling
    /// </summary>
    [JsonPropertyName("tools")]
    public List<object>? Tools { get; init; }
}

/// <summary>
///     Message model for AG-UI conversations
///     Represents a single message in the chat history
/// </summary>
public sealed record AgUiMessage
{
    /// <summary>
    ///     Role of the message sender
    ///     Values: "user", "assistant", "system", "tool"
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>
    ///     Content of the message
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    ///     Optional name of the sender
    ///     Used for multi-agent scenarios or named users
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
