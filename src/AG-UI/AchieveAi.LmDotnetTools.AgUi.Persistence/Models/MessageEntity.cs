namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Models;

/// <summary>
/// Immutable entity representing a persisted LmCore message.
/// </summary>
/// <remarks>
/// Stores complete IMessage objects as JSON for session recovery.
/// This is the primary storage mechanism - AG-UI events can be regenerated from messages.
/// </remarks>
public sealed record MessageEntity
{
    /// <summary>
    /// Gets the unique message identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the session identifier this message belongs to.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the complete LmCore message as JSON string.
    /// Stores the IMessage object in its native format for accurate recovery.
    /// </summary>
    public required string MessageJson { get; init; }

    /// <summary>
    /// Gets the message timestamp as Unix timestamp (milliseconds since epoch).
    /// </summary>
    public required long Timestamp { get; init; }

    /// <summary>
    /// Gets the message type name (e.g., "TextMessage", "ToolsCallMessage").
    /// Used for filtering and querying without deserializing JSON.
    /// </summary>
    public required string MessageType { get; init; }
}
