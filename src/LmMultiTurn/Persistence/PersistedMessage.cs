namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Flattened message model for persistence.
/// Contains all IMessage properties plus the serialized message JSON.
/// </summary>
public sealed record PersistedMessage
{
    /// <summary>
    /// Unique message identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Thread identifier for conversation continuity.
    /// </summary>
    public required string ThreadId { get; init; }

    /// <summary>
    /// Run identifier for this specific execution.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Parent run identifier for branching/time travel (git-like lineage).
    /// </summary>
    public string? ParentRunId { get; init; }

    /// <summary>
    /// Generation identifier - all messages in the same turn share this.
    /// </summary>
    public string? GenerationId { get; init; }

    /// <summary>
    /// Order index of this message within its generation.
    /// </summary>
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    /// Unix timestamp in milliseconds when the message was created.
    /// </summary>
    public required long Timestamp { get; init; }

    /// <summary>
    /// The concrete message type name (e.g., "TextMessage", "ToolCallMessage").
    /// </summary>
    public required string MessageType { get; init; }

    /// <summary>
    /// Message role (User, Assistant, System, Tool).
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// The agent that produced this message, if any.
    /// </summary>
    public string? FromAgent { get; init; }

    /// <summary>
    /// The full IMessage serialized as JSON.
    /// Use MessagePersistenceConverter to deserialize back to IMessage.
    /// </summary>
    public required string MessageJson { get; init; }
}
