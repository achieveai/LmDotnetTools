using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Queue operation event representing user message submission (enqueue) or acceptance (dequeue).
/// </summary>
public record QueueOperationEvent : JsonlEventBase
{
    /// <summary>
    ///     The operation type: "enqueue" or "dequeue"
    /// </summary>
    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    /// <summary>
    ///     Timestamp of the operation
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; init; }

    /// <summary>
    ///     Content blocks (only present for enqueue operations)
    /// </summary>
    [JsonPropertyName("content")]
    public List<ContentBlock>? Content { get; init; }

    /// <summary>
    ///     Session identifier
    /// </summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
}
