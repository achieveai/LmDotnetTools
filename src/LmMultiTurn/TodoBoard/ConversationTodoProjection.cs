using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;

/// <summary>
///     Persists and reads a conversation's todo board inside <see cref="ThreadMetadata.Properties" /> —
///     zero schema migration, uniform across every <see cref="IConversationStore" /> backend, exactly as
///     <c>ConversationUsageProjection</c> does for the usage banner.
/// </summary>
/// <remarks>
///     <para>
///         The snapshot is stored as a JSON <b>string</b> so it round-trips identically whether the backing
///         store keeps native CLR objects (in-memory) or re-hydrates property-bag values as
///         <see cref="JsonElement" /> (file / SQLite).
///     </para>
///     <para>
///         Reads are tolerant by construction: a corrupt or newer-schema blob reads as <b>absent</b> and
///         never throws. The board is a convenience view of work in flight — a bad blob must not turn
///         every subsequent read of that conversation into a 500.
///     </para>
/// </remarks>
public static class ConversationTodoProjection
{
    /// <summary>The metadata property-bag key under which the board snapshot JSON is stored.</summary>
    public const string PropertyKey = "todo.board";

    /// <summary>Highest board schema version this build understands; newer is treated as absent.</summary>
    private const int CurrentSchemaVersion = 1;

    /// <summary>
    ///     Atomically persists <paramref name="snapshot" /> into the conversation's metadata property bag.
    ///     Two guards, both applied under the store's write serialization so the read-compare-write cannot
    ///     interleave:
    ///     <list type="bullet">
    ///         <item>
    ///             A projection written by a <b>newer schema version</b> is never overwritten, so an older
    ///             build during a rollback / mixed-version deployment preserves forward-compatible data.
    ///         </item>
    ///         <item>
    ///             A snapshot captured <b>strictly earlier</b> than the persisted one is dropped, so a slow
    ///             write racing a fresh one cannot regress the board to an older state.
    ///         </item>
    ///     </list>
    ///     Note what is deliberately <i>not</i> guarded: an empty board is persisted like any other, because
    ///     "the agent cleared every row" is a real state. Whether a given empty snapshot means "cleared" or
    ///     merely "this process has not seen the board yet" is knowable only at the call site, so that
    ///     policy lives there rather than being guessed at here.
    ///     <para>
    ///         <b>Never creates a conversation.</b> A board is an attribute of a conversation that already
    ///         exists; if no metadata row is present this is a no-op. See the guard below for why an
    ///         auto-created row would be unreadable by everyone.
    ///     </para>
    /// </summary>
    public static async Task SaveAsync(
        IConversationStore store,
        TodoBoardSnapshot snapshot,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshot);

        // NEVER bring a conversation's metadata row into existence. A row minted here would carry no
        // TenantId / OwnerUserId / OwnerAppId / Visibility, and ConversationAuthorizer reads a null
        // TenantId as conversation_not_found — an unstamped row is one NOBODY can read, for anyone.
        // The board's only writers are a read path (GET /todos) and, from PR 2, a change notification;
        // neither is entitled to create a conversation. A pooled agent has already persisted its row
        // via MultiTurnAgentPool.PersistThreadBindingsIfNeededAsync, so on the live-board path this
        // costs one lookup and skips nothing real. Absent row => the board is simply not persisted.
        if (await store.LoadMetadataAsync(snapshot.ThreadId, ct) is null)
        {
            return;
        }

        // Depth bound (#608): the board model is unlimited-depth and no code in the projection or
        // writer caps it — this Serialize call is where the practical bound lives. System.Text.Json's
        // default MaxDepth (64) applies to the whole snapshot document, so a board nested deeper than
        // roughly 60 task levels throws JsonException here instead of persisting. Deliberately left
        // at the default rather than raised; requirements.md Req 1.3 states the same bound.
        var json = JsonSerializer.Serialize(snapshot);

