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
    /// The row's position in its thread: 1-based, dense, monotonic, assigned by the STORE on append
    /// and never by the caller (a supplied value is ignored). <c>null</c> only on a row written by a
    /// build that predates this column; the first append to such a thread backfills every legacy row
    /// in <c>(timestamp, message_order_idx)</c> order and the value never changes afterwards, not even
    /// through <see cref="IConversationStore.ReplaceMessageAsync"/>.
    /// </summary>
    /// <remarks>
    /// This is what makes "no rows were appended since I looked" an answerable question:
    /// <see cref="IConversationStore.GetMessageWatermarkAsync"/> is the highest value in the thread,
    /// and a compaction checkpoint is allowed to activate only while the watermark it captured is
    /// still current (spec 679 §2.2, §3.5). <c>(timestamp, message_order_idx)</c> could not serve —
    /// it is neither dense nor total (a clock step, two rows in one millisecond).
    /// </remarks>
    public long? Seq { get; init; }

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
