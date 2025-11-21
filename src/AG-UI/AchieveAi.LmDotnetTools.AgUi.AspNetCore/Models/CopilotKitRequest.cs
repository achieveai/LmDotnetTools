using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Models;

/// <summary>
/// Request model for CopilotKit client
/// Represents the incoming request from CopilotKit React frontend
/// </summary>
public sealed record CopilotKitRequest
{
    /// <summary>
    /// Thread identifier for conversation continuity
    /// Maps to a persistent conversation session
    /// </summary>
    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    /// <summary>
    /// Run identifier for this specific execution
    /// Used to track individual agent runs within a thread
    /// </summary>
    [JsonPropertyName("runId")]
    public string? RunId { get; init; }

    /// <summary>
    /// Messages in the conversation
    /// Contains user messages, assistant responses, and system prompts
    /// </summary>
    [JsonPropertyName("messages")]
    public List<CopilotKitMessage>? Messages { get; init; }

    /// <summary>
    /// Additional context for the request
    /// Can contain application-specific metadata
    /// </summary>
    [JsonPropertyName("context")]
    public object? Context { get; init; }

    /// <summary>
    /// Optional agent name to use for this request
    /// If not specified, default agent will be used
    /// </summary>
    [JsonPropertyName("agentName")]
    public string? AgentName { get; init; }
}

/// <summary>
/// Message model for CopilotKit conversations
/// Represents a single message in the chat history
/// </summary>
public sealed record CopilotKitMessage
{
    /// <summary>
    /// Role of the message sender
    /// Values: "user", "assistant", "system"
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    /// <summary>
    /// Content of the message
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Optional name of the sender
    /// Used for multi-agent scenarios or named users
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
