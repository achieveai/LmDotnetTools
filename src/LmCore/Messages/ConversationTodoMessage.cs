using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     A live-only push frame carrying the conversation's whole todo board (#583, PR 2). Broadcast to the
///     run's subscribers whenever a task tool call changes the board, so the client panel updates mid-run
///     instead of only after a reload of <c>GET /conversations/{id}/todos</c>. Implements
///     <see cref="ITransientMessage" />: it is never buffered, added to history, or persisted — a
///     reconnecting client restores the authoritative board from the read endpoint.
/// </summary>
/// <remarks>
///     <para>
///         The snapshot's fields sit FLAT on the message — <c>threadId</c>, <c>schemaVersion</c>,
///         <c>capturedAtUtc</c>, <c>tasks</c> — never nested under a wrapper property: the client's frame
///         handler is written against the flat shape, mirroring how <see cref="ConversationUsageMessage" />
///         flattens the banner tuple. Always a whole board, never a delta: the board is small, and a client
///         that misses a frame must not be left reconstructing state from tool-call acks that do not carry it.
///     </para>
///     <para>
///         Field names are fixed camelCase via <see cref="JsonPropertyNameAttribute" /> so the wire shape is
///         stable regardless of the serializer's naming policy — the WebSocket channel serializes with no
///         naming policy at all, so an unpinned name would go out PascalCase.
///     </para>
/// </remarks>
public sealed record ConversationTodoMessage : IMessage, ITransientMessage
{
    /// <summary>
    ///     The conversation whose board this is. <b>Required and never omitted</b>, widening
    ///     <see cref="IMessage.ThreadId" />'s nullable declaration to a non-null guarantee here: the
    ///     client's guard drops frames whose id differs from the active conversation, and that guard is
    ///     INERT when the id is absent (#584 F-003). An optional field would let a future emitter silently
    ///     disable the guard by omission, so the type refuses to be constructed without one.
    /// </summary>
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    /// <summary>
    ///     Schema version carried through from the snapshot unchanged — it is load-bearing on the read
    ///     path (a newer version reads as absent there), so the live frame must not claim a different one.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>When the board was captured, so a reader can tell fresh from stale.</summary>
    [JsonPropertyName("capturedAtUtc")]
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Top-level rows in tree order, exactly the <c>GET /todos</c> task shape. Never null.</summary>
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TodoTaskNode> Tasks { get; init; } = [];

    /// <summary>The role associated with this frame (assistant, matching other loop-emitted messages).</summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>The name or identifier of the agent that produced this frame.</summary>
    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>Not carried on transient todo frames.</summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>Not carried on transient todo frames.</summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>
    ///     Flattens a board snapshot into the push frame. The thread id is stamped from
    ///     <paramref name="threadId" /> — the publishing loop's own id — not from the snapshot, exactly as
    ///     <see cref="ConversationUsageMessage.FromAggregate" /> stamps the usage frame, so a frame can never
    ///     claim a conversation other than the one whose subscribers receive it.
    /// </summary>
    /// <param name="snapshot">The board as captured by the conversation's task tool.</param>
    /// <param name="threadId">The publishing conversation's id. Required; never null or blank.</param>
    public static ConversationTodoMessage FromSnapshot(TodoBoardSnapshot snapshot, string threadId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // A blank id passes `required` but leaves the client's guard just as inert as an absent one, so
        // the frame refuses to exist rather than going out unroutable.
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        return new ConversationTodoMessage
        {
            ThreadId = threadId,
            SchemaVersion = snapshot.SchemaVersion,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            Tasks = snapshot.Tasks,
        };
    }
}