        await store.UpdateMetadataAsync(
            snapshot.ThreadId,
            existing =>
            {
                // Re-checked INSIDE the store's write serialization, because the row can be deleted
                // between the probe above and this callback — the delete-then-read resurrection race
                // (Delete evicts the pool entry before removing the row, so a GET that captured the
                // live board pre-eviction can arrive here post-delete carrying the very task titles the
                // delete was meant to remove).
                //
                // Throwing is the only way to decline: every IConversationStore.UpdateMetadataAsync
                // persists whatever this callback returns, so there is no "no-op" value to hand back.
                // All three implementations invoke the callback BEFORE their write, so the throw
                // guarantees nothing is written. Callers treat a failed board save as non-fatal.
                // The dedicated type lets a caller distinguish this deliberate, final decline from a
                // store-infrastructure fault that happens to be an InvalidOperationException subtype.
                if (existing is null)
                {
                    throw new TodoBoardDeclinedException(
                        $"Conversation '{snapshot.ThreadId}' no longer exists; refusing to recreate its "
                            + "metadata row to persist a todo board."
                    );
                }

                // Forward-compatibility: refuse to overwrite a projection a newer build wrote.
                if (PersistedSchemaVersion(existing) > CurrentSchemaVersion)
                {
                    return existing;
                }

                // Monotonic in capture time. Equal instants are ACCEPTED, not rejected: at coarse clock
                // resolution successive captures routinely share a tick, and treating those as stale would
                // silently drop every write that lands inside one tick of the previous one.
                var persisted = FromMetadata(existing);
                if (persisted is not null && persisted.CapturedAtUtc > snapshot.CapturedAtUtc)
                {
                    return existing;
                }

                return WithProjection(existing, json);
            },
            ct
        );
    }

    /// <summary>Loads the persisted board for a conversation, or null when none has been stored.</summary>
    public static async Task<TodoBoardSnapshot?> LoadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        var metadata = await store.LoadMetadataAsync(threadId, ct);
        return FromMetadata(metadata);
    }

    /// <summary>
    ///     Reads bind property names case-insensitively (#590 review F-001): PR 2 pinned
    ///     <see cref="TodoTaskNode" />'s rows to camelCase for its transport channels, which also changed
    ///     the AT-REST shape — but blobs persisted by #586 carry PascalCase row keys (<c>"Id"</c>,
    ///     <c>"Status"</c>, …), and those properties are <c>required</c>, so a case-SENSITIVE read throws
    ///     and the tolerant catch below turns every pre-PR-2 board into "absent". Case-insensitive
    ///     binding reads both generations; new writes are camelCase-rowed from here on.
    ///     <see cref="PersistedSchemaVersion" /> is unaffected: it probes the raw document by exact key,
    ///     and the snapshot ROOT was never pinned.
    /// </summary>
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    ///     Extracts the board from already-loaded metadata. Store-agnostic: accepts the value whether it is
    ///     a native JSON string (in-memory) or a re-hydrated <see cref="JsonElement" /> (file / SQLite).
    /// </summary>
    public static TodoBoardSnapshot? FromMetadata(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata);
        if (json is null)
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<TodoBoardSnapshot>(json, ReadOptions);
            return snapshot is { SchemaVersion: <= CurrentSchemaVersion } ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the persisted schema version even when it is newer than this build understands.</summary>
    private static int PersistedSchemaVersion(ThreadMetadata? metadata)
    {
        var json = RawJson(metadata);
        if (json is null)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("SchemaVersion", out var value)
                && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : CurrentSchemaVersion;
        }
        catch (JsonException)
        {
            return 0; // corrupt — treat as absent, allow overwrite
        }
    }

    private static string? RawJson(ThreadMetadata? metadata)
    {
        if (metadata?.Properties is null || !metadata.Properties.TryGetValue(PropertyKey, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    ///     Returns <paramref name="existing" /> with the board written into its property bag.
    /// </summary>
    /// <remarks>
    ///     Takes a NON-NULL row by design — there is deliberately no create branch, so this method
    ///     cannot mint an ownership-less conversation even if a future caller forgets the guard in
    ///     <see cref="SaveAsync" />. <see cref="ThreadMetadata.LastUpdated" /> is deliberately NOT
    ///     bumped: it drives the sidebar's default ordering, and a read that persisted the board would
    ///     otherwise float the conversation to the top of the user's list. Matches
    ///     <c>ConversationUsageProjection</c>, which leaves it alone for the same reason.
    /// </remarks>
    private static ThreadMetadata WithProjection(ThreadMetadata existing, string boardJson)
    {
        // SetItem, never a wholesale replace: this bag is shared with the usage projection, the mode
        // binding, and the workspace binding.
        var properties = (existing.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
            PropertyKey,
            boardJson
        );

        return existing with
        {
            Properties = properties,
        };
    }
}
