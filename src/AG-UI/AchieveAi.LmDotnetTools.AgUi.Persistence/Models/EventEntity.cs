namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Models;

/// <summary>
///     Immutable entity representing minimal AG-UI event metadata.
/// </summary>
/// <remarks>
///     Stores lightweight event metadata for correlation and ordering.
///     Full event content is not stored - events can be regenerated from messages.
/// </remarks>
public sealed record EventEntity
{
    /// <summary>
    ///     Gets the unique event identifier (auto-generated sequence).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Gets the session identifier this event belongs to.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    ///     Gets the minimal event data as JSON string.
    ///     Contains only metadata needed for correlation, not full event content.
    /// </summary>
    public required string EventJson { get; init; }

    /// <summary>
    ///     Gets the event timestamp as Unix timestamp (milliseconds since epoch).
    /// </summary>
    public required long Timestamp { get; init; }

    /// <summary>
    ///     Gets the event type name (e.g., "run-started", "text-chunk").
    ///     Used for filtering and querying without deserializing JSON.
    /// </summary>
    public required string EventType { get; init; }
}
