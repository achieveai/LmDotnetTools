namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Models;

/// <summary>
/// Immutable entity representing a persisted AG-UI session.
/// </summary>
/// <remarks>
/// Sessions track conversation state, timing, and metadata across WebSocket connections.
/// The Status field follows the lifecycle: Started → Active → Completed/Failed.
/// </remarks>
public sealed record SessionEntity
{
    /// <summary>
    /// Gets the unique session identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the optional conversation identifier for grouping related sessions.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Gets the session start time as Unix timestamp (milliseconds since epoch).
    /// </summary>
    public required long StartTime { get; init; }

    /// <summary>
    /// Gets the session end time as Unix timestamp (milliseconds since epoch).
    /// Null if session is still active.
    /// </summary>
    public long? EndTime { get; init; }

    /// <summary>
    /// Gets the session status: "Started", "Active", "Completed", or "Failed".
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the optional metadata as JSON string.
    /// Stores session-specific configuration, user context, etc.
    /// </summary>
    public string? MetadataJson { get; init; }
}
