using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     Status of a single row on the conversation's todo board. A structural mirror of the task-tool's
///     own status enum, declared here so the read path (pool, projection, endpoint, client) shares one
///     wire vocabulary without any of those layers referencing the tool assembly.
/// </summary>
/// <remarks>
///     Serialized as the member NAME, not an ordinal: the client's type union is written against
///     <c>"NotStarted" | "InProgress" | "Completed" | "Removed" | "Blocked"</c>, and an ordinal
///     would silently re-map every row the day a member is inserted rather than appended.
///     <c>Blocked</c> was appended after the other four for exactly that reason — the client's
///     type union does not yet list it (tracked as #584), but the client's parser already has a
///     tested fallback for an unrecognized status name, so an older client degrades gracefully
///     rather than breaking.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoTaskStatus
{
    /// <summary>Not picked up yet.</summary>
    NotStarted,

    /// <summary>Actively being worked.</summary>
    InProgress,

    /// <summary>Finished.</summary>
    Completed,

    /// <summary>Struck out — kept for history, grouped away in the UI.</summary>
    Removed,

    /// <summary>Waiting on one or more other tasks before it can be claimed. Appended rather than
    /// inserted so existing ordinals — irrelevant to the name-based wire format, but still a
    /// convention worth keeping — never shift.</summary>
    Blocked,
}

/// <summary>
///     One row of the board, including its sub-rows. Ordering is the tree's own order and is never
///     re-sorted: the board must not shuffle under a reader who is watching it.
/// </summary>
/// <remarks>
///     Property names are fixed camelCase via <see cref="JsonPropertyNameAttribute" /> because these rows
///     travel two channels with different serializer settings: the REST endpoint (camelCase naming policy)
///     and, from PR 2, the pushed <c>conversation_todo</c> frame, whose WebSocket serializer applies no
///     naming policy at all. Without the pins the same row would go out camelCase on one channel and
///     PascalCase on the other, and the client's parser is written against exactly one shape. The snapshot
///     ROOT is deliberately left unpinned: it never crosses the WebSocket (the frame flattens its fields),
///     and the projection's forward-compat probe reads the root's <c>SchemaVersion</c> key by its unpinned
///     name.
/// </remarks>
public sealed record TodoTaskNode
{
    /// <summary>Dotted-path identifier — <c>"1"</c>, <c>"1.2"</c>, <c>"1.2.3"</c>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The row's status.</summary>
    [JsonPropertyName("status")]
    public required TodoTaskStatus Status { get; init; }

    /// <summary>The row's title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Short shared status lines attached to the row. Never null; empty when there are none.</summary>
    [JsonPropertyName("notes")]
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    ///     Workspace-relative file paths attached to the row via <c>attach-artifact</c> (#583, PR 5).
    ///     Never host paths: the tool boundary validates and normalizes before anything reaches this
    ///     type, so what goes onto the wire is exactly what the file-browser preview endpoint accepts.
    ///     Never null; empty when there are none. Additive to schema version 1 — a snapshot persisted
    ///     before this field existed reads back with it simply empty, and the client tolerates both
    ///     shapes, so the version is deliberately not bumped.
    /// </summary>
    [JsonPropertyName("artifacts")]
    public IReadOnlyList<string> Artifacts { get; init; } = [];

    /// <summary>
    ///     Dotted-path ids of the tasks blocking this row, exactly as <c>block-task</c> recorded
    ///     them (#595). Persisted so a <c>Blocked</c> row keeps its force across a restart: without
    ///     the list, rehydration produced a row that rendered as Blocked while every claim guard
    ///     passed. Meaningful only alongside <see cref="TodoTaskStatus.Blocked" />; empty otherwise.
    ///     Never null. Additive to schema version 1, following <see cref="Artifacts" />'s precedent —
    ///     a snapshot persisted before this field existed reads back with it simply empty, and the
    ///     client tolerates unknown fields, so the version is deliberately not bumped.
    /// </summary>
    [JsonPropertyName("blockedBy")]
    public IReadOnlyList<string> BlockedBy { get; init; } = [];

    /// <summary>
    ///     Agent name holding or owning this row, exactly as <c>claim-task</c>/<c>assign-task</c>
    ///     recorded it (#595, D2). Persisted so a claim survives agent recreation: without it, a
    ///     rehydrated <see cref="TodoTaskStatus.InProgress" /> row arrived assignee-less and the very
    ///     agent that claimed it could no longer complete it (<c>task_not_claimed</c>). Null when
    ///     unassigned. Additive to schema version 1, same contract as <see cref="BlockedBy" />.
    /// </summary>
    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    /// <summary>When the row was created. Null on rows persisted before timestamps round-tripped.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    ///     When the current claim was taken or last refreshed (#595, D2). Persisted alongside
    ///     <see cref="Assignee" /> so lease staleness keeps meaning something across a restart —
    ///     an absent value would otherwise make every rehydrated lease read as freshly claimed and
    ///     never go stale. Null when the row is not claimed.
    /// </summary>
    [JsonPropertyName("claimedAt")]
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>When the row was completed. Null unless the row is completed.</summary>
    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Nested rows. Never null; empty when the row is a leaf.</summary>
    [JsonPropertyName("subTasks")]
    public IReadOnlyList<TodoTaskNode> SubTasks { get; init; } = [];
}

/// <summary>
///     The whole board for one conversation at one instant — the read projection behind
///     <c>GET /api/conversations/{threadId}/todos</c> and (from PR 2) the pushed
///     <c>conversation_todo</c> frame.
/// </summary>
/// <remarks>
///     A snapshot, deliberately not a delta: the board is small, and a client that misses a frame must
///     never be left reconstructing state from acks the mutating tools do not emit.
/// </remarks>
public sealed record TodoBoardSnapshot
{
    /// <summary>The conversation these rows belong to.</summary>
    public required string ThreadId { get; init; }

    /// <summary>Schema version of the serialized snapshot; a newer version reads as absent.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>When the snapshot was taken, so a reader can tell fresh from stale.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Top-level rows, in tree order. Never null.</summary>
    public IReadOnlyList<TodoTaskNode> Tasks { get; init; } = [];

    /// <summary>
    ///     Dotted ids this board is known to have held that were <b>absent</b> when the snapshot was
    ///     taken, each mapped to the instant it was last known present (#621 Part B). Empty on a healthy
    ///     board — an entry here is a recorded row loss, not a deleted row: an id deliberately removed by
    ///     <c>delete-task</c> leaves the board's id ledger entirely and never appears.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the only part of the id ledger worth persisting. For every id that IS on the board,
    ///         "last known present" is exactly <see cref="CapturedAtUtc" />, so the ledger rebuilds from
    ///         <see cref="Tasks" /> on rehydration; the ids the board no longer holds are the ones that
    ///         would otherwise be forgotten across a restart, taking with them the only evidence that
    ///         separates a vanished row from an id the agent invented.
    ///     </para>
    ///     <para>
    ///         Additive to schema version 1, following <see cref="TodoTaskNode.Artifacts" />'s precedent:
    ///         a snapshot persisted before this field existed reads back with it simply empty, so the
    ///         version is deliberately not bumped. It never crosses the WebSocket —
    ///         <c>ConversationTodoMessage.FromSnapshot</c> flattens the frame to id/version/captured-at/tasks
    ///         — but it DOES ride the <c>GET /todos</c> response body, where the client's parser ignores it
    ///         as it ignores any unknown field.
    ///     </para>
    /// </remarks>
    public IReadOnlyDictionary<string, DateTimeOffset> MissingTaskIds { get; init; } =
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

    /// <summary>True when the board carries no rows at all — nothing worth showing or persisting.</summary>
    [JsonIgnore]
    public bool IsEmpty => Tasks.Count == 0;
}
